using FlowGuard.Core.Shaping;

namespace FlowGuard.Core.Qos;

/// <summary>
/// Priority numbers: 1 is the most important, 128 the least. Apps and sites without a number
/// (<see cref="None"/>) are left alone: never slowed by smart sharing.
/// </summary>
public static class Rank
{
    public const byte None = 0;
    public const byte Highest = 1;
    public const byte Lowest = 128;

    /// <summary>What the old High / Low choices map to, keeping their order with room around them.</summary>
    public const byte LegacyHigh = 10;
    public const byte LegacyLow = 100;

    public static bool IsValid(int rank) => rank is >= Highest and <= Lowest;

    public static byte Clamp(int rank) => rank <= 0 ? None : (byte)Math.Min(rank, Lowest);
}

/// <summary>Smart sharing tuning. Immutable — replace with <c>with</c> to change.</summary>
public sealed record QosOptions
{
    /// <summary>
    /// Master switch for smart sharing by priority number. Game and Movie mode have their own
    /// switches and work either way.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>An app or site counts as "using the network" above this combined rate (default 16 KB/s).</summary>
    public long ActiveBytesPerSecond { get; init; } = 16 * 1024;

    /// <summary>How long a numbered app keeps priority after it goes quiet (default 5 s).</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Nothing is ever slowed below this, per app and direction (default 50 KB/s).</summary>
    public long MinBytesPerSecond { get; init; } = 50 * 1024;

    /// <summary>
    /// While something more important is busy, slower apps share at most this fraction of the
    /// line (default 92 %): a full line queues packets and raises everyone's ping.
    /// </summary>
    public double LineUtilization { get; init; } = 0.92;

    /// <summary>
    /// Extra room kept free for more important apps to speed up into (default 12 % of the line);
    /// without it they can never show how much they want.
    /// </summary>
    public double Headroom { get; init; } = 0.12;
}

/// <summary>Game mode: listed games come first, with a fixed reservation and a ping guard.</summary>
public sealed record GameModeOptions
{
    public bool Enabled { get; init; }

    /// <summary>Always kept free for the game while it runs (default 100 KB/s each way).</summary>
    public long ReserveBytesPerSecond { get; init; } = 100 * 1024;

    /// <summary>Part of the line never filled while a game runs, so packets never queue (default 7 %).</summary>
    public double SafetyMargin { get; init; } = 0.07;

    /// <summary>Slow everything else further when the measured ping climbs.</summary>
    public bool PingGuard { get; init; } = true;
}

/// <summary>Movie mode: room kept waiting for video so every chunk downloads at full speed.</summary>
public sealed record MovieModeOptions
{
    public bool Enabled { get; init; }

    /// <summary>Space kept free for video (download). 0 = learn it from the video's own speed.</summary>
    public long ReserveBytesPerSecond { get; init; }

    /// <summary>How long the space stays reserved after the last video traffic (default 20 s).</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>Outcome of one QoS evaluation, published with each <see cref="Stats.TrafficSnapshot"/>.</summary>
public sealed record QosStatus(
    bool Enabled,
    bool IsThrottling,
    IReadOnlyList<string> TriggeringKeys,
    IReadOnlyList<string> ThrottledKeys,
    bool FocusModeEnabled,
    string? ForegroundExe,
    bool GameModeEnabled,
    IReadOnlyList<string> RunningGames,
    bool MovieModeEnabled,
    IReadOnlyList<string> ActiveVideo,
    long VideoReserveBytesPerSecond,
    double PingFactor,
    long CapacityDown,
    long CapacityUp)
{
    public static readonly QosStatus Inactive = new(true, false, [], [], false, null, false, [], false, [], 0, 1, 0, 0);

    public bool IsGameActive => GameModeEnabled && RunningGames.Count > 0;
    public bool IsVideoActive => MovieModeEnabled && ActiveVideo.Count > 0;
}
