using System.Diagnostics;
using System.Net;
using FlowGuard.Core.Engine;
using FlowGuard.Core.Mapping;
using FlowGuard.Core.Processes;
using FlowGuard.Core.Qos;
using FlowGuard.Core.Settings;
using FlowGuard.Core.Shaping;
using FlowGuard.Core.Sites;
using FlowGuard.Core.Stats;

namespace FlowGuard.Core;

public sealed class FlowGuardOptions
{
    /// <summary>
    /// How often speeds are sampled, QoS is evaluated and a <see cref="TrafficSnapshot"/> is
    /// published (default 250 ms — also bounds how fast sharing reacts).
    /// </summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>EMA weight of the newest sample (1 = no smoothing).</summary>
    public double SmoothingFactor { get; init; } = 0.5;

    /// <summary>Processes with no traffic for this long are dropped from the table.</summary>
    public TimeSpan IdleEviction { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// After the last cap is lifted, stay in divert mode this long before falling back to sniff
    /// (avoids handle churn when QoS toggles caps on and off).
    /// </summary>
    public TimeSpan DivertHoldTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Re-check every N samples that cached PIDs still belong to the same process.</summary>
    public int ValidateEverySamples { get; init; } = 20;

    /// <summary>Ping once a second (and run the ping guard). Off in tests.</summary>
    public bool EnablePing { get; init; } = true;

    /// <summary>
    /// Where settings are loaded from at construction and auto-saved (debounced) after every
    /// change. Null disables persistence. The UI passes <see cref="SettingsStore.DefaultPath"/>.
    /// </summary>
    public string? SettingsPath { get; init; }

    public PacketEngineOptions Engine { get; init; } = new();
    public ShapingOptions Shaping { get; init; } = new();
    public QosOptions Qos { get; init; } = new();
}

/// <summary>
/// Composition root for the core: port→PID mapper, process table, shaper (with site
/// recognition), packet engine, QoS controller, line speed and ping monitors, foreground
/// watcher, sampler and settings persistence. This is the only type the UI needs to talk to.
/// All members are thread-safe.
/// </summary>
public sealed class FlowGuardService : IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FlowIdle = TimeSpan.FromMinutes(2);

    private readonly FlowGuardOptions _options;
    private readonly object _modeLock = new();
    private readonly object _focusLock = new();
    private readonly object _saveLock = new();
    private readonly object _configLock = new();
    private readonly PingGuard _pingGuard = new();
    private Timer? _timer;
    private Timer? _saveTimer;
    private ForegroundWatcher? _watcher;
    private int _ticking;
    private long _tickCount;
    private long _lastSampleTimestamp;
    private long _lastNeededDivertTimestamp;
    private long _lastInternetDown, _lastInternetUp;
    private volatile TrafficSnapshot _snapshot = TrafficSnapshot.Empty;
    private volatile UiSetting _ui = new();
    private List<string> _sites = [];
    private bool _gameRunning;
    private bool _suppressSave;
    private bool _disposed;

    public FlowGuardService(FlowGuardOptions? options = null)
    {
        _options = options ?? new FlowGuardOptions();
        Mapper = new PortPidMapper();
        Processes = new ProcessTable();
        Shaper = new TrafficShaper(Processes, _options.Shaping);
        Engine = new PacketEngine(Mapper, Shaper, _options.Engine);
        Qos = new QosController(Shaper, _options.Qos, OwnIgnoredForeground());
        Ping = new PingMonitor(IPAddress.Parse("1.1.1.1"));
        Ping.Measured += OnPing;
        Shaper.Injector = Engine;
        Engine.Faulted += ex => Error?.Invoke(this, ex);

        if (_options.SettingsPath is not null)
        {
            _suppressSave = true;
            try { ApplySettings(SettingsStore.Load(_options.SettingsPath)); }
            finally { _suppressSave = false; }
        }
        else
        {
            UpdateTrackedSites();
        }
    }

    public PortPidMapper Mapper { get; }
    public ProcessTable Processes { get; }
    public TrafficShaper Shaper { get; }
    public PacketEngine Engine { get; }
    public QosController Qos { get; }
    public LineCapacity Line { get; } = new();
    public PingMonitor Ping { get; }

