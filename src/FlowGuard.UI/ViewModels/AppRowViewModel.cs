using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FlowGuard.Core;
using FlowGuard.Core.Processes;
using FlowGuard.Core.Qos;
using FlowGuard.Core.Shaping;
using FlowGuard.Core.Sites;
using FlowGuard.UI.Services;

namespace FlowGuard.UI.ViewModels;

/// <summary>
/// One row of the list: an executable (all of its processes) or a tracked site. Limits and
/// priorities live in the core per key, so editing a row affects every process with that name.
/// </summary>
public sealed partial class AppRowViewModel : ObservableObject
{
    private readonly FlowGuardService _service;
    private readonly Action _rulesChanged;
    private bool _syncing;
    private RateLimit _hardLimit;
    private byte _rank;

    internal AppRowViewModel(FlowGuardService service, string key, Action rulesChanged)
    {
        _service = service;
        _rulesChanged = rulesChanged;
        ExeName = key;
        IsSite = TargetKey.IsSite(key);
        IsUnattributed = !IsSite && string.Equals(key, ProcessResolver.Resolve(ProcessResolver.UnattributedPid).Name, StringComparison.OrdinalIgnoreCase);
        DisplayName = IsUnattributed ? "Unattributed traffic" : SiteCatalog.DisplayName(key);

        _syncing = true;
        _rank = service.GetRank(key);
        _rankText = RankToText(_rank);
        _hardLimit = service.GetLimits().GetValueOrDefault(key);
        _downLimitText = Units.LimitText(_hardLimit.DownBytesPerSecond);
        _upLimitText = Units.LimitText(_hardLimit.UpBytesPerSecond);
        _syncing = false;
    }

    /// <summary>Rule key: "steam.exe" or "site:youtube".</summary>
    public string ExeName { get; }
    public string DisplayName { get; }
    public bool IsSite { get; }
    public bool IsUnattributed { get; }
    public bool IsEditable => !IsUnattributed;
    public bool IsApp => !IsSite && !IsUnattributed;

    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string _pidText = "";
    [ObservableProperty] private string? _pidToolTip;
    [ObservableProperty] private string _downText = "";
    [ObservableProperty] private string _upText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isForeground;
    [ObservableProperty] private bool _isQosThrottled;
    [ObservableProperty] private bool _isCapped;
    [ObservableProperty] private bool _isGame;
    [ObservableProperty] private bool _isVideo;

    [ObservableProperty] private string _rankText;
    [ObservableProperty] private string _downLimitText;
    [ObservableProperty] private string _upLimitText;

    internal double TotalRate { get; private set; }

    public byte Rank => _rank;

    internal static string RankToText(byte rank) => rank == Core.Qos.Rank.None ? "" : rank.ToString();

    /// <summary>"" or "-" clears the number; 1..128 sets it; anything else is reverted.</summary>
    partial void OnRankTextChanged(string value)
    {
        if (_syncing)
            return;
        string s = (value ?? "").Trim().TrimStart('#');
        int rank = -1;
        if (s is "" or "-" or "—")
            rank = Core.Qos.Rank.None;
        else if (int.TryParse(s, out int n) && Core.Qos.Rank.IsValid(n))
            rank = n;

        if (rank >= 0 && rank != _rank)
        {
            _service.SetRank(ExeName, rank);
            _rank = (byte)rank;
            Log.Info($"Priority {ExeName} = {(rank == 0 ? "none" : rank.ToString())}");
            _rulesChanged();
        }
        SetRankQuietly(_rank);
    }

    /// <summary>Re-reads the number after it was changed elsewhere (e.g. the priority order list).</summary>
    internal void ReloadRank() => SetRankQuietly(_service.GetRank(ExeName));

    private void SetRankQuietly(byte rank)
    {
        _rank = rank;
        _syncing = true;
        RankText = RankToText(rank);
        _syncing = false;
        OnPropertyChanged(nameof(Rank));
    }

    partial void OnDownLimitTextChanged(string value) => ApplyLimit();

    partial void OnUpLimitTextChanged(string value) => ApplyLimit();

