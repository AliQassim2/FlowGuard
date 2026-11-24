using System.Diagnostics;

namespace FlowGuard.Core.Shaping;

/// <summary>
/// Classic token bucket with <em>debt</em> semantics: a packet is admitted whenever the balance is
/// non-negative, and may drive it negative. This admits packets larger than the burst size (TCP
/// segmentation-offload frames can be 64 KB) while keeping the long-run rate exact.
/// <para>
/// Time is passed in explicitly as <see cref="Stopwatch"/> ticks, making the math deterministic
/// and testable. Not thread-safe: callers serialise access (see <see cref="FlowLane"/>).
/// </para>
/// </summary>
public sealed class TokenBucket
{
    private readonly long _frequency;
    private double _tokens;
    private double _rate;      // bytes per second; <= 0 means unlimited
    private double _capacity;  // bytes
    private long _lastTicks;

    public TokenBucket(double bytesPerSecond, double capacityBytes, long nowTicks, long? ticksPerSecond = null)
    {
        _frequency = ticksPerSecond ?? Stopwatch.Frequency;
        _lastTicks = nowTicks;
        _rate = bytesPerSecond;
        _capacity = Math.Max(0, capacityBytes);
        _tokens = _capacity;
    }

    public bool IsUnlimited => _rate <= 0;
    public double Rate => _rate;
    public double Capacity => _capacity;

    /// <summary>Current balance after refilling to <paramref name="nowTicks"/> (may be negative).</summary>
    public double GetTokens(long nowTicks)
    {
        Refill(nowTicks);
        return _tokens;
    }

    /// <summary>
    /// Changes rate/capacity without resetting accumulated debt, so retuning a busy bucket
    /// (e.g. by the QoS controller) cannot be abused to burst.
    /// </summary>
    public void Configure(double bytesPerSecond, double capacityBytes, long nowTicks)
    {
        Refill(nowTicks);
        bool wasUnlimited = IsUnlimited;
        _rate = bytesPerSecond;
        _capacity = Math.Max(0, capacityBytes);

        if (wasUnlimited)
            _tokens = _capacity;               // coming from unlimited: start with one full burst
        else if (_tokens > _capacity)
            _tokens = _capacity;
    }

    /// <summary>Admits <paramref name="bytes"/> if the balance is non-negative.</summary>
    public bool TryConsume(int bytes, long nowTicks)
    {
        if (_rate <= 0)
            return true;

        Refill(nowTicks);
        if (_tokens < 0)
            return false;

        _tokens -= bytes;
        return true;
    }

    /// <summary>Ticks until <see cref="TryConsume"/> would succeed (0 = now).</summary>
    public long TicksUntilAvailable(long nowTicks)
    {
        if (_rate <= 0)
            return 0;

        Refill(nowTicks);
        if (_tokens >= 0)
            return 0;
        return (long)Math.Ceiling(-_tokens / _rate * _frequency);
    }

    private void Refill(long nowTicks)
    {
        long elapsed = nowTicks - _lastTicks;
        if (elapsed <= 0)
            return;
        _lastTicks = nowTicks;

        if (_rate > 0)
            _tokens = Math.Min(_capacity, _tokens + elapsed * _rate / _frequency);
    }
}