    public bool IsRunning => Engine.IsRunning;

    /// <summary>True while the engine diverts (and can therefore delay/drop) packets.</summary>
    public bool IsShaping => Engine.IsRunning && !Engine.SniffOnly;

    public TrafficSnapshot Snapshot => _snapshot;

    /// <summary>Raised on a thread-pool thread after every sample. UI must marshal to its dispatcher.</summary>
    public event EventHandler<TrafficSnapshot>? SnapshotUpdated;

    /// <summary>Engine faults, mode-switch failures and settings I/O errors.</summary>
    public event EventHandler<Exception>? Error;

    /// <summary>Starts capture (requires Administrator).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Engine.IsRunning)
            return;

        _lastNeededDivertTimestamp = Stopwatch.GetTimestamp();
        Engine.Start(sniffOnly: !Shaper.RequiresDivert);
        _lastSampleTimestamp = Stopwatch.GetTimestamp();
        _lastInternetDown = Shaper.InternetDownBytes;
        _lastInternetUp = Shaper.InternetUpBytes;
        _timer ??= new Timer(Tick, null, _options.SampleInterval, _options.SampleInterval);
        if (_options.EnablePing)
            Ping.Start();
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        Ping.Stop();
        Engine.Stop();
    }

    // ------------------------------------------------------------------ hard limits

    /// <summary>Caps an executable or site (all processes of an exe share the cap). KB = 1024 bytes.</summary>
    public void SetLimit(string key, RateLimit limit)
    {
        Shaper.SetHardLimit(key, limit);
        EnsureMode();
        ScheduleSave();
    }

    public void SetLimitKBps(string key, double downKBps, double upKBps)
        => SetLimit(key, RateLimit.FromKBps(downKBps, upKBps));

    public void ClearLimit(string key) => SetLimit(key, RateLimit.Unlimited);

    public IReadOnlyDictionary<string, RateLimit> GetLimits() => Shaper.GetHardLimits();

    // ------------------------------------------------------------------ priorities

    /// <summary>Sets a priority number (1 = most important … 128); 0 removes it.</summary>
    public void SetRank(string key, int rank)
    {
        Qos.SetRank(key, rank);
        ScheduleSave();
    }

    public byte GetRank(string key) => Qos.GetRank(key);

    public IReadOnlyDictionary<string, byte> GetRanks() => Qos.GetRanks();

    public QosOptions QosOptions
    {
        get => Qos.Options;
        set
        {
            Qos.Options = value;
            ScheduleSave();
        }
    }

    // ------------------------------------------------------------------ sites

    /// <summary>Sites counted and prioritised separately from the browser carrying them.</summary>
    public IReadOnlyList<string> GetTrackedSites()
    {
        lock (_configLock)
            return _sites.ToList();
    }

    /// <summary>
    /// Starts tracking a site: a built-in key ("site:youtube"), or any domain or URL — a domain
    /// belonging to a built-in platform maps to that platform.
    /// </summary>
    /// <returns>The site key, or null when the text isn't a usable domain.</returns>
    public string? AddSite(string keyOrDomain)
    {
        string? key = ResolveSiteKey(keyOrDomain);
        if (key is null)
            return null;
        lock (_configLock)
        {
            if (!_sites.Contains(key, StringComparer.OrdinalIgnoreCase))
                _sites.Add(key);
        }
        UpdateTrackedSites();
        ScheduleSave();
        return key;
    }

    /// <summary>Stops tracking a site and forgets its priority and limit.</summary>
    public void RemoveSite(string key)
    {
        string k = TargetKey.Normalize(key);
        lock (_configLock)
            _sites.RemoveAll(s => s.Equals(k, StringComparison.OrdinalIgnoreCase));
        Qos.SetRank(k, Rank.None);
        Shaper.ClearHardLimit(k);
        UpdateTrackedSites();
        ScheduleSave();
    }

    public static string? ResolveSiteKey(string keyOrDomain)
    {
        if (string.IsNullOrWhiteSpace(keyOrDomain))
            return null;
        string text = keyOrDomain.Trim();
        if (TargetKey.IsSite(text))
            return SiteCatalog.Resolve(text)?.Key;
        string domain = TargetKey.NormalizeDomain(text);
        foreach (SiteDefinition s in SiteCatalog.BuiltIn)
            if (s.Matches(domain) || s.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase))
                return s.Key;
        return TargetKey.IsValidDomain(domain) ? TargetKey.ForSite(domain) : null;
    }

    /// <summary>
    /// Tracked = added by the user, plus the Movie mode platforms while Movie mode is on (when
    /// it's off, their traffic stays with the browser and its priority).
    /// </summary>
    private void UpdateTrackedSites()
    {
        IEnumerable<string> keys;
        IEnumerable<string> video = Qos.MovieMode.Enabled ? Qos.GetVideoTargets().Where(TargetKey.IsSite) : [];
        lock (_configLock)
            keys = _sites.Concat(video).ToList();
        Shaper.SetTrackedSites(keys.Select(SiteCatalog.Resolve).OfType<SiteDefinition>());
    }

    // ------------------------------------------------------------------ modes

    public GameModeOptions GameMode
    {
        get => Qos.GameMode;
        set
        {
            Qos.GameMode = value;
            if (!value.Enabled) _pingGuard.Reset();
            ScheduleSave();
        }
    }

    public MovieModeOptions MovieMode
    {
        get => Qos.MovieMode;
        set
        {
            bool toggled = Qos.MovieMode.Enabled != value.Enabled;
            Qos.MovieMode = value;
            if (toggled)
                UpdateTrackedSites();
            ScheduleSave();
        }
    }

    public IReadOnlyList<string> GetGames() => Qos.GetGames();

    public void SetGames(IEnumerable<string> exes)
    {
        Qos.SetGames(exes.Where(e => !TargetKey.IsSite(e)));
        ScheduleSave();
    }

    public IReadOnlyList<string> GetVideoTargets() => Qos.GetVideoTargets();

    public void SetVideoTargets(IEnumerable<string> keys)
    {
        Qos.SetVideoTargets(keys.Select(k => TargetKey.IsSite(k) ? ResolveSiteKey(k) : TargetKey.Normalize(k)).OfType<string>());
        UpdateTrackedSites();
        ScheduleSave();
    }

    /// <summary>Smart Focus Mode: the foreground window's executable counts as priority 1.</summary>
    public bool FocusModeEnabled
    {
        get => Qos.FocusModeEnabled;
        set
        {
            lock (_focusLock)
            {
                if (value && _watcher is null && !_disposed)
                {
                    try
                    {
                        _watcher = new ForegroundWatcher();
                        _watcher.ForegroundChanged += (_, exe) => Qos.ReportForeground(exe);
                        Qos.ReportForeground(_watcher.CurrentExe);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(this, ex);
                        return;
                    }
                }
                else if (!value && _watcher is not null)
                {
                    _watcher.Dispose();
                    _watcher = null;
                }
                Qos.FocusModeEnabled = value;
            }
            ScheduleSave();
        }
    }

    // ------------------------------------------------------------------ line and ping

    /// <summary>Manual line speed in bytes/s (0 = learn automatically).</summary>
    public void SetManualLineSpeed(long downBytesPerSecond, long upBytesPerSecond)
    {
        Line.ManualDown = Math.Max(0, downBytesPerSecond);
        Line.ManualUp = Math.Max(0, upBytesPerSecond);
        ScheduleSave();
    }

    public void ResetLearnedLineSpeed()
    {
        Line.ResetLearned();
        ScheduleSave();
    }

    public IPAddress PingTarget
    {
        get => Ping.Target;
        set
        {
            Ping.Target = value;
            _pingGuard.Reset();
            ScheduleSave();
        }
    }

    private void OnPing(PingStatus status)
    {
        lock (_pingGuard)
        {
            if (_gameRunning)
                _pingGuard.Update(status);
            else
                _pingGuard.Reset();
        }
    }

    /// <summary>UI-only preferences, persisted alongside the core settings.</summary>
    public UiSetting UiSettings
    {
        get => _ui;
        set
        {
            _ui = value ?? new UiSetting();
            ScheduleSave();
        }
    }

    // ------------------------------------------------------------------ settings

    public FlowGuardSettings GetSettings()
    {
        QosOptions q = Qos.Options;
        GameModeOptions g = Qos.GameMode;
        MovieModeOptions m = Qos.MovieMode;
        var s = new FlowGuardSettings
        {
            FocusMode = Qos.FocusModeEnabled,
            Ui = _ui,
            Sites = GetTrackedSites().ToList(),
            PingTarget = Ping.Target.ToString(),
            AutoThrottle = new AutoThrottleSetting
            {
                Enabled = q.Enabled,
                FallbackKBps = q.MinBytesPerSecond / 1024.0,
                HoldSeconds = q.HoldTime.TotalSeconds,
            },
            Line = new LineSetting
            {
                DownKBps = Line.ManualDown / 1024.0,
                UpKBps = Line.ManualUp / 1024.0,
                LearnedDownKBps = Math.Round(Line.LearnedDown / 1024.0),
                LearnedUpKBps = Math.Round(Line.LearnedUp / 1024.0),
            },
            GameMode = new GameModeSetting
            {
                Enabled = g.Enabled,
                Apps = Qos.GetGames().ToList(),
                ReserveKBps = g.ReserveBytesPerSecond / 1024.0,
                MarginPercent = Math.Round(g.SafetyMargin * 100, 1),
                PingGuard = g.PingGuard,
            },
            MovieMode = new MovieModeSetting
            {
                Enabled = m.Enabled,
                Targets = Qos.GetVideoTargets().ToList(),
                ReserveKBps = m.ReserveBytesPerSecond / 1024.0,
                HoldSeconds = m.HoldTime.TotalSeconds,
            },
        };
        foreach ((string key, RateLimit l) in Shaper.GetHardLimits())
            s.Limits[key] = new LimitSetting { DownKBps = l.DownBytesPerSecond / 1024.0, UpKBps = l.UpBytesPerSecond / 1024.0 };
        foreach ((string key, byte rank) in Qos.GetRanks())
            s.Priorities[key] = rank;
        return s;
    }

    /// <summary>Replaces the live configuration with <paramref name="settings"/>.</summary>
    public void ApplySettings(FlowGuardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (string key in Shaper.GetHardLimits().Keys)
            if (!settings.Limits.ContainsKey(key))
                Shaper.ClearHardLimit(key);
        foreach ((string key, LimitSetting l) in settings.Limits)
            Shaper.SetHardLimit(key, RateLimit.FromKBps(l.DownKBps, l.UpKBps));

        foreach (string key in Qos.GetRanks().Keys)
            if (!settings.Priorities.ContainsKey(key))
                Qos.SetRank(key, Rank.None);
        foreach ((string key, int rank) in settings.Priorities)
            Qos.SetRank(key, rank);

        AutoThrottleSetting a = settings.AutoThrottle;
        Qos.Options = Qos.Options with
        {
            Enabled = a.Enabled,
            MinBytesPerSecond = (long)(Math.Clamp(a.FallbackKBps, 1, 100_000) * 1024),
            HoldTime = TimeSpan.FromSeconds(Math.Clamp(a.HoldSeconds, 0, 600)),
        };

        LineSetting line = settings.Line;
        Line.ManualDown = (long)(Math.Max(0, line.DownKBps) * 1024);
        Line.ManualUp = (long)(Math.Max(0, line.UpKBps) * 1024);
        Line.Restore((long)(Math.Max(0, line.LearnedDownKBps) * 1024), (long)(Math.Max(0, line.LearnedUpKBps) * 1024));

        GameModeSetting g = settings.GameMode;
        Qos.GameMode = new GameModeOptions
        {
            Enabled = g.Enabled,
            ReserveBytesPerSecond = (long)(Math.Clamp(g.ReserveKBps, 0, 100_000) * 1024),
            SafetyMargin = Math.Clamp(g.MarginPercent, 0, 50) / 100,
            PingGuard = g.PingGuard,
        };
        Qos.SetGames(g.Apps);

        MovieModeSetting m = settings.MovieMode;
        Qos.MovieMode = new MovieModeOptions
        {
            Enabled = m.Enabled,
            ReserveBytesPerSecond = (long)(Math.Clamp(m.ReserveKBps, 0, 1_000_000) * 1024),
            HoldTime = TimeSpan.FromSeconds(Math.Clamp(m.HoldSeconds, 1, 600)),
        };
        Qos.SetVideoTargets(m.Targets.Select(k => TargetKey.IsSite(k) ? ResolveSiteKey(k) : k).OfType<string>());

        lock (_configLock)
            _sites = settings.Sites.Select(ResolveSiteKey).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        UpdateTrackedSites();

        Ping.Target = IPAddress.TryParse(settings.PingTarget, out IPAddress? target) ? target : IPAddress.Parse("1.1.1.1");

        _ui = settings.Ui ?? new UiSetting();
        FocusModeEnabled = settings.FocusMode;
        EnsureMode();
        ScheduleSave();
    }

    /// <summary>Writes settings now (also happens automatically, debounced, after each change).</summary>
    public void SaveSettings()
    {
        if (_options.SettingsPath is null)
            return;
        lock (_saveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            try
            {
                SettingsStore.Save(GetSettings(), _options.SettingsPath);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }
        }
    }

    private void ScheduleSave()
    {
        if (_options.SettingsPath is null || _suppressSave || _disposed)
            return;
        lock (_saveLock)
        {
            _saveTimer ??= new Timer(_ => SaveSettings());
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    // ------------------------------------------------------------------ sampler

    private void Tick(object? _)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
            return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Math.Max(1e-3, Stopwatch.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds);
            List<ProcessTrafficState> live = Sample(now, elapsed);

            long inetDown = Shaper.InternetDownBytes, inetUp = Shaper.InternetUpBytes;
            if (Line.Observe(inetDown - _lastInternetDown, inetUp - _lastInternetUp, elapsed))
                ScheduleSave();
            _lastInternetDown = inetDown;
            _lastInternetUp = inetUp;

            var rates = new Dictionary<string, KeyRates>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessTrafficState s in live)
            {
                if (s.Pid == ProcessResolver.UnattributedPid)
                    continue;
                KeyRates r = rates.GetValueOrDefault(s.Name);
                rates[s.Name] = new KeyRates(r.Down + s.DownRate, r.Up + s.UpRate);
            }

            double factor;
            lock (_pingGuard)
                factor = _pingGuard.Factor;
            QosStatus qos = Qos.Evaluate(rates, now, Line.Down, Line.Up, factor);
            _gameRunning = qos.IsGameActive;

            Publish(live, qos);
            EnsureMode();
            if (++_tickCount % 40 == 0)
                Shaper.Sites.Prune(FlowIdle);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Updates per-process rates, evicts dead/idle processes, returns the survivors.</summary>
    private List<ProcessTrafficState> Sample(long now, double elapsed)
    {
        _lastSampleTimestamp = now;

        bool validate = _options.ValidateEverySamples > 0 && _tickCount % _options.ValidateEverySamples == 0;
        long idleTicks = (long)(_options.IdleEviction.TotalSeconds * Stopwatch.Frequency);
        double alpha = Math.Clamp(_options.SmoothingFactor, 0.01, 1);

        var live = new List<ProcessTrafficState>();
        foreach (ProcessTrafficState s in Processes.ToArray())
        {
            long down = s.TotalDownBytes;
            long up = s.TotalUpBytes;
            long dDown = down - s.SampledDown;
            long dUp = up - s.SampledUp;
            s.SampledDown = down;
            s.SampledUp = up;

            s.DownRate = Smooth(s.DownRate, dDown / elapsed, alpha);
            s.UpRate = Smooth(s.UpRate, dUp / elapsed, alpha);

            if (dDown > 0 || dUp > 0)
                s.LastActiveTimestamp = now;
            else if (!s.IsSite && now - s.LastActiveTimestamp > idleTicks)
            {
                Processes.Remove(s);
                continue;
            }

            if (validate && s.Pid > ProcessResolver.SystemPid && !IsSameProcess(s))
            {
                Processes.Remove(s); // exited, or PID reused — next packet re-resolves
                continue;
            }
            live.Add(s);
        }
        return live;
    }

    private void Publish(List<ProcessTrafficState> live, QosStatus qos)
    {
        var list = new List<ProcessTrafficInfo>(live.Count);
        double totalDown = 0, totalUp = 0;
        string? foreground = qos.FocusModeEnabled ? qos.ForegroundExe : null;

        foreach (ProcessTrafficState s in live)
        {
            // Look the group up by name: a state only binds lazily on its next packet.
            ShapingGroup? g = s.Pid == ProcessResolver.UnattributedPid ? null : Shaper.FindGroup(s.Name);
            list.Add(new ProcessTrafficInfo(
                s.Pid, s.Name, s.Identity.Path, s.DownRate, s.UpRate,
                s.TotalDownBytes, s.TotalUpBytes, s.DroppedPackets,
                EffectiveLimit: g?.EffectiveLimit ?? RateLimit.Unlimited,
                HardLimit: g?.HardLimit ?? RateLimit.Unlimited,
                Rank: Qos.GetEffectiveRank(s.Name),
                IsForeground: foreground is not null && string.Equals(s.Name, foreground, StringComparison.OrdinalIgnoreCase),
                IsQosThrottled: g is not null && !g.DynamicCap.IsUnlimited,
                IsSite: s.IsSite));
            totalDown += s.DownRate;
            totalUp += s.UpRate;
        }

        list.Sort(static (a, b) =>
            (b.DownBytesPerSecond + b.UpBytesPerSecond).CompareTo(a.DownBytesPerSecond + a.UpBytesPerSecond));

        var snapshot = new TrafficSnapshot(DateTime.UtcNow, list, totalDown, totalUp, IsShaping, Shaper.QueuedBytes,
            qos, Ping.Status);
        _snapshot = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);
    }

    private static double Smooth(double previous, double sample, double alpha)
    {
        double v = alpha * sample + (1 - alpha) * previous;
        return sample == 0 && v < 512 ? 0 : v; // snap to zero so idle detection / UI filtering work
    }

    private static bool IsSameProcess(ProcessTrafficState s)
    {
        long? creation = ProcessResolver.GetCreationTime(s.Pid);
        if (creation is null)
            return false;
        long known = s.Identity.CreationTime;
        return creation == 0 || known == 0 || creation == known;
    }

    private static IEnumerable<string> OwnIgnoredForeground()
    {
        // Never let FlowGuard itself (or its dev host) become the focused app.
        string? own = Environment.ProcessPath is { } p ? System.IO.Path.GetFileName(p) : null;
        return own is null
            ? QosController.DefaultIgnoredForeground
            : QosController.DefaultIgnoredForeground.Append(own);
    }

    /// <summary>Divert while any cap is active; fall back to sniff after <see cref="FlowGuardOptions.DivertHoldTime"/>.</summary>
    private void EnsureMode()
    {
        lock (_modeLock)
        {
            if (!Engine.IsRunning)
                return;

            long now = Stopwatch.GetTimestamp();
            bool needed = Shaper.RequiresDivert;
            if (needed)
                _lastNeededDivertTimestamp = now;

            try
            {
                if (needed && Engine.SniffOnly)
                    Engine.Restart(sniffOnly: false);
                else if (!needed && !Engine.SniffOnly &&
                         Stopwatch.GetElapsedTime(_lastNeededDivertTimestamp, now) > _options.DivertHoldTime)
                    Engine.Restart(sniffOnly: true);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_saveLock)
        {
            bool pending = _saveTimer is not null;
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (pending && _options.SettingsPath is not null)
            {
                try { SettingsStore.Save(GetSettings(), _options.SettingsPath); }
                catch (Exception ex) { Error?.Invoke(this, ex); }
            }
        }

        _disposed = true;
        Stop();
        lock (_focusLock)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        Ping.Dispose();
        Shaper.Dispose();
        Engine.Dispose();
        Mapper.Dispose();
    }
}