    private void ApplyLimit()
    {
        if (_syncing)
            return;

        bool okDown = Units.TryParseLimitKBps(DownLimitText, out double down);
        bool okUp = Units.TryParseLimitKBps(UpLimitText, out double up);
        if (okDown && okUp)
        {
            RateLimit limit = RateLimit.FromKBps(down, up);
            if (limit != _hardLimit)
            {
                _service.SetLimit(ExeName, limit);
                _hardLimit = limit;
                Log.Info($"Limit {ExeName} = {down:0.##} KB/s down, {up:0.##} KB/s up");
            }
        }

        // Normalise the text ("2 MB" -> "2048") or revert an invalid entry.
        _syncing = true;
        DownLimitText = Units.LimitText(_hardLimit.DownBytesPerSecond);
        UpLimitText = Units.LimitText(_hardLimit.UpBytesPerSecond);
        _syncing = false;
        IsCapped = !_hardLimit.IsUnlimited;
    }

    internal void Update(AppAggregate a, QosStatus qos, bool isGame, bool isVideo)
    {
        TotalRate = a.Down + a.Up;
        IsRunning = IsSite || a.Pids.Count > 0;
        IsActive = TotalRate > 0;
        DownText = IsActive || a.TotalDown > 0 ? Units.Speed(a.Down) : "";
        UpText = IsActive || a.TotalUp > 0 ? Units.Speed(a.Up) : "";
        IsForeground = a.IsForeground;
        IsQosThrottled = a.IsQosThrottled && TotalRate > 16 * 1024;
        IsCapped = !_hardLimit.IsUnlimited;
        IsGame = isGame;
        IsVideo = isVideo;

        if (Path is null && a.Path is not null)
        {
            Path = a.Path;
            Icon = IconCache.Get(a.Path);
        }

        a.Pids.Sort();
        PidText = IsSite ? "Website" : a.Pids.Count switch
        {
            0 => IsUnattributed ? "" : "—",
            1 => a.Pids[0].ToString(),
            _ => $"{a.Pids.Count} processes",
        };
        PidToolTip = a.Pids.Count > 1 ? "PIDs " + string.Join(", ", a.Pids) : null;

        DetailText = BuildDetail(a, qos);
    }

    /// <summary>Row kept in place while the list order is frozen, but no longer in the data.</summary>
    internal void MarkIdle()
    {
        TotalRate = 0;
        IsActive = false;
        IsQosThrottled = false;
        IsForeground = false;
        if (DownText.Length > 0) DownText = Units.Speed(0);
        if (UpText.Length > 0) UpText = Units.Speed(0);
        DetailText = "Idle";
    }

    private string BuildDetail(AppAggregate a, QosStatus qos)
    {
        if (IsUnattributed)
            return "Packets whose socket closed before it could be matched to a process";
        if (IsGame && qos.IsGameActive)
            return "Game mode: always first, with space kept free";
        if (IsVideo && qos.ActiveVideo.Contains(ExeName, StringComparer.OrdinalIgnoreCase))
            return $"Movie mode: {Units.Speed(qos.VideoReserveBytesPerSecond)} kept waiting for the next chunk";
        if (IsQosThrottled && !a.EffectiveLimit.IsUnlimited)
            return $"Sharing: up to {Units.Speed(a.EffectiveLimit.DownBytesPerSecond)} while more important apps are busy";
        if (a.IsForeground)
            return "In focus: counts as priority 1";
        if (!IsSite && a.Pids.Count == 0)
            return "Not running";
        if (a.TotalDown > 0 || a.TotalUp > 0)
            return $"↓ {Units.Size(a.TotalDown)} · ↑ {Units.Size(a.TotalUp)} total" +
                   (a.Dropped > 0 ? $" · {a.Dropped:N0} packets dropped by limit" : "");
        return IsSite ? "Recognised by its web address, in any browser or app" : "Idle";
    }
}

/// <summary>Per-key totals assembled from one snapshot (plus idle sockets/rules).</summary>
internal sealed class AppAggregate(string exe)
{
    public string Exe { get; } = exe;
    public string? Path;
    public double Down, Up;
    public long TotalDown, TotalUp, Dropped;
    public readonly List<int> Pids = [];
    public bool IsForeground, IsQosThrottled;
    public RateLimit EffectiveLimit = RateLimit.Unlimited;
}
