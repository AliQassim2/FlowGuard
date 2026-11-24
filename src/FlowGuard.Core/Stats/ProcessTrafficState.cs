using System.Runtime.CompilerServices;
using FlowGuard.Core.Packets;
using FlowGuard.Core.Processes;
using FlowGuard.Core.Shaping;

namespace FlowGuard.Core.Stats;

/// <summary>
/// Live, mutable per-process counters. Written by the capture / shaper threads with Interlocked,
/// read by the sampler. One instance per PID for the lifetime of that process.
/// </summary>
public sealed class ProcessTrafficState
{
    private long _downBytes;
    private long _upBytes;
    private long _droppedPackets;
    private long _droppedBytes;

    internal ProcessTrafficState(ProcessIdentity identity)
    {
        Identity = identity;
    }

    public ProcessIdentity Identity { get; }
    public int Pid => Identity.Pid;
    public string Name => Identity.Name;

    /// <summary>True for the counters of a tracked site rather than a process.</summary>
    public bool IsSite => Identity.Pid < 0;

    /// <summary>Bytes actually delivered (passed or released from the delay queue).</summary>
    public long TotalDownBytes => Interlocked.Read(ref _downBytes);
    public long TotalUpBytes => Interlocked.Read(ref _upBytes);
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddDelivered(PacketDirection direction, int bytes)
    {
        if (direction == PacketDirection.Inbound)
            Interlocked.Add(ref _downBytes, bytes);
        else
            Interlocked.Add(ref _upBytes, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddDropped(int bytes)
    {
        Interlocked.Increment(ref _droppedPackets);
        Interlocked.Add(ref _droppedBytes, bytes);
    }

    // ---- Shaper binding (hot path; see TrafficShaper) ----
    internal ShapingGroup? Group;
    internal int ConfigVersion = -1;

    // ---- Sampler-owned state (single-threaded access from the sampler) ----
    internal long SampledDown;
    internal long SampledUp;
    internal double DownRate;
    internal double UpRate;
    internal long LastActiveTimestamp;
}
