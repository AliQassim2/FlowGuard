using System.Diagnostics;
using FlowGuard.Core.Shaping;
using FlowGuard.Core.Sites;

namespace FlowGuard.Core.Qos;

/// <summary>Bytes/s per direction for one app or site.</summary>
public readonly record struct KeyRates(double Down, double Up)
{
    public double Total => Down + Up;
}

/// <summary>
/// Smart QoS: priority numbers, Game mode, Movie mode and Focus mode.
/// <para>
/// Called once per sample with the rate of every app and tracked site. It sorts them into
/// <see cref="BandwidthAllocator"/> groups (games, video, apps without a number, numbered apps)
/// and gives each a <see cref="ShapingGroup.DynamicCap"/>, which the shaper combines with the
/// user's hard limit (stricter wins). Apps that need no cap get theirs removed.
/// </para>
/// <para>
/// Keys are executable names ("steam.exe") or sites ("site:youtube"): a browser's foreground
/// window and the process actually moving bytes are different PIDs of the same exe.
/// </para>
/// </summary>
public sealed class QosController
{
    /// <summary>Foreground owners that never steal focus (shell surfaces, lock screen).</summary>
    public static readonly IReadOnlySet<string> DefaultIgnoredForeground = new HashSet<string>(
        ["explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
         "SearchApp.exe", "LockApp.exe", "ShellHost.exe", "TextInputHost.exe", "dwm.exe"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Caps closer than this (relative) to the applied one aren't re-applied.</summary>
    private const double CapTolerance = 0.08;

    private readonly TrafficShaper _shaper;
    private readonly long _frequency;
    private readonly object _lock = new();
    private readonly Dictionary<string, byte> _ranks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _videoTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignoredForeground;
    private readonly Dictionary<string, long> _lastActive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _videoAverage = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RateLimit> _applied = new(StringComparer.OrdinalIgnoreCase);

    private QosOptions _options;
    private GameModeOptions _gameMode = new();
    private MovieModeOptions _movieMode = new();
    private bool _active;
    private string? _foregroundExe;
    private bool _focusMode;
    private long _lastEvaluate = long.MinValue;

    public QosController(TrafficShaper shaper, QosOptions? options = null,
        IEnumerable<string>? ignoredForeground = null, long? ticksPerSecond = null)
    {
        _shaper = shaper ?? throw new ArgumentNullException(nameof(shaper));
        _options = options ?? new QosOptions();
        _frequency = ticksPerSecond ?? Stopwatch.Frequency;
        _ignoredForeground = new HashSet<string>(ignoredForeground ?? DefaultIgnoredForeground,
            StringComparer.OrdinalIgnoreCase);
    }

    public QosOptions Options
    {
        get { lock (_lock) return _options; }
        set { lock (_lock) _options = value ?? throw new ArgumentNullException(nameof(value)); }
    }

    public GameModeOptions GameMode
    {
        get { lock (_lock) return _gameMode; }
        set { lock (_lock) _gameMode = value ?? throw new ArgumentNullException(nameof(value)); }
    }

    public MovieModeOptions MovieMode
    {
        get { lock (_lock) return _movieMode; }
        set { lock (_lock) _movieMode = value ?? throw new ArgumentNullException(nameof(value)); }
    }

    public bool FocusModeEnabled
    {
        get { lock (_lock) return _focusMode; }
        set { lock (_lock) _focusMode = value; }
    }

    /// <summary>Last reported foreground executable (tracked even while Focus Mode is off).</summary>
    public string? ForegroundExe
    {
        get { lock (_lock) return _foregroundExe; }
    }

    public bool IsThrottling
    {
        get { lock (_lock) return _active; }
    }

    // ---------------------------------------------------------------- rules

    /// <summary>Assigns a priority number (1 = most important); <see cref="Rank.None"/> removes it.</summary>
    public void SetRank(string key, int rank)
    {
        string k = TargetKey.Normalize(key);
        lock (_lock)
        {
            if (Rank.IsValid(rank))
                _ranks[k] = (byte)rank;
            else
                _ranks.Remove(k);
        }
    }

    /// <summary>The configured number, or <see cref="Rank.None"/> (ignores Focus Mode).</summary>
    public byte GetRank(string key)
    {
        lock (_lock)
            return _ranks.GetValueOrDefault(TargetKey.Normalize(key), Rank.None);
    }

    /// <summary>The number actually used: the foreground app counts as 1 while Focus Mode is on.</summary>
    public byte GetEffectiveRank(string key)
    {
        lock (_lock)
            return EffectiveRankLocked(key);
    }

    public IReadOnlyDictionary<string, byte> GetRanks()
    {
        lock (_lock)
            return new Dictionary<string, byte>(_ranks, StringComparer.OrdinalIgnoreCase);
    }

    public void SetGames(IEnumerable<string> exes) => ReplaceSet(_games, exes);

    public IReadOnlyList<string> GetGames()
    {
        lock (_lock)
            return _games.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Movie mode targets: sites ("site:youtube") and/or player apps ("vlc.exe").</summary>
    public void SetVideoTargets(IEnumerable<string> keys) => ReplaceSet(_videoTargets, keys);

    public IReadOnlyList<string> GetVideoTargets()
    {
        lock (_lock)
            return _videoTargets.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ReplaceSet(HashSet<string> set, IEnumerable<string> keys)
    {
        var normalized = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(TargetKey.Normalize).ToList();
        lock (_lock)
        {
            set.Clear();
            set.UnionWith(normalized);
        }
    }

    /// <summary>
    /// Reports the owner of the foreground window. Shell surfaces in the ignore list keep the
    /// previous foreground app, so clicking the taskbar doesn't demote the game you're playing.
    /// </summary>
    /// <returns>True if the tracked foreground executable changed.</returns>
    public bool ReportForeground(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName) || _ignoredForeground.Contains(exeName))
            return false;
        lock (_lock)
        {
            if (string.Equals(_foregroundExe, exeName, StringComparison.OrdinalIgnoreCase))
                return false;
            _foregroundExe = exeName;
            return true;
        }
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>Recomputes and applies every dynamic cap.</summary>
    /// <param name="rates">Rate per app / tracked site. Every running app should be present, even at 0.</param>
    /// <param name="nowTicks">Monotonic time in the controller's tick unit.</param>
    /// <param name="capacityDown">Line speed in bytes/s (0 = unknown: nothing is capped).</param>
    /// <param name="pingFactor">Ping guard factor (1 = no back-off), used while a game runs.</param>
    public QosStatus Evaluate(IReadOnlyDictionary<string, KeyRates> rates, long nowTicks,
        long capacityDown, long capacityUp, double pingFactor = 1)
    {
        lock (_lock)
        {
            QosOptions o = _options;
            GameModeOptions game = _gameMode;
            MovieModeOptions movie = _movieMode;
            double dt = _lastEvaluate == long.MinValue ? 0 : (nowTicks - _lastEvaluate) / (double)_frequency;
            _lastEvaluate = nowTicks;

            foreach ((string key, KeyRates r) in rates)
                if (r.Total > o.ActiveBytesPerSecond)
                    _lastActive[key] = nowTicks;

            var runningGames = game.Enabled
                ? rates.Keys.Where(_games.Contains).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            var activeVideo = movie.Enabled
                ? rates.Keys.Where(k => _videoTargets.Contains(k) && ActiveWithin(k, movie.HoldTime, nowTicks))
                    .Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            long videoReserve = VideoReserve(activeVideo, rates, movie, capacityDown, dt);
            bool gameRunning = runningGames.Count > 0;

            var down = new List<AllocationMember>(rates.Count);
            var up = new List<AllocationMember>(rates.Count);
            var protectedKeys = new HashSet<string>(runningGames.Concat(activeVideo), StringComparer.OrdinalIgnoreCase);
            foreach ((string key, KeyRates r) in rates)
            {
                bool isGame = gameRunning && _games.Contains(key);
                bool isVideo = !isGame && activeVideo.Contains(key, StringComparer.OrdinalIgnoreCase);
                byte rank = o.Enabled ? EffectiveRankLocked(key) : Rank.None;
                MemberClass cls = isGame ? MemberClass.Game
                    : isVideo ? MemberClass.Video
                    : rank != Rank.None ? MemberClass.Ranked
                    : MemberClass.Unmanaged;
                bool active = ActiveWithin(key, o.HoldTime, nowTicks);
                double reserveDown = isGame ? game.ReserveBytesPerSecond : isVideo ? videoReserve / (double)activeVideo.Count : 0;
                double reserveUp = isGame ? game.ReserveBytesPerSecond : 0;
                down.Add(new AllocationMember(key, cls, rank, r.Down, active, reserveDown));
                up.Add(new AllocationMember(key, cls, rank, r.Up, active, reserveUp));
            }

            double factor = gameRunning && game.PingGuard ? Math.Clamp(pingFactor, 0.1, 1) : 1;
            var settings = new AllocatorSettings(o.LineUtilization, o.Headroom, o.MinBytesPerSecond, game.SafetyMargin);
            Dictionary<string, long> capsDown = BandwidthAllocator.Allocate(capacityDown * factor, down, settings, gameRunning);
            Dictionary<string, long> capsUp = BandwidthAllocator.Allocate(capacityUp * factor, up, settings, gameRunning);

            var desired = new Dictionary<string, RateLimit>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in capsDown.Keys.Union(capsUp.Keys, StringComparer.OrdinalIgnoreCase))
                desired[key] = new RateLimit(capsDown.GetValueOrDefault(key), capsUp.GetValueOrDefault(key));
            Apply(desired);
            _active = desired.Count > 0;

            // Forget activity of apps that are gone.
            if (_lastActive.Count > rates.Count + 256)
                foreach (string key in _lastActive.Keys.Where(k => !rates.ContainsKey(k)).ToList())
                    _lastActive.Remove(key);

            var throttled = desired.Keys
                .Where(k => rates.TryGetValue(k, out KeyRates r) && r.Total > o.ActiveBytesPerSecond)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            var triggers = !_active ? [] : protectedKeys
                .Concat(down.Where(m => m.Class == MemberClass.Ranked && m.Active && !desired.ContainsKey(m.Key)).Select(m => m.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

            return new QosStatus(o.Enabled, _active, triggers, throttled, _focusMode, _foregroundExe,
                game.Enabled, runningGames, movie.Enabled, activeVideo, videoReserve, factor,
                capacityDown, capacityUp);
        }
    }

    /// <summary>The dynamic cap currently applied to <paramref name="key"/> (Unlimited when none).</summary>
    public RateLimit GetAppliedCap(string key)
    {
        lock (_lock)
            return _applied.GetValueOrDefault(TargetKey.Normalize(key), RateLimit.Unlimited);
    }

    private void Apply(Dictionary<string, RateLimit> desired)
    {
        foreach ((string key, _) in _applied)
            if (!desired.ContainsKey(key))
                _shaper.SetDynamicCap(key, RateLimit.Unlimited);

        var applied = new Dictionary<string, RateLimit>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, RateLimit cap) in desired)
        {
            RateLimit current = _applied.GetValueOrDefault(key, RateLimit.Unlimited);
            if (Differs(current.DownBytesPerSecond, cap.DownBytesPerSecond) ||
                Differs(current.UpBytesPerSecond, cap.UpBytesPerSecond))
            {
                _shaper.SetDynamicCap(key, cap);
                current = cap;
            }
            applied[key] = current;
        }
        _applied = applied;
    }

    private static bool Differs(long current, long wanted)
    {
        if ((current <= 0) != (wanted <= 0))
            return true;
        return wanted > 0 && Math.Abs(wanted - current) > current * CapTolerance;
    }

    private bool ActiveWithin(string key, TimeSpan hold, long now)
        => _lastActive.TryGetValue(key, out long t) && now - t <= (long)(hold.TotalSeconds * _frequency);

    /// <summary>
    /// The room kept waiting for video. Automatic: 1.5 × the video's average speed (about one
    /// quality step of margin so the buffer refills fast), at least 1 MB/s, at most 60 % of the line.
    /// </summary>
    private long VideoReserve(List<string> active, IReadOnlyDictionary<string, KeyRates> rates,
        MovieModeOptions movie, long capacity, double dt)
    {
        foreach (string key in _videoAverage.Keys.Where(k => !active.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _videoAverage.Remove(key);
        if (active.Count == 0)
            return 0;
        if (movie.ReserveBytesPerSecond > 0)
            return movie.ReserveBytesPerSecond;

        double total = 0;
        double weight = Math.Clamp(dt / 10.0, 0, 1); // ~10 s average: spans several chunks
        foreach (string key in active)
        {
            double rate = rates[key].Down;
            double avg = _videoAverage.TryGetValue(key, out double a) ? a + (rate - a) * weight : rate;
            _videoAverage[key] = avg;
            total += avg;
        }
        double reserve = Math.Max(1024 * 1024, total * 1.5);
        if (capacity > 0)
            reserve = Math.Min(reserve, capacity * 0.6);
        return (long)reserve;
    }

    private byte EffectiveRankLocked(string key)
    {
        if (_focusMode && _foregroundExe is not null &&
            string.Equals(key, _foregroundExe, StringComparison.OrdinalIgnoreCase))
            return Rank.Highest;
        return _ranks.GetValueOrDefault(key, Rank.None);
    }
}
