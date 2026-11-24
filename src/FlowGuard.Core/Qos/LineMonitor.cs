using System.Net;
using System.Net.NetworkInformation;

namespace FlowGuard.Core.Qos;

/// <summary>
/// Learns the internet line's speed from the fastest sustained total (1 s average) it has seen,
/// per direction. A manual value, when set, wins.
/// </summary>
public sealed class LineCapacity
{
    private const int Window = 4;
    private readonly object _lock = new();
    private readonly double[] _down = new double[Window];
    private readonly double[] _up = new double[Window];
    private readonly double[] _seconds = new double[Window];
    private int _index, _count;

    /// <summary>Learned peaks in bytes/s (0 = not learned yet).</summary>
    public long LearnedDown { get; private set; }
    public long LearnedUp { get; private set; }

    /// <summary>Manual values in bytes/s (0 = automatic).</summary>
    public long ManualDown { get; set; }
    public long ManualUp { get; set; }

    public long Down => ManualDown > 0 ? ManualDown : LearnedDown;
    public long Up => ManualUp > 0 ? ManualUp : LearnedUp;

    public void Restore(long learnedDown, long learnedUp)
    {
        lock (_lock)
        {
            LearnedDown = Math.Max(0, learnedDown);
            LearnedUp = Math.Max(0, learnedUp);
        }
    }

    public void ResetLearned() => Restore(0, 0);

    /// <summary>Feeds internet bytes moved during the last <paramref name="seconds"/>.</summary>
    /// <returns>True when a learned value grew noticeably (worth saving).</returns>
    public bool Observe(long downBytes, long upBytes, double seconds)
    {
        if (seconds <= 0)
            return false;
        lock (_lock)
        {
            _down[_index] = downBytes;
            _up[_index] = upBytes;
            _seconds[_index] = seconds;
            _index = (_index + 1) % Window;
            _count = Math.Min(_count + 1, Window);

            double span = 0, down = 0, up = 0;
            for (int i = 0; i < _count; i++)
            {
                span += _seconds[i];
                down += _down[i];
                up += _up[i];
            }
            if (span < 0.9)
                return false; // need a full second before trusting a peak

            bool grew = false;
            long d = (long)(down / span), u = (long)(up / span);
            if (d > LearnedDown)
            {
                grew |= d > LearnedDown * 1.03 + 1024;
                LearnedDown = d;
            }
            if (u > LearnedUp)
            {
                grew |= u > LearnedUp * 1.03 + 1024;
                LearnedUp = u;
            }
            return grew;
        }
    }
}

public sealed record PingStatus(IPAddress? Target, double? LastMs, double? BaselineMs, bool TimedOut)
{
    public static readonly PingStatus None = new(null, null, null, false);
}

/// <summary>
/// Pings a host once a second. The baseline is the lowest ping of the last minute: the line's
/// latency with empty queues. Anything above it is queueing delay, i.e. lag caused by traffic.
/// </summary>
public sealed class PingMonitor : IDisposable
{
    private const int BaselineSamples = 60;
    private readonly Queue<double> _recent = new();
    private readonly object _lock = new();
    private readonly Timer _timer;
    private readonly Ping _ping = new();
    private volatile IPAddress _target;
    private volatile PingStatus _status = PingStatus.None;
    private int _busy;
    private bool _disposed;

    public PingMonitor(IPAddress target)
    {
        _target = target;
        _timer = new Timer(_ => Send(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public PingStatus Status => _status;

    /// <summary>Raised on a thread-pool thread after each reply or timeout.</summary>
    public event Action<PingStatus>? Measured;

    public IPAddress Target
    {
        get => _target;
        set
        {
            if (value.Equals(_target))
                return;
            _target = value;
            lock (_lock) _recent.Clear();
            _status = PingStatus.None;
        }
    }

    public void Start() => _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _status = PingStatus.None;
    }

    private async void Send()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        IPAddress target = _target;
        try
        {
            PingReply reply = await _ping.SendPingAsync(target, 1000);
            Record(target, reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
        }
        catch (Exception) when (!_disposed)
        {
            Record(target, null);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Record(IPAddress target, double? ms)
    {
        if (!target.Equals(_target) || _disposed)
            return;
        double? baseline;
        lock (_lock)
        {
            if (ms is { } v)
            {
                _recent.Enqueue(v);
                while (_recent.Count > BaselineSamples)
                    _recent.Dequeue();
            }
            baseline = _recent.Count > 0 ? _recent.Min() : null;
        }
        var status = new PingStatus(target, ms, baseline, ms is null);
        _status = status;
        Measured?.Invoke(status);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _ping.Dispose();
    }
}

/// <summary>
/// Turns ping into a speed factor for everything except the game: back off quickly when the
/// ping climbs above its baseline (packets are queueing), recover slowly when it settles.
/// </summary>
public sealed class PingGuard
{
    public double RiseMs { get; init; } = 30;
    public double CalmMs { get; init; } = 12;
    public double BackOff { get; init; } = 0.85;
    public double Recover { get; init; } = 0.02;
    public double MinFactor { get; init; } = 0.4;

    public double Factor { get; private set; } = 1;

    public void Reset() => Factor = 1;

    /// <summary>Call once per ping sample.</summary>
    public double Update(PingStatus status)
    {
        if (status.TimedOut || (status.LastMs is { } last && status.BaselineMs is { } b && last > b + RiseMs))
            Factor = Math.Max(MinFactor, Factor * BackOff);
        else if (status.LastMs is { } l && status.BaselineMs is { } baseline && l <= baseline + CalmMs)
            Factor = Math.Min(1, Factor + Recover);
        return Factor;
    }
}
