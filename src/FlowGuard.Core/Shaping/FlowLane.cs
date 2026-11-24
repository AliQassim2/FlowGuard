using System.Buffers;
using FlowGuard.Core.Engine;
using FlowGuard.Core.Packets;
using FlowGuard.Core.Stats;

namespace FlowGuard.Core.Shaping;

public sealed class ShapingOptions
{
    /// <summary>Bucket depth expressed as time at the configured rate (default 100 ms).</summary>
    public TimeSpan BurstDuration { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Floor for bucket depth so tiny limits still pass a few MTU-sized packets (default 4500 B).</summary>
    public int MinBurstBytes { get; init; } = 4500;

    /// <summary>
    /// Max queueing delay before tail-drop (default 500 ms). Dropping beyond this is deliberate:
    /// it is the congestion signal that makes TCP / QUIC senders back off to the cap.
    /// </summary>
    public TimeSpan MaxQueueDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Queue floor regardless of <see cref="MaxQueueDelay"/> (default 16 KB). Without it, caps
    /// below ~3 KB/s could not hold even one full-size packet and would turn every over-burst
    /// packet into a drop instead of a delay.
    /// </summary>
    public int MinQueueBytes { get; init; } = 16 * 1024;

    /// <summary>Hard memory bound per lane (default 4 MB).</summary>
    public int MaxQueueBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// One direction (down or up) of one shaping group: a token bucket plus a FIFO delay queue.
/// FIFO is strict — once anything is queued, later packets queue behind it — so shaping never
/// reorders a flow.
/// </summary>
public sealed class FlowLane
{
    private struct QueuedPacket
    {
        public byte[] Buffer;
        public int Length;
        public PacketMeta Meta;
        public ProcessTrafficState Owner;
    }

    private readonly object _lock = new();
    private readonly Queue<QueuedPacket> _queue = new();
    private readonly ShapingOptions _options;
    private readonly TokenBucket _bucket;
    private long _queuedBytes;
    private long _droppedPackets;

    internal FlowLane(PacketDirection direction, ShapingOptions options, long nowTicks)
    {
        Direction = direction;
        _options = options;
        _bucket = new TokenBucket(0, 0, nowTicks);
    }

    public PacketDirection Direction { get; }

    /// <summary>Current effective cap in bytes/s (0 = unlimited).</summary>
    public long RateBytesPerSecond { get; private set; }

    public int QueuedPackets { get { lock (_lock) return _queue.Count; } }
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    public void SetRate(long bytesPerSecond, long nowTicks)
    {
        lock (_lock)
        {
            if (bytesPerSecond == RateBytesPerSecond)
                return;
            RateBytesPerSecond = Math.Max(0, bytesPerSecond);
            double capacity = RateBytesPerSecond <= 0
                ? 0
                : Math.Max(_options.MinBurstBytes, RateBytesPerSecond * _options.BurstDuration.TotalSeconds);
            _bucket.Configure(RateBytesPerSecond, capacity, nowTicks);
        }
    }

    /// <summary>
    /// Decides the fate of a freshly captured packet. On <see cref="PacketVerdict.Queued"/> the
    /// bytes have been copied into a pooled buffer and the caller must wake the scheduler when
    /// <paramref name="wasIdle"/> is true.
    /// </summary>
    public PacketVerdict Admit(ReadOnlySpan<byte> packet, in PacketMeta meta, ProcessTrafficState owner,
        long nowTicks, out bool wasIdle)
    {
        wasIdle = false;
        int length = packet.Length;

        lock (_lock)
        {
            if (_queue.Count == 0 && _bucket.TryConsume(length, nowTicks))
                return PacketVerdict.Pass;

            long projected = _queuedBytes + length;
            bool overDelay = RateBytesPerSecond > 0 &&
                             projected > Math.Max(_options.MinQueueBytes,
                                 RateBytesPerSecond * _options.MaxQueueDelay.TotalSeconds);
            if (overDelay || projected > _options.MaxQueueBytes)
            {
                _droppedPackets++;
                return PacketVerdict.Drop;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            packet.CopyTo(buffer);
            _queue.Enqueue(new QueuedPacket { Buffer = buffer, Length = length, Meta = meta, Owner = owner });
            _queuedBytes = projected;
            wasIdle = _queue.Count == 1;
            return PacketVerdict.Queued;
        }
    }

    /// <summary>
    /// Releases as many queued packets as the bucket allows.
    /// </summary>
    /// <returns>Ticks until the head packet may be sent, or -1 when the queue is empty.</returns>
    public long Release(long nowTicks, IPacketInjector? injector)
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
            {
                if (!_bucket.TryConsume(_queue.Peek().Length, nowTicks))
                    return Math.Max(1, _bucket.TicksUntilAvailable(nowTicks));

                QueuedPacket p = _queue.Dequeue();
                _queuedBytes -= p.Length;

                bool sent = injector is not null && injector.Inject(p.Buffer.AsSpan(0, p.Length), p.Meta);
                if (sent)
                    p.Owner.AddDelivered(Direction, p.Length);
                else
                {
                    _droppedPackets++;
                    p.Owner.AddDropped(p.Length);
                }
                ArrayPool<byte>.Shared.Return(p.Buffer);
            }
            return -1;
        }
    }

    /// <summary>Discards everything queued (engine shutdown).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            while (_queue.TryDequeue(out QueuedPacket p))
                ArrayPool<byte>.Shared.Return(p.Buffer);
            _queuedBytes = 0;
        }
    }
}

/// <summary>All processes sharing one executable name share one group (and therefore one cap).</summary>
public sealed class ShapingGroup
{
    internal ShapingGroup(string exeName, ShapingOptions options, long nowTicks)
    {
        ExeName = exeName;
        Down = new FlowLane(PacketDirection.Inbound, options, nowTicks);
        Up = new FlowLane(PacketDirection.Outbound, options, nowTicks);
    }

    public string ExeName { get; }
    public FlowLane Down { get; }
    public FlowLane Up { get; }

    /// <summary>User-configured hard cap.</summary>
    public RateLimit HardLimit { get; internal set; }

    /// <summary>Dynamic cap imposed by QoS (Phase 3). Unlimited when inactive.</summary>
    public RateLimit DynamicCap { get; internal set; }

    public RateLimit EffectiveLimit => RateLimit.Min(HardLimit, DynamicCap);

    public bool IsActive => !EffectiveLimit.IsUnlimited || Down.QueuedBytes > 0 || Up.QueuedBytes > 0;

    public FlowLane Lane(PacketDirection direction) => direction == PacketDirection.Inbound ? Down : Up;

    internal void Apply(long nowTicks)
    {
        RateLimit eff = EffectiveLimit;
        Down.SetRate(eff.DownBytesPerSecond, nowTicks);
        Up.SetRate(eff.UpBytesPerSecond, nowTicks);
    }
}
