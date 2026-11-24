using FlowGuard.Core.Shaping;

namespace FlowGuard.Core.Tests;

public class TokenBucketTests
{
    // 1000 ticks per second ⇒ 1 tick = 1 ms, which keeps the arithmetic readable.
    private const long Hz = 1000;

    [Fact]
    public void Unlimited_bucket_admits_everything()
    {
        var b = new TokenBucket(0, 0, 0, Hz);
        for (int i = 0; i < 10_000; i++)
            Assert.True(b.TryConsume(65_535, 0));
        Assert.Equal(0, b.TicksUntilAvailable(0));
    }

    [Fact]
    public void Starts_full_and_allows_one_packet_of_debt()
    {
        var b = new TokenBucket(1000, 1000, 0, Hz);

        Assert.True(b.TryConsume(1000, 0));  // balance 0
        Assert.True(b.TryConsume(500, 0));   // balance 0 is non-negative ⇒ admitted into debt (-500)
        Assert.False(b.TryConsume(1, 0));    // in debt
        Assert.Equal(500, b.TicksUntilAvailable(0)); // 500 B at 1000 B/s = 0.5 s

        Assert.False(b.TryConsume(1, 499));
        Assert.True(b.TryConsume(1, 500));
    }

    [Fact]
    public void Refill_never_exceeds_capacity()
    {
        var b = new TokenBucket(1000, 300, 0, Hz);
        Assert.Equal(300, b.GetTokens(60_000), 3);
    }

    [Fact]
    public void Packets_larger_than_capacity_still_flow_at_the_configured_rate()
    {
        // 64 KB LSO frames through a 10 KB bucket.
        var b = new TokenBucket(100_000, 10_000, 0, Hz);
        long admitted = 0;
        for (long t = 0; t <= 10_000; t++) // 10 s, poll every ms
            while (b.TryConsume(65_536, t))
                admitted += 65_536;

        // Expect rate*time + capacity, plus at most one packet of debt.
        double expected = 100_000 * 10 + 10_000;
        Assert.InRange(admitted, expected - 65_536, expected + 65_536);
    }

    [Fact]
    public void Long_run_rate_matches_configuration()
    {
        var b = new TokenBucket(50 * 1024, 5 * 1024, 0, Hz);
        long admitted = 0;
        for (long t = 0; t <= 20_000; t++) // 20 s
            while (b.TryConsume(1500, t))
                admitted += 1500;

        double expectedRate = 50 * 1024;
        double measured = (admitted - 5 * 1024) / 20.0;
        Assert.InRange(measured, expectedRate * 0.99, expectedRate * 1.01);
    }

    [Fact]
    public void Reconfigure_preserves_debt()
    {
        var b = new TokenBucket(1000, 1000, 0, Hz);
        b.TryConsume(1000, 0);
        b.TryConsume(1000, 0); // balance -1000

        b.Configure(2000, 2000, 0);
        Assert.Equal(-1000, b.GetTokens(0), 3);
        Assert.Equal(500, b.TicksUntilAvailable(0)); // 1000 B at the new 2000 B/s
    }

    [Fact]
    public void Leaving_unlimited_starts_with_a_full_burst()
    {
        var b = new TokenBucket(0, 0, 0, Hz);
        b.TryConsume(1_000_000, 0);
        b.Configure(1000, 400, 0);
        Assert.Equal(400, b.GetTokens(0), 3);
    }

    [Fact]
    public void Lowering_capacity_clamps_balance()
    {
        var b = new TokenBucket(1000, 1000, 0, Hz);
        b.Configure(1000, 100, 0);
        Assert.Equal(100, b.GetTokens(0), 3);
    }

    [Fact]
    public void Time_going_backwards_is_ignored()
    {
        var b = new TokenBucket(1000, 1000, 1000, Hz);
        b.TryConsume(1000, 1000);
        Assert.Equal(0, b.GetTokens(500), 3);
    }
}

public class RateLimitTests
{
    [Fact]
    public void Min_treats_zero_as_unlimited()
    {
        var hard = new RateLimit(500, 0);
        var qos = new RateLimit(50, 200);
        Assert.Equal(new RateLimit(50, 200), RateLimit.Min(hard, qos));
        Assert.Equal(hard, RateLimit.Min(hard, RateLimit.Unlimited));
        Assert.True(RateLimit.Min(RateLimit.Unlimited, RateLimit.Unlimited).IsUnlimited);
    }

    [Fact]
    public void FromKBps_uses_1024()
    {
        Assert.Equal(new RateLimit(512_000, 1024), RateLimit.FromKBps(500, 1));
    }
}
