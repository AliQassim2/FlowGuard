using System.Collections.Concurrent;
using System.Diagnostics;
using FlowGuard.Core.Engine;
using FlowGuard.Core.Packets;
using FlowGuard.Core.Processes;
using FlowGuard.Core.Shaping;
using FlowGuard.Core.Stats;

namespace FlowGuard.Core.Tests;

internal sealed class RecordingInjector : IPacketInjector
{
    public readonly ConcurrentQueue<byte[]> Sent = new();
    public bool Fail { get; set; }

    public bool Inject(ReadOnlySpan<byte> packet, in PacketMeta meta)
    {
        if (Fail) return false;
        Sent.Enqueue(packet.ToArray());
        return true;
    }
}

public class FlowLaneTests
{
    private static readonly long Hz = Stopwatch.Frequency;
    private static long Ms(double ms) => (long)(ms * Hz / 1000);

    private static readonly ShapingOptions Options = new()
    {
        BurstDuration = TimeSpan.FromMilliseconds(100),
        MinBurstBytes = 1000,
        MaxQueueDelay = TimeSpan.FromMilliseconds(500),
        MinQueueBytes = 0,
        MaxQueueBytes = 1_000_000,
    };

    private static byte[] Packet(byte tag, int size = 1000)
    {
        var p = new byte[size];
        p[0] = tag;
        return p;
    }

    private static ProcessTrafficState Owner() => new(new ProcessIdentity(1234, "test.exe", null, 0));

    [Fact]
    public void Unlimited_lane_passes_everything()
    {
        var lane = new FlowLane(PacketDirection.Inbound, Options, 0);
        var owner = Owner();
        for (int i = 0; i < 1000; i++)
            Assert.Equal(PacketVerdict.Pass, lane.Admit(Packet(0), default, owner, 0, out _));
    }

    [Fact]
    public void Queues_once_burst_is_spent_and_keeps_strict_fifo()
    {
        long t0 = 1_000_000;
        var lane = new FlowLane(PacketDirection.Inbound, Options, t0);
        lane.SetRate(10_000, t0); // capacity = max(1000, 10_000 * 0.1) = 1000 B
        var owner = Owner();

        Assert.Equal(PacketVerdict.Pass, lane.Admit(Packet(1), default, owner, t0, out _));   // 1000 → 0
        Assert.Equal(PacketVerdict.Pass, lane.Admit(Packet(2), default, owner, t0, out _));   // 0 → -1000 (debt)
        Assert.Equal(PacketVerdict.Queued, lane.Admit(Packet(3), default, owner, t0, out bool idle));
        Assert.True(idle);

        // Even after tokens are available again, new packets must queue behind #3.
        long later = t0 + Ms(500);
        Assert.Equal(PacketVerdict.Queued, lane.Admit(Packet(4), default, owner, later, out idle));
        Assert.False(idle);
        Assert.Equal(2, lane.QueuedPackets);

        // Bucket refilled to its 1000 B cap: #3 takes it to 0, #4 is admitted into debt.
        var injector = new RecordingInjector();
        Assert.Equal(-1, lane.Release(later, injector));
        Assert.Equal(new byte[] { 3, 4 }, injector.Sent.Select(p => p[0]).ToArray());

        // Now in debt (-1000 B at 10 KB/s): the next packet must wait ~100 ms.
        Assert.Equal(PacketVerdict.Queued, lane.Admit(Packet(5), default, owner, later, out _));
        long wait = lane.Release(later, injector);
        Assert.InRange(wait, Ms(99), Ms(101));
        Assert.Equal(-1, lane.Release(later + wait, injector));
        Assert.Equal(new byte[] { 3, 4, 5 }, injector.Sent.Select(p => p[0]).ToArray());
        Assert.Equal(3000, owner.TotalDownBytes); // only released packets counted by the lane
        Assert.Equal(0, lane.QueuedBytes);
    }

    [Fact]
    public void Tail_drops_beyond_max_queue_delay()
    {
        var lane = new FlowLane(PacketDirection.Outbound, Options, 0);
        lane.SetRate(10_000, 0); // 500 ms at 10 KB/s ⇒ 5000 B of queue
        var owner = Owner();

        lane.Admit(Packet(0), default, owner, 0, out _);
        lane.Admit(Packet(0), default, owner, 0, out _);

        int queued = 0, dropped = 0;
        for (int i = 0; i < 20; i++)
        {
            var v = lane.Admit(Packet(0), default, owner, 0, out _);
            if (v == PacketVerdict.Queued) queued++;
            if (v == PacketVerdict.Drop) dropped++;
        }
        Assert.Equal(5, queued);
        Assert.Equal(15, dropped);
        Assert.Equal(15, lane.DroppedPackets);
    }

    [Fact]
    public void Lifting_the_cap_drains_the_queue_immediately()
    {
        var lane = new FlowLane(PacketDirection.Inbound, Options, 0);
        lane.SetRate(10_000, 0);
        var owner = Owner();
        for (int i = 0; i < 5; i++) lane.Admit(Packet((byte)i), default, owner, 0, out _);
        Assert.True(lane.QueuedPackets > 0);

        lane.SetRate(0, 0);
        var injector = new RecordingInjector();
        Assert.Equal(-1, lane.Release(0, injector));
        Assert.Equal(0, lane.QueuedPackets);
    }

