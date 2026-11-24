using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlowGuard.Core;
using FlowGuard.Core.Processes;
using FlowGuard.Core.Qos;
using FlowGuard.Core.Settings;
using FlowGuard.Core.Sites;
using FlowGuard.Core.Stats;
using FlowGuard.UI.Services;

namespace FlowGuard.UI.ViewModels;

/// <summary>
/// Main window / tray state. Polls <see cref="FlowGuardService.Snapshot"/> once a second on the
/// UI thread and diffs it into <see cref="Apps"/> so rows (and their editors) are reused.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly FlowGuardService _service;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, AppRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessIdentity> _socketOwners = [];
    private bool _loading;
    private bool _forceSync;
    private bool _wasThrottling, _wasGame, _wasVideo;
    private long _loggedLineDown, _loggedLineUp;
    private string _loggedThrottled = "";

    public MainViewModel(FlowGuardService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;

        _loading = true;
        QosOptions q = service.QosOptions;
        _sharingEnabled = q.Enabled;
        _minKBps = q.MinBytesPerSecond / 1024.0;
        _holdSeconds = q.HoldTime.TotalSeconds;
        _focusModeEnabled = service.FocusModeEnabled;

        GameModeOptions g = service.GameMode;
        _gameModeEnabled = g.Enabled;
        _gameReserveKBps = g.ReserveBytesPerSecond / 1024.0;
        _gameMarginPercent = g.SafetyMargin * 100;
        _pingGuardEnabled = g.PingGuard;
        _pingTargetText = service.PingTarget.ToString();

        MovieModeOptions m = service.MovieMode;
        _movieModeEnabled = m.Enabled;
        _movieReserveAuto = m.ReserveBytesPerSecond <= 0;
        _movieReserveKBps = m.ReserveBytesPerSecond > 0 ? m.ReserveBytesPerSecond / 1024.0 : 3500;
        _movieHoldSeconds = m.HoldTime.TotalSeconds;

        _lineDownText = Units.LimitText(service.Line.ManualDown);
        _lineUpText = Units.LimitText(service.Line.ManualUp);

        UiSetting ui = service.UiSettings;
        _showIdle = ui.ShowIdleProcesses;
        _closeToTray = ui.CloseToTray;
        _startMinimized = ui.StartMinimized;

        HashSet<string> targets = service.GetVideoTargets().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SiteDefinition site in SiteCatalog.BuiltIn.Where(s => s.IsVideo))
            Platforms.Add(new PlatformOption(site, targets.Contains(site.Key), OnPlatformsChanged));
        _loading = false;

        ReloadLists();
        service.Error += OnServiceError;

        // Raised on the capture thread: log off it, once per domain.
        var recognised = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        service.Shaper.Sites.Recognised += (site, domain) =>
        {
            if (recognised.Count < 1000 && recognised.TryAdd(domain, 0))
                ThreadPool.QueueUserWorkItem(_ => Log.Info($"Website recognised: {domain} = {site.DisplayName}"));
        };
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
    }

    public ObservableCollection<AppRowViewModel> Apps { get; } = [];

    public string SettingsFilePath => SettingsStore.DefaultPath;
    public string LogFilePath => Log.FilePath;

    /// <summary>Set by the view: true while the window is shown (list work is skipped while in the tray).</summary>
    public bool IsWindowVisible { get; set; }

    /// <summary>Set by the view: returns true while the user is pointing at or editing the list (reordering would move rows under the cursor).</summary>
    public Func<bool>? IsUserInteracting { get; set; }

    // ------------------------------------------------------------------ header / status

    [ObservableProperty] private string _totalDownText = "0 B/s";
    [ObservableProperty] private string _totalUpText = "0 B/s";
    [ObservableProperty] private string _lineText = "";
    [ObservableProperty] private string _pingText = "—";
    [ObservableProperty] private string _pingDetailText = "";
    [ObservableProperty] private int _pingLevel;
    [ObservableProperty] private string _engineStatusText = "Starting…";
    [ObservableProperty] private string _engineDetailText = "";
    [ObservableProperty] private string _sharingStatusText = "";
    [ObservableProperty] private string _sharingDetailText = "";
    [ObservableProperty] private bool _isThrottling;
    [ObservableProperty] private string _gameStatusText = "";
    [ObservableProperty] private string _gameDetailText = "";
    [ObservableProperty] private bool _isGameActive;
    [ObservableProperty] private string _movieStatusText = "";
    [ObservableProperty] private string _movieDetailText = "";
    [ObservableProperty] private bool _isVideoActive;
    [ObservableProperty] private string _focusDetailText = "";
    [ObservableProperty] private string _learnedLineText = "";
    [ObservableProperty] private string _listSummary = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isListEmpty = true;
    [ObservableProperty] private string _trayToolTip = "FlowGuard";
    [ObservableProperty] private bool _isErrorOpen;
    [ObservableProperty] private string _errorTitle = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isSortPaused;

    // ------------------------------------------------------------------ engine

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseMenuText))]
    private bool _engineEnabled;

    partial void OnEngineEnabledChanged(bool value)
    {
        if (_loading)
            return;
        if (value)
            StartEngine();
        else
        {
            _service.Stop();
            Log.Info("Engine paused by user");
        }
        Refresh();
    }

    public string PauseMenuText => EngineEnabled ? "Pause FlowGuard" : "Resume FlowGuard";

    public void Start()
    {
        StartEngine();
        _timer.Start();
        Refresh();
    }

    private void StartEngine()
    {
        try
        {
            Log.Info($"Starting engine ({(_service.Shaper.RequiresDivert ? "limiting" : "monitor-only")} mode)");
            _service.Start();
            Log.Info($"Engine started ({(_service.IsShaping ? "limiting" : "monitoring")})");
            SetQuietly(() => EngineEnabled = true);
        }
        catch (Exception ex)
        {
            Log.Error("Engine start", ex);
            ShowError("Packet capture couldn't start", ex.Message);
            SetQuietly(() => EngineEnabled = false);
        }
    }

    // ------------------------------------------------------------------ smart sharing

    [ObservableProperty] private bool _sharingEnabled;
    [ObservableProperty] private double? _minKBps;
    [ObservableProperty] private double? _holdSeconds;
    [ObservableProperty] private bool _focusModeEnabled;

    partial void OnSharingEnabledChanged(bool value)
    {
        if (_loading)
            return;
        _service.QosOptions = _service.QosOptions with { Enabled = value };
        Log.Info($"Smart sharing {(value ? "on" : "off")}");
        Refresh();
    }

    partial void OnMinKBpsChanged(double? value)
    {
        if (_loading)
            return;
        if (value is >= 1)
            _service.QosOptions = _service.QosOptions with { MinBytesPerSecond = (long)(value.Value * 1024) };
        else
            SetQuietly(() => MinKBps = _service.QosOptions.MinBytesPerSecond / 1024.0);
    }

    partial void OnHoldSecondsChanged(double? value)
    {
        if (_loading)
            return;
        if (value is >= 0 and <= 600)
            _service.QosOptions = _service.QosOptions with { HoldTime = TimeSpan.FromSeconds(value.Value) };
        else
            SetQuietly(() => HoldSeconds = _service.QosOptions.HoldTime.TotalSeconds);
    }

    partial void OnFocusModeEnabledChanged(bool value)
    {
        if (_loading)
            return;
        _service.FocusModeEnabled = value;
        if (_service.FocusModeEnabled != value) // watcher failed to start; the error is already shown
            SetQuietly(() => FocusModeEnabled = _service.FocusModeEnabled);
        Log.Info($"Focus Mode {(FocusModeEnabled ? "on" : "off")}");
        Refresh();
    }

    // ------------------------------------------------------------------ priority order

    /// <summary>Every numbered app and site, most important first.</summary>
    public ObservableCollection<RankItem> RankItems { get; } = [];

    [ObservableProperty] private bool _hasRankItems;

    private void ReloadRanks()
    {
        List<RankItem> items = _service.GetRanks()
            .OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new RankItem(p.Key, p.Value)).ToList();
        RankItems.Clear();
        foreach (RankItem i in items)
            RankItems.Add(i);
        HasRankItems = RankItems.Count > 0;
    }

    /// <summary>Called by rows after their number changed.</summary>
    private void OnRulesChanged()
    {
        ReloadRanks();
        Refresh();
    }

    /// <summary>
    /// Moves an entry in the priority order (drag and drop, or the arrow buttons). The numbers in
    /// use stay the same; they are handed out again in the new order.
    /// </summary>
    public void MoveRank(int from, int to)
    {
        if (from < 0 || from >= RankItems.Count || to < 0 || to >= RankItems.Count || from == to)
            return;
        List<byte> numbers = RankItems.Select(i => i.Rank).Order().ToList();
        RankItems.Move(from, to);

        // Neighbours with the same number would keep sharing: give the moved one its own place.
        for (int i = 1; i < numbers.Count; i++)
            if (numbers[i] <= numbers[i - 1])
                numbers[i] = (byte)Math.Min(Rank.Lowest, numbers[i - 1] + 1);

        for (int i = 0; i < RankItems.Count; i++)
        {
            RankItem item = RankItems[i];
            if (item.Rank != numbers[i])
            {
                _service.SetRank(item.Key, numbers[i]);
                item.Rank = numbers[i];
            }
        }
        Log.Info("Priority order: " + string.Join(", ", RankItems.Select(i => $"{i.Rank} {i.Key}")));
        foreach (AppRowViewModel row in _rows.Values)
            row.ReloadRank();
        Refresh();
    }

    [RelayCommand]
    private void MoveRankUp(RankItem item) => MoveRank(RankItems.IndexOf(item), RankItems.IndexOf(item) - 1);

    [RelayCommand]
    private void MoveRankDown(RankItem item) => MoveRank(RankItems.IndexOf(item), RankItems.IndexOf(item) + 1);

    [RelayCommand]
    private void ClearRank(RankItem item)
    {
        _service.SetRank(item.Key, Rank.None);
        if (_rows.TryGetValue(item.Key, out AppRowViewModel? row))
            row.ReloadRank();
        ReloadRanks();
        Refresh();
    }

    // ------------------------------------------------------------------ game mode

    [ObservableProperty] private bool _gameModeEnabled;
    [ObservableProperty] private double? _gameReserveKBps;
    [ObservableProperty] private double? _gameMarginPercent;
    [ObservableProperty] private bool _pingGuardEnabled;
    [ObservableProperty] private string _pingTargetText;
    [ObservableProperty] private string _newGameText = "";

    public ObservableCollection<NamedItem> Games { get; } = [];

    /// <summary>Running apps offered when adding a game.</summary>
    public ObservableCollection<string> RunningApps { get; } = [];

    partial void OnGameModeEnabledChanged(bool value)
    {
        if (_loading)
            return;
        _service.GameMode = _service.GameMode with { Enabled = value };
        Log.Info($"Game mode {(value ? "on" : "off")}");
        Refresh();
    }

    partial void OnGameReserveKBpsChanged(double? value)
    {
        if (_loading)
            return;
        if (value is >= 0 and <= 100_000)
            _service.GameMode = _service.GameMode with { ReserveBytesPerSecond = (long)(value.Value * 1024) };
        else
            SetQuietly(() => GameReserveKBps = _service.GameMode.ReserveBytesPerSecond / 1024.0);
    }

    partial void OnGameMarginPercentChanged(double? value)
    {
        if (_loading)
            return;
        if (value is >= 0 and <= 50)
            _service.GameMode = _service.GameMode with { SafetyMargin = value.Value / 100 };
        else
            SetQuietly(() => GameMarginPercent = _service.GameMode.SafetyMargin * 100);
    }

    partial void OnPingGuardEnabledChanged(bool value)
    {
        if (_loading)
            return;
        _service.GameMode = _service.GameMode with { PingGuard = value };
    }

    partial void OnPingTargetTextChanged(string value)
    {
        if (_loading)
            return;
        if (IPAddress.TryParse(value?.Trim(), out IPAddress? ip))
        {
            _service.PingTarget = ip;
            Log.Info($"Ping target = {ip}");
        }
        else
            SetQuietly(() => PingTargetText = _service.PingTarget.ToString());
    }

    [RelayCommand]
    private void AddGame()
    {
        string name = (NewGameText ?? "").Trim();
        if (name.Length == 0)
            return;
        string key = ProcessResolver.NormalizeExeName(name);
        _service.SetGames(_service.GetGames().Append(key));
        Log.Info($"Game added: {key}");
        NewGameText = "";
        ReloadLists();
        Refresh();
    }

    [RelayCommand]
    private void RemoveGame(NamedItem item)
    {
        _service.SetGames(_service.GetGames().Where(g => !g.Equals(item.Key, StringComparison.OrdinalIgnoreCase)));
        ReloadLists();
        Refresh();
    }

    /// <summary>Row context menu: add or remove an app from Game mode.</summary>
    public void ToggleGame(string key)
    {
        IReadOnlyList<string> games = _service.GetGames();
        _service.SetGames(games.Contains(key, StringComparer.OrdinalIgnoreCase)
            ? games.Where(g => !g.Equals(key, StringComparison.OrdinalIgnoreCase))
            : games.Append(key));
        ReloadLists();
        Refresh();
    }

    // ------------------------------------------------------------------ movie mode

    [ObservableProperty] private bool _movieModeEnabled;
    [ObservableProperty] private bool _movieReserveAuto;
    [ObservableProperty] private double? _movieReserveKBps;
    [ObservableProperty] private double? _movieHoldSeconds;
    [ObservableProperty] private string _newVideoTargetText = "";

    public ObservableCollection<PlatformOption> Platforms { get; } = [];

    /// <summary>Video targets that aren't built-in platforms: custom sites and player apps.</summary>
    public ObservableCollection<NamedItem> CustomVideoTargets { get; } = [];

    partial void OnMovieModeEnabledChanged(bool value)
    {
        if (_loading)
            return;
        _service.MovieMode = _service.MovieMode with { Enabled = value };
        Log.Info($"Movie mode {(value ? "on" : "off")}");
        Refresh();
    }

    partial void OnMovieReserveAutoChanged(bool value) => ApplyMovieReserve();

    partial void OnMovieReserveKBpsChanged(double? value) => ApplyMovieReserve();

    private void ApplyMovieReserve()
    {
        if (_loading)
            return;
        if (!MovieReserveAuto && MovieReserveKBps is not (>= 64 and <= 1_000_000))
        {
            SetQuietly(() => MovieReserveKBps = 3500);
        }
        long reserve = MovieReserveAuto ? 0 : (long)(MovieReserveKBps!.Value * 1024);
        _service.MovieMode = _service.MovieMode with { ReserveBytesPerSecond = reserve };
    }

    partial void OnMovieHoldSecondsChanged(double? value)
    {
        if (_loading)
            return;
        if (value is >= 1 and <= 600)
            _service.MovieMode = _service.MovieMode with { HoldTime = TimeSpan.FromSeconds(value.Value) };
        else
            SetQuietly(() => MovieHoldSeconds = _service.MovieMode.HoldTime.TotalSeconds);
    }

    private void OnPlatformsChanged()
    {
        if (_loading)
            return;
        SaveVideoTargets(Platforms.Where(p => p.IsSelected).Select(p => p.Key).Concat(CustomVideoTargets.Select(c => c.Key)));
    }

    [RelayCommand]
    private void AddVideoTarget()
    {
        string text = (NewVideoTargetText ?? "").Trim();
        if (text.Length == 0)
            return;
        string? key = text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? ProcessResolver.NormalizeExeName(text)
            : FlowGuardService.ResolveSiteKey(text);
        if (key is null)
        {
            ShowError("That doesn't look like a website", "Type a web address such as example.com, or an app such as vlc.exe.");
            return;
        }
        SaveVideoTargets(_service.GetVideoTargets().Append(key));
        NewVideoTargetText = "";
    }

    [RelayCommand]
    private void RemoveVideoTarget(NamedItem item)
        => SaveVideoTargets(_service.GetVideoTargets().Where(k => !k.Equals(item.Key, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Row context menu: add or remove an app or site from Movie mode.</summary>
    public void ToggleVideo(string key)
    {
        IReadOnlyList<string> targets = _service.GetVideoTargets();
        SaveVideoTargets(targets.Contains(key, StringComparer.OrdinalIgnoreCase)
            ? targets.Where(k => !k.Equals(key, StringComparison.OrdinalIgnoreCase))
            : targets.Append(key));
    }

    private void SaveVideoTargets(IEnumerable<string> keys)
    {
        _service.SetVideoTargets(keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        Log.Info("Movie mode targets: " + string.Join(", ", _service.GetVideoTargets()));
        ReloadLists();
        _forceSync = true;
        Refresh();
    }

    // ------------------------------------------------------------------ sites

    public ObservableCollection<NamedItem> TrackedSites { get; } = [];

    /// <summary>Built-in platforms offered in the "add site" box.</summary>
    public IReadOnlyList<string> SiteSuggestions { get; } = SiteCatalog.BuiltIn.Select(s => s.DisplayName).ToList();

    [ObservableProperty] private string _newSiteText = "";

    [RelayCommand]
    private void AddSite()
    {
        string text = (NewSiteText ?? "").Trim();
        if (text.Length == 0)
            return;
        string? key = _service.AddSite(text);
        if (key is null)
        {
            ShowError("That doesn't look like a website", "Type a web address such as example.com, or pick a platform from the list.");
            return;
        }
        Log.Info($"Site tracked: {key}");
        NewSiteText = "";
        ReloadLists();
        _forceSync = true;
        Refresh();
    }

    [RelayCommand]
    private void RemoveSite(NamedItem item) => RemoveSiteKey(item.Key);

    public void RemoveSiteKey(string key)
    {
        _service.RemoveSite(key);
        _rows.Remove(key);
        ReloadLists();
        ReloadRanks();
        _forceSync = true;
        Refresh();
    }

    // ------------------------------------------------------------------ line speed

    [ObservableProperty] private string _lineDownText;
    [ObservableProperty] private string _lineUpText;

    partial void OnLineDownTextChanged(string value) => ApplyLine();

    partial void OnLineUpTextChanged(string value) => ApplyLine();

    private void ApplyLine()
    {
        if (_loading)
            return;
        bool okDown = Units.TryParseLimitKBps(LineDownText, out double down);
        bool okUp = Units.TryParseLimitKBps(LineUpText, out double up);
        if (okDown && okUp)
        {
            _service.SetManualLineSpeed((long)(down * 1024), (long)(up * 1024));
            Log.Info($"Line speed = {(down > 0 ? $"{down:0} KB/s" : "auto")} down, {(up > 0 ? $"{up:0} KB/s" : "auto")} up");
        }
        SetQuietly(() =>
        {
            LineDownText = Units.LimitText(_service.Line.ManualDown);
            LineUpText = Units.LimitText(_service.Line.ManualUp);
        });
        Refresh();
    }

    [RelayCommand]
    private void ResetLearnedLine()
    {
        _service.ResetLearnedLineSpeed();
        Log.Info("Learned line speed reset");
        Refresh();
    }

    // ------------------------------------------------------------------ list / UI preferences

    [ObservableProperty] private bool _showIdle;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string _searchText = "";

    partial void OnShowIdleChanged(bool value) { SaveUi(); _forceSync = true; Refresh(); }
    partial void OnCloseToTrayChanged(bool value) => SaveUi();
    partial void OnStartMinimizedChanged(bool value) => SaveUi();
    partial void OnSearchTextChanged(string value) { _forceSync = true; Refresh(); }

    private void SaveUi()
    {
        if (_loading)
            return;
        _service.UiSettings = new UiSetting
        {
            ShowIdleProcesses = ShowIdle,
            CloseToTray = CloseToTray,
            StartMinimized = StartMinimized,
        };
    }

    public bool IsGame(string key) => _service.GetGames().Contains(key, StringComparer.OrdinalIgnoreCase);

    public bool IsVideoTarget(string key) => _service.GetVideoTargets().Contains(key, StringComparer.OrdinalIgnoreCase);

    private void ReloadLists()
    {
        Replace(Games, _service.GetGames().Select(k => new NamedItem(k)));
        Replace(TrackedSites, _service.GetTrackedSites().Select(k => new NamedItem(k)));
        HashSet<string> builtIn = Platforms.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> targets = _service.GetVideoTargets();
        Replace(CustomVideoTargets, targets.Where(k => !builtIn.Contains(k)).Select(k => new NamedItem(k)));
        foreach (PlatformOption p in Platforms)
            p.SetQuietly(targets.Contains(p.Key, StringComparer.OrdinalIgnoreCase));
        ReloadRanks();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
            target.Add(item);
    }

    // ------------------------------------------------------------------ errors

    private void OnServiceError(object? sender, Exception ex)
    {
        Log.Error("Service", ex);
        _dispatcher.BeginInvoke(() => ShowError("Something went wrong", ex.Message));
    }

    public void ShowError(string title, string message)
    {
        ErrorTitle = title;
        ErrorMessage = message;
        IsErrorOpen = true;
    }

    // ------------------------------------------------------------------ refresh

    public void Refresh()
    {
        TrafficSnapshot snap = _service.Snapshot;
        bool running = _service.IsRunning;

        UpdateHeader(snap, running);

        if (!IsWindowVisible)
            return; // only the tray tooltip matters while hidden

        Dictionary<string, AppAggregate> apps = Aggregate(snap, running);
        UpdateList(apps, snap.Qos);
    }

    private void UpdateHeader(TrafficSnapshot snap, bool running)
    {
        double down = running ? snap.TotalDownBytesPerSecond : 0;
        double up = running ? snap.TotalUpBytesPerSecond : 0;
        TotalDownText = Units.Speed(down);
        TotalUpText = Units.Speed(up);

        LineCapacity line = _service.Line;
        LineText = line.Down <= 0
            ? "Learning your line speed: it's measured the first time something fills the line"
            : $"Line {Units.Speed(line.Down)} ↓ · {(line.Up > 0 ? Units.Speed(line.Up) : "?")} ↑" +
              (line.ManualDown > 0 ? " (set by you)" : "");
        if (line.LearnedDown > _loggedLineDown * 1.1 + 64 * 1024 || line.LearnedUp > _loggedLineUp * 1.1 + 32 * 1024)
        {
            _loggedLineDown = line.LearnedDown;
            _loggedLineUp = line.LearnedUp;
            Log.Info($"Line speed learned: {line.LearnedDown / 1024} KB/s down, {line.LearnedUp / 1024} KB/s up");
        }
        LearnedLineText = line.LearnedDown <= 0
            ? "Nothing learned yet"
            : $"Learned so far: {Units.Speed(line.LearnedDown)} down, {Units.Speed(line.LearnedUp)} up";

        UpdatePing(running ? snap.Ping : PingStatus.None);

        if (!running)
        {
            EngineStatusText = "Paused";
            EngineDetailText = "Not monitoring or limiting traffic";
        }
        else if (_service.IsShaping)
        {
            EngineStatusText = "Limiting";
            EngineDetailText = snap.QueuedBytes > 0
                ? $"Speed limits active · {Units.Size(snap.QueuedBytes)} queued"
                : "Speed limits active";
        }
        else
        {
            EngineStatusText = "Monitoring";
            EngineDetailText = "No limits active right now";
        }

        QosStatus qos = snap.Qos;
        IsThrottling = running && qos.IsThrottling && qos.ThrottledKeys.Count > 0;
        string throttledSet = IsThrottling ? string.Join(",", qos.ThrottledKeys) : "";
        if (IsThrottling != _wasThrottling || throttledSet != _loggedThrottled)
        {
            _wasThrottling = IsThrottling;
            _loggedThrottled = throttledSet;
            Log.Info(IsThrottling
                ? $"Sharing ON: {string.Join(", ", qos.TriggeringKeys)} first; slowing " +
                  string.Join(", ", qos.ThrottledKeys.Select(k =>
                      $"{k} to {Units.Speed(_service.Shaper.FindGroup(k)?.EffectiveLimit.DownBytesPerSecond ?? 0)}"))
                : "Sharing OFF: everything back to full speed");
        }

        int ranked = _service.GetRanks().Count;
        if (!SharingEnabled)
        {
            SharingStatusText = "Off";
            SharingDetailText = "Priority numbers are ignored";
        }
        else if (IsThrottling)
        {
            SharingStatusText = qos.ThrottledKeys.Count == 1 ? "Slowing 1 app" : $"Slowing {qos.ThrottledKeys.Count} apps";
            SharingDetailText = qos.TriggeringKeys.Count > 0
                ? $"{string.Join(", ", qos.TriggeringKeys.Select(SiteCatalog.DisplayName))} comes first"
                : "Holding briefly after important traffic";
        }
        else
        {
            SharingStatusText = "Standing by";
            SharingDetailText = ranked < 2 && !FocusModeEnabled
                ? "Give apps priority numbers (1 = most important) to share the line"
                : line.Down <= 0
                    ? "Waiting to learn your line speed"
                    : "Nothing needs to be slowed right now";
        }

        IsGameActive = running && qos.IsGameActive;
        if (IsGameActive != _wasGame)
        {
            _wasGame = IsGameActive;
            Log.Info(IsGameActive ? $"Game mode active: {string.Join(", ", qos.RunningGames)}" : "Game mode idle: no game running");
        }
        int games = _service.GetGames().Count;
        GameStatusText = !GameModeEnabled ? "Off" : IsGameActive ? "Protecting" : "Ready";
        GameDetailText = !GameModeEnabled
            ? "Turn on to keep ping low while you play"
            : games == 0
                ? "Add your games in settings below"
                : IsGameActive
                    ? $"{string.Join(", ", qos.RunningGames)} · {Units.Speed(GameModeReserve)} kept free" +
                      (qos.PingFactor < 0.99 ? $" · ping guard −{(1 - qos.PingFactor) * 100:0}%" : "")
                    : $"Waiting for {(games == 1 ? _service.GetGames()[0] : $"one of {games} games")}";

        IsVideoActive = running && qos.IsVideoActive;
        if (IsVideoActive != _wasVideo)
        {
            _wasVideo = IsVideoActive;
            Log.Info(IsVideoActive
                ? $"Movie mode active: {string.Join(", ", qos.ActiveVideo)}, {Units.Speed(qos.VideoReserveBytesPerSecond)} kept free"
                : "Movie mode idle: no video");
        }
        MovieStatusText = !MovieModeEnabled ? "Off" : IsVideoActive ? "Video playing" : "Ready";
        MovieDetailText = !MovieModeEnabled
            ? "Turn on so videos never buffer"
            : IsVideoActive
                ? $"{string.Join(", ", qos.ActiveVideo.Select(SiteCatalog.DisplayName))} · {Units.Speed(qos.VideoReserveBytesPerSecond)} kept waiting"
                : $"Watching {_service.GetVideoTargets().Count} platforms";

        FocusDetailText = !FocusModeEnabled
            ? "The app in front gets no special treatment"
            : qos.ForegroundExe is { } fg
                ? $"{fg} is in focus and counts as priority 1"
                : "Waiting for a window to take focus";

        string state = !running ? "Paused" : IsGameActive ? "Game mode" : IsVideoActive ? "Movie mode"
            : IsThrottling ? "Sharing" : _service.IsShaping ? "Limiting" : "Monitoring";
        TrayToolTip = $"FlowGuard · {state}\n↓ {TotalDownText}   ↑ {TotalUpText}" + (PingText != "—" ? $"   ping {PingText}" : "");
    }

    private long GameModeReserve => _service.GameMode.ReserveBytesPerSecond;

    private void UpdatePing(PingStatus ping)
    {
        if (ping.Target is null)
        {
            PingText = "—";
            PingDetailText = "Measured once a second while FlowGuard runs";
            PingLevel = 0;
            return;
        }
        if (ping.TimedOut || ping.LastMs is null)
        {
            PingText = "Timeout";
            PingDetailText = $"No reply from {ping.Target}";
            PingLevel = 2;
            return;
        }
        double last = ping.LastMs.Value;
        double baseline = ping.BaselineMs ?? last;
        double extra = last - baseline;
        PingText = $"{last:0} ms";
        PingLevel = extra < 20 ? 0 : extra < 60 ? 1 : 2;
        PingDetailText = PingLevel == 0
            ? $"Normal for your line ({baseline:0} ms) · {ping.Target}"
            : $"+{extra:0} ms over normal: packets are queueing";
    }

    private Dictionary<string, AppAggregate> Aggregate(TrafficSnapshot snap, bool running)
    {
        var apps = new Dictionary<string, AppAggregate>(StringComparer.OrdinalIgnoreCase);

        if (running)
        {
            foreach (ProcessTrafficInfo p in snap.Processes)
            {
                AppAggregate a = GetOrAdd(apps, p.Name);
                a.Path ??= p.Path;
                a.Down += p.DownBytesPerSecond;
                a.Up += p.UpBytesPerSecond;
                a.TotalDown += p.TotalDownBytes;
                a.TotalUp += p.TotalUpBytes;
                a.Dropped += p.DroppedPackets;
                a.IsForeground |= p.IsForeground;
                a.IsQosThrottled |= p.IsQosThrottled;
                a.EffectiveLimit = p.EffectiveLimit;
                if (!p.IsSite && p.Pid != ProcessResolver.UnattributedPid && !a.Pids.Contains(p.Pid))
                    a.Pids.Add(p.Pid);
            }
        }

        if (ShowIdle)
        {
            // Everything with an open socket, plus every app and site that has a rule.
            AddSocketOwners(apps);
            foreach (string key in _service.GetLimits().Keys.Concat(_service.GetRanks().Keys)
                         .Concat(_service.GetTrackedSites()).Concat(_service.GetGames()))
                GetOrAdd(apps, key);
        }

        string search = SearchText.Trim();
        var result = new Dictionary<string, AppAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, AppAggregate a) in apps)
        {
            if (!ShowIdle && a.Down + a.Up <= 0)
                continue;
            if (search.Length > 0 && !key.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !SiteCatalog.DisplayName(key).Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            result[key] = a;
        }
        return result;
    }

    private void AddSocketOwners(Dictionary<string, AppAggregate> apps)
    {
        var seen = new HashSet<int>();
        foreach ((_, _, int pid) in _service.Mapper.GetEntries())
        {
            if (pid <= ProcessResolver.UnattributedPid || !seen.Add(pid))
                continue;
            if (!_socketOwners.TryGetValue(pid, out ProcessIdentity id))
                _socketOwners[pid] = id = ProcessResolver.Resolve(pid);

            AppAggregate a = GetOrAdd(apps, id.Name);
            a.Path ??= id.Path;
            if (!a.Pids.Contains(pid))
                a.Pids.Add(pid);
        }

        // Forget PIDs whose sockets are gone (the PID may be reused by another program).
        foreach (int pid in _socketOwners.Keys.Where(p => !seen.Contains(p)).ToList())
            _socketOwners.Remove(pid);
    }

    private static AppAggregate GetOrAdd(Dictionary<string, AppAggregate> apps, string key)
    {
        if (!apps.TryGetValue(key, out AppAggregate? a))
            apps[key] = a = new AppAggregate(key);
        return a;
    }

    private void UpdateList(Dictionary<string, AppAggregate> apps, QosStatus qos)
    {
        HashSet<string> games = _service.GetGames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> video = _service.GetVideoTargets().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, AppAggregate a) in apps)
        {
            if (!_rows.TryGetValue(key, out AppRowViewModel? row))
                _rows[key] = row = new AppRowViewModel(_service, key, OnRulesChanged);
            row.Update(a, qos, games.Contains(key), video.Contains(key));
        }

        // A filter change is an explicit request for a different list: never freeze through it.
        IsSortPaused = !_forceSync && IsUserInteracting?.Invoke() == true;
        _forceSync = false;
        if (IsSortPaused)
        {
            // Keep rows where they are; zero out the ones that went idle and append new ones.
            foreach (AppRowViewModel row in Apps)
                if (!apps.ContainsKey(row.ExeName))
                    row.MarkIdle();
            foreach (string key in apps.Keys)
                if (!Apps.Contains(_rows[key]))
                    Apps.Add(_rows[key]);
        }
        else
        {
            List<AppRowViewModel> desired = apps.Keys.Select(key => _rows[key])
                .OrderByDescending(r => r.TotalRate)
                .ThenByDescending(r => r.IsRunning)
                .ThenBy(r => r.IsUnattributed)
                .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Sync(desired);

            // Rows that left the list forget their state; they re-read rules when they return.
            foreach (string key in _rows.Keys.Where(k => !apps.ContainsKey(k)).ToList())
                _rows.Remove(key);
        }

        // Offer running apps (not already games) when adding a game.
        List<string> running = Apps.Where(r => r.IsApp && r.IsRunning && !games.Contains(r.ExeName))
            .Select(r => r.ExeName).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!running.SequenceEqual(RunningApps))
            Replace(RunningApps, running);

        int limited = Apps.Count(r => r.IsCapped);
        int active = Apps.Count(r => r.IsActive);
        ListSummary = $"{active} active" + (ShowIdle ? $" · {Apps.Count} shown" : "") + (limited > 0 ? $" · {limited} capped" : "");
        IsListEmpty = Apps.Count == 0;
        EmptyText = !_service.IsRunning
            ? "FlowGuard is paused. Turn it back on to see traffic."
            : SearchText.Trim().Length > 0
                ? "No apps match your search."
                : "No app is using the network right now. Turn on \"Show idle apps\" to see everything with an open connection.";
    }

    private void Sync(List<AppRowViewModel> desired)
    {
        var keep = new HashSet<AppRowViewModel>(desired);
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!keep.Contains(Apps[i]))
                Apps.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Apps.Count && ReferenceEquals(Apps[i], desired[i]))
                continue;
            int existing = Apps.IndexOf(desired[i]);
            if (existing >= 0)
                Apps.Move(existing, i);
            else
                Apps.Insert(i, desired[i]);
        }
    }

    private void SetQuietly(Action set)
    {
        bool previous = _loading;
        _loading = true;
        try { set(); }
        finally { _loading = previous; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _service.Error -= OnServiceError;
    }
}
