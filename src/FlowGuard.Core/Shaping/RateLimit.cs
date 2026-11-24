namespace FlowGuard.Core.Shaping;

/// <summary>Download/upload caps in bytes per second. 0 means unlimited.</summary>
public readonly record struct RateLimit(long DownBytesPerSecond, long UpBytesPerSecond)
{
    public static readonly RateLimit Unlimited = new(0, 0);

    public static RateLimit FromKBps(double downKBps, double upKBps)
        => new((long)(Math.Max(0, downKBps) * 1024), (long)(Math.Max(0, upKBps) * 1024));

    public bool IsUnlimited => DownBytesPerSecond <= 0 && UpBytesPerSecond <= 0;

    /// <summary>The stricter of two limits, per direction (0 = unlimited is the identity).</summary>
    public static RateLimit Min(RateLimit a, RateLimit b)
        => new(MinRate(a.DownBytesPerSecond, b.DownBytesPerSecond), MinRate(a.UpBytesPerSecond, b.UpBytesPerSecond));

    private static long MinRate(long a, long b)
        => a <= 0 ? Math.Max(0, b) : b <= 0 ? a : Math.Min(a, b);

    public override string ToString()
        => $"↓{Format(DownBytesPerSecond)} ↑{Format(UpBytesPerSecond)}";

    private static string Format(long bps) => bps <= 0 ? "∞" : $"{bps / 1024.0:0.#} KB/s";
}