    [Fact]
    public void Failed_injection_counts_as_drop()
    {
        var lane = new FlowLane(PacketDirection.Inbound, Options, 0);
        lane.SetRate(10_000, 0);
        var owner = Owner();
        lane.Admit(Packet(0), default, owner, 0, out _);
        lane.Admit(Packet(0), default, owner, 0, out _);
        Assert.Equal(PacketVerdict.Queued, lane.Admit(Packet(0), default, owner, 0, out _));

        lane.Release(Ms(10_000), new RecordingInjector { Fail = true });
        Assert.Equal(1, owner.DroppedPackets);
        Assert.Equal(0, owner.TotalDownBytes);
    }
}

public class TrafficShaperTests
{
    private static ProcessTable Table() => new(pid => new ProcessIdentity(pid, pid switch
    {
        100 => "steam.exe",
        200 => "chrome.exe",
        _ => "other.exe",
    }, null, 0));

    private static PacketVerdict Send(TrafficShaper shaper, int pid, byte[] packet, bool canModify = true)
    {
        // An internet packet of the same size (local-network traffic is never shaped).
        byte[] real = TestPackets.Tcp(TestPackets.Server, TestPackets.Local, 443, 50000, [], size: packet.Length);
        return shaper.OnPacket(TestPackets.Context(pid, PacketDirection.Inbound, real, canModify));
    }

    [Fact]
    public void Only_the_limited_executable_is_shaped()
    {
        var table = Table();
        using var shaper = new TrafficShaper(table);
        shaper.Injector = new RecordingInjector();
        shaper.SetHardLimit("Steam", RateLimit.FromKBps(10, 0)); // normalised to steam.exe

        var packet = new byte[1500];
        var steamVerdicts = Enumerable.Range(0, 50).Select(_ => Send(shaper, 100, packet)).ToList();
        var chromeVerdicts = Enumerable.Range(0, 50).Select(_ => Send(shaper, 200, packet)).ToList();

        Assert.Contains(PacketVerdict.Queued, steamVerdicts);
        Assert.All(chromeVerdicts, v => Assert.Equal(PacketVerdict.Pass, v));
        Assert.True(shaper.RequiresDivert);
        Assert.Equal(RateLimit.FromKBps(10, 0), shaper.GetHardLimits()["steam.exe"]);
    }

    [Fact]
    public void Sniff_mode_never_queues_or_drops()
    {
        using var shaper = new TrafficShaper(Table());
        shaper.SetHardLimit("steam.exe", RateLimit.FromKBps(1, 1));
        var packet = new byte[1500];
        for (int i = 0; i < 100; i++)
            Assert.Equal(PacketVerdict.Pass, Send(shaper, 100, packet, canModify: false));
    }

    [Fact]
    public void Limit_added_later_applies_to_already_known_processes()
    {
        var table = Table();
        using var shaper = new TrafficShaper(table);
        var packet = new byte[1500];
        Assert.Equal(PacketVerdict.Pass, Send(shaper, 100, packet)); // steam.exe seen, no limit yet

        shaper.SetHardLimit("steam.exe", RateLimit.FromKBps(1, 0));
        var verdicts = Enumerable.Range(0, 20).Select(_ => Send(shaper, 100, packet)).ToList();
        Assert.Contains(PacketVerdict.Queued, verdicts);
    }

    [Fact]
    public void Scheduler_releases_queued_packets_at_roughly_the_cap()
    {
        var table = Table();
        using var shaper = new TrafficShaper(table, new ShapingOptions
        {
            MaxQueueDelay = TimeSpan.FromSeconds(5),
            MinBurstBytes = 1500,
            BurstDuration = TimeSpan.FromMilliseconds(10),
        });
        var injector = new RecordingInjector();
        shaper.Injector = injector;
        const int rate = 100 * 1024; // 100 KB/s
        shaper.SetHardLimit("steam.exe", new RateLimit(rate, 0));

        var packet = new byte[1000];
        int queued = Enumerable.Range(0, 60).Count(_ => Send(shaper, 100, packet) == PacketVerdict.Queued);
        Assert.True(queued > 50);

        var sw = Stopwatch.StartNew();
        while (injector.Sent.Count < queued && sw.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(5);
        sw.Stop();

        Assert.Equal(queued, injector.Sent.Count);
        // ~queued KB at 100 KB/s ⇒ ~0.55 s; allow scheduler jitter (Windows timer ≈ 15.6 ms).
        double expected = (queued - 1) * 1000.0 / rate;
        Assert.InRange(sw.Elapsed.TotalSeconds, expected * 0.7, expected + 0.5);

        table.TryGet(100, out ProcessTrafficState? steam);
        Assert.Equal(60 * 1000, steam!.TotalDownBytes);
    }

    [Fact]
    public void Unattributed_traffic_is_never_shaped()
    {
        using var shaper = new TrafficShaper(Table());
        shaper.SetHardLimit("(unattributed)", RateLimit.FromKBps(1, 1));
        var packet = new byte[1500];
        for (int i = 0; i < 50; i++)
            Assert.Equal(PacketVerdict.Pass, Send(shaper, ProcessResolver.UnattributedPid, packet));
    }
}
