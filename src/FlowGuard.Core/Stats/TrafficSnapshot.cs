using FlowGuard.Core.Qos;
using FlowGuard.Core.Shaping;

namespace FlowGuard.Core.Stats;

/// <summary>Immutable per-process view published once per sample interval.</summary>
public sealed record ProcessTrafficInfo(
    int Pid,
    string Name,
    string? Path,
    double DownBytesPerSecond,
    double UpBytesPerSecond,
    long TotalDownBytes,
    long TotalUpBytes,
    long DroppedPackets,
    RateLimit EffectiveLimit,
    RateLimit HardLimit,
    byte Rank,
    bool IsForeground,
    bool IsQosThrottled,
    bool IsSite = false)
{
    public bool IsIdle => DownBytesPerSecond <= 0 && UpBytesPerSecond <= 0;
    public bool IsLimited => !EffectiveLimit.IsUnlimited;
}

public sealed record TrafficSnapshot(
    DateTime TimestampUtc,
    IReadOnlyList<ProcessTrafficInfo> Processes,
    double TotalDownBytesPerSecond,
    double TotalUpBytesPerSecond,
    bool IsShaping,
    long QueuedBytes,
    QosStatus Qos,
    PingStatus Ping)
{
    public static readonly TrafficSnapshot Empty = new(DateTime.MinValue, [], 0, 0, false, 0, QosStatus.Inactive, PingStatus.None);
}
