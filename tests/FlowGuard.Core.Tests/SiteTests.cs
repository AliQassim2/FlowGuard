using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using FlowGuard.Core.Engine;
using FlowGuard.Core.Packets;
using FlowGuard.Core.Processes;
using FlowGuard.Core.Shaping;
using FlowGuard.Core.Sites;
using FlowGuard.Core.Stats;

namespace FlowGuard.Core.Tests;

public class SiteCatalogTests
{
    [Theory]
    [InlineData("youtube.com", true)]
    [InlineData("rr3---sn-4g5e6nzz.googlevideo.com", true)]
    [InlineData("WWW.YOUTUBE.COM", true)]
    [InlineData("notyoutube.com", false)]
    [InlineData("youtube.com.evil.net", false)]
    public void Matches_domains_and_subdomains_only(string domain, bool expected)
        => Assert.Equal(expected, SiteCatalog.Resolve("site:youtube")!.Matches(domain));

    [Theory]
    [InlineData("Steam", "Steam.exe")]
    [InlineData("SITE:YouTube", "site:youtube")]
    [InlineData("site:https://www.Example.com/watch?v=1", "site:example.com")]
    public void Normalises_keys(string input, string expected) => Assert.Equal(expected, TargetKey.Normalize(input));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", "site:youtube")]
    [InlineData("Netflix", "site:netflix")]
    [InlineData("nflxvideo.net", "site:netflix")]
    [InlineData("example.com:8443", "site:example.com")]
    [InlineData("not a domain", null)]
    public void Resolves_what_people_type(string text, string? expected)
        => Assert.Equal(expected, FlowGuardService.ResolveSiteKey(text));
}

public class ProtocolParserTests
{
    [Fact]
    public void Reads_dns_answers_against_the_question_name()
    {
        byte[] dns = TestPackets.DnsResponse("www.youtube.com", IPAddress.Parse("142.250.1.1"), IPAddress.Parse("142.250.1.2"));
        var seen = new List<(string, IpAddr)>();
        Assert.True(DnsParser.TryReadAnswers(dns, (n, a) => seen.Add((n, a))));
        Assert.Equal([("www.youtube.com", IpAddr.From(IPAddress.Parse("142.250.1.1"))),
                      ("www.youtube.com", IpAddr.From(IPAddress.Parse("142.250.1.2")))], seen);
    }

    [Fact]
    public void Ignores_dns_queries_and_garbage()
    {
        byte[] dns = TestPackets.DnsResponse("a.com", IPAddress.Loopback);
        dns[2] = 0x01; // QR = 0: a query
        Assert.False(DnsParser.TryReadAnswers(dns, (_, _) => { }));
        Assert.False(DnsParser.TryReadAnswers([1, 2, 3], (_, _) => { }));
    }

    [Fact]
    public void Reads_sni_from_a_real_tls_client_hello()
    {
        byte[] hello = RealHello.Tls("www.netflix.com");
        Assert.Equal(ParseResult.Found, TlsParser.TryGetSniFromRecords(hello, out string? sni));
        Assert.Equal("www.netflix.com", sni);

        // Any prefix is "need more", never a wrong answer.
        Assert.Equal(ParseResult.NeedMore, TlsParser.TryGetSniFromRecords(hello.AsSpan(0, hello.Length / 2), out _));
        Assert.Equal(ParseResult.No, TlsParser.TryGetSniFromRecords("GET / HTTP/1.1\r\n"u8, out _));
    }

    [Fact]
    public void Derives_the_rfc_9001_initial_keys()
    {
        using QuicInitialKeys k = QuicInitialKeys.Derive(QuicInitialKeys.Version1, Convert.FromHexString("8394c8f03e515708"));
        Assert.Equal("1f369613dd76d5467730efcbe3b1a22d", Convert.ToHexStringLower(k.Key));
        Assert.Equal("fa044b2f42a3fd3b46fb255c", Convert.ToHexStringLower(k.Iv));
        Assert.Equal("9f50449e04a0e810283a1e9933adedd2", Convert.ToHexStringLower(k.Hp));
    }

    [Theory]
    [InlineData(QuicInitialKeys.Version1)]
    [InlineData(QuicInitialKeys.Version2)]
    public void Reassembles_a_client_hello_split_and_reordered_across_quic_packets(uint version)
    {
        // Browsers spread big (post-quantum) hellos over two Initial packets and shuffle the
        // CRYPTO frames; the SNI can land in either.
        byte[] handshake = RealHello.Handshake("rr1---sn-abc.googlevideo.com");
        byte[] dcid = Convert.FromHexString("0011223344556677");
        int cut1 = handshake.Length / 3, cut2 = 2 * handshake.Length / 3;

        using QuicInitialKeys keys = QuicInitialKeys.Derive(version, dcid);
        byte[] p1 = QuicInitial.Protect(keys, dcid, [.. Crypto(cut2, handshake[cut2..]), .. Crypto(0, handshake[..cut1]), 0, 0, 0], 0);
        byte[] p2 = QuicInitial.Protect(keys, dcid, [1, .. Crypto(cut1, handshake[cut1..cut2]), 0, 0], 1);

        var crypto = new CryptoStreamBuffer();
        using QuicInitialKeys reader = QuicInitialKeys.Derive(version, dcid);
        Assert.True(QuicInitial.TryReadCrypto(p1, reader, crypto));
        Assert.Equal(ParseResult.NeedMore, TlsParser.TryGetSniFromHandshake(crypto.Prefix, out _));
        Assert.True(QuicInitial.TryReadCrypto(p2, reader, crypto));
        Assert.Equal(ParseResult.Found, TlsParser.TryGetSniFromHandshake(crypto.Prefix, out string? sni));
        Assert.Equal("rr1---sn-abc.googlevideo.com", sni);

        p2[^1] ^= 1; // tampered: authentication fails
        Assert.False(QuicInitial.TryReadCrypto(p2, reader, new CryptoStreamBuffer()));
    }

    [Fact]
    public async Task Reads_sni_from_a_real_quic_initial_packet()
    {
        if (!QuicConnection.IsSupported)
            return; // msquic not available on this machine

        List<byte[]> datagrams = await RealHello.QuicDatagrams("www.youtube.com");
        Assert.NotEmpty(datagrams);
        Assert.True(QuicInitial.IsInitial(datagrams[0], out uint version, out ReadOnlySpan<byte> dcid));

        using QuicInitialKeys keys = QuicInitialKeys.Derive(version, dcid);
        var crypto = new CryptoStreamBuffer();
        string? sni = null;
        foreach (byte[] d in datagrams)
        {
            QuicInitial.TryReadCrypto(d, keys, crypto);
            if (TlsParser.TryGetSniFromHandshake(crypto.Prefix, out sni) == ParseResult.Found)
                break;
        }
        Assert.Equal("www.youtube.com", sni);
    }

    private static byte[] Crypto(int offset, byte[] data)
    {
        var f = new List<byte> { 0x06 };
        f.AddRange(Varint(offset));
        f.AddRange(Varint(data.Length));
        f.AddRange(data);
        return f.ToArray();
    }

    private static byte[] Varint(int v) => v < 64 ? [(byte)v] : [(byte)(0x40 | (v >> 8)), (byte)v];
}

public class SiteClassifierTests
{
    private static readonly SiteDefinition YouTube = SiteCatalog.Resolve("site:youtube")!;
    private static readonly SiteDefinition Netflix = SiteCatalog.Resolve("site:netflix")!;

    private static SiteClassifier Classifier()
    {
        var c = new SiteClassifier();
        c.Configure([YouTube, Netflix]);
        return c;
    }

    private static SiteDefinition? Classify(SiteClassifier c, PacketDirection dir, byte[] packet)
        => c.Classify(TestPackets.Context(1, dir, packet));

    [Fact]
    public void Dns_answers_label_later_connections()
    {
        SiteClassifier c = Classifier();
        IPAddress video = IPAddress.Parse("142.250.9.9");
        byte[] dns = TestPackets.DnsResponse("youtube.com", video);
        Assert.Null(Classify(c, PacketDirection.Inbound, TestPackets.Udp(IPAddress.Parse("8.8.8.8"), TestPackets.Local, 53, 50000, dns)));

        byte[] data = TestPackets.Tcp(video, TestPackets.Local, 443, 51000, new byte[100]);
        Assert.Equal(YouTube, Classify(c, PacketDirection.Inbound, data));
    }

    [Fact]
    public void Tls_hello_split_over_two_segments_labels_the_connection()
    {
        SiteClassifier c = Classifier();
        byte[] hello = RealHello.Tls("assets.nflxext.com");
        int cut = hello.Length / 2;
        byte[] s1 = TestPackets.Tcp(TestPackets.Local, TestPackets.Server, 52000, 443, hello.AsSpan(0, cut), seq: 5000);
        byte[] s2 = TestPackets.Tcp(TestPackets.Local, TestPackets.Server, 52000, 443, hello.AsSpan(cut), seq: (uint)(5000 + cut));

        Assert.Null(Classify(c, PacketDirection.Outbound, s1));
        Assert.Null(Classify(c, PacketDirection.Outbound, s1));                    // retransmission: skipped
        Assert.Equal(Netflix, Classify(c, PacketDirection.Outbound, s2));
        byte[] reply = TestPackets.Tcp(TestPackets.Server, TestPackets.Local, 443, 52000, new byte[1400]);
        Assert.Equal(Netflix, Classify(c, PacketDirection.Inbound, reply));        // the server's data too
    }

    [Fact]
    public void Quic_initial_labels_the_connection()
    {
        SiteClassifier c = Classifier();
        byte[] dcid = Convert.FromHexString("a1a2a3a4a5a6a7a8");
        using QuicInitialKeys keys = QuicInitialKeys.Derive(QuicInitialKeys.Version1, dcid);
        byte[] handshake = RealHello.Handshake("rr5---sn-x.googlevideo.com");
        byte[] frames = [0x06, 0x00, (byte)(0x40 | (handshake.Length >> 8)), (byte)handshake.Length, .. handshake];
        byte[] initial = QuicInitial.Protect(keys, dcid, frames, 0);

        byte[] packet = TestPackets.Udp(TestPackets.Local, TestPackets.Server, 53000, 443, initial);
        Assert.Equal(YouTube, Classify(c, PacketDirection.Outbound, packet));
        Assert.Equal(1, c.RecognisedCount);
    }

    [Fact]
    public void Unknown_sites_and_plain_udp_stay_with_the_app()
    {
        SiteClassifier c = Classifier();
        byte[] hello = RealHello.Tls("www.example.org");
        Assert.Null(Classify(c, PacketDirection.Outbound, TestPackets.Tcp(TestPackets.Local, TestPackets.Server, 52001, 443, hello)));
        Assert.Null(Classify(c, PacketDirection.Outbound, TestPackets.Udp(TestPackets.Local, TestPackets.Server, 53001, 27015, new byte[64])));
    }

    [Fact]
    public void Reconfiguring_relabels_known_connections()
    {
        var c = new SiteClassifier();
        c.Configure([Netflix]);
        byte[] hello = RealHello.Tls("www.youtube.com");
        byte[] packet = TestPackets.Tcp(TestPackets.Local, TestPackets.Server, 52002, 443, hello);
        Assert.Null(Classify(c, PacketDirection.Outbound, packet));
        c.Configure([Netflix, YouTube]);
        Assert.Equal(YouTube, Classify(c, PacketDirection.Outbound, packet));
    }
}

public class SiteRoutingTests
{
    [Fact]
    public void Tracked_site_traffic_is_counted_and_shaped_apart_from_the_browser()
    {
        var table = new ProcessTable(pid => new ProcessIdentity(pid, "chrome.exe", null, 0));
        using var shaper = new TrafficShaper(table);
        shaper.Injector = new RecordingInjector();
        shaper.SetTrackedSites([SiteCatalog.Resolve("site:youtube")!]);
        shaper.SetHardLimit("site:youtube", RateLimit.FromKBps(10, 0));

        IPAddress video = IPAddress.Parse("142.250.9.9");
        shaper.OnPacket(TestPackets.Context(200, PacketDirection.Inbound,
            TestPackets.Udp(IPAddress.Parse("8.8.8.8"), TestPackets.Local, 53, 50000, TestPackets.DnsResponse("youtube.com", video))));

        var verdicts = Enumerable.Range(0, 30).Select(_ => shaper.OnPacket(TestPackets.Context(200, PacketDirection.Inbound,
            TestPackets.Tcp(video, TestPackets.Local, 443, 51000, [], size: 1500)))).ToList();
        Assert.Contains(PacketVerdict.Queued, verdicts);

        // Other Chrome traffic is untouched.
        var other = Enumerable.Range(0, 30).Select(_ => shaper.OnPacket(TestPackets.Context(200, PacketDirection.Inbound,
            TestPackets.Tcp(TestPackets.Server, TestPackets.Local, 443, 51001, [], size: 1500)))).ToList();
        Assert.All(other, v => Assert.Equal(PacketVerdict.Pass, v));

        ProcessTrafficState site = shaper.SiteRoutes.Single().State;
        Assert.True(site.IsSite);
        Assert.Equal("site:youtube", site.Name);
        Assert.True(site.TotalDownBytes > 0);
        Assert.True(table.TryGet(200, out ProcessTrafficState? chrome));
        Assert.Equal(30 * 1500 + 28 + TestPackets.DnsResponse("youtube.com", video).Length, chrome!.TotalDownBytes);
    }

    [Fact]
    public void Local_network_dns_and_pings_are_never_shaped()
    {
        var table = new ProcessTable(pid => new ProcessIdentity(pid, "steam.exe", null, 0));
        using var shaper = new TrafficShaper(table);
        shaper.Injector = new RecordingInjector();
        shaper.SetHardLimit("steam.exe", RateLimit.FromKBps(1, 1));

        IPAddress nas = IPAddress.Parse("192.168.1.50");
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(PacketVerdict.Pass, shaper.OnPacket(TestPackets.Context(100, PacketDirection.Inbound,
                TestPackets.Tcp(nas, TestPackets.Local, 445, 50000, [], size: 1500))));
            Assert.Equal(PacketVerdict.Pass, shaper.OnPacket(TestPackets.Context(100, PacketDirection.Outbound,
                TestPackets.Udp(TestPackets.Local, IPAddress.Parse("8.8.8.8"), 50001, 53, new byte[600]))));
        }
        Assert.Equal(50 * 628, shaper.InternetUpBytes);     // DNS crossed the line (counted)…
        Assert.Equal(0, shaper.InternetDownBytes);          // …the NAS did not
    }
}

/// <summary>Real ClientHellos produced by the OS TLS stack (SChannel) and by msquic.</summary>
internal static class RealHello
{
    private static readonly Dictionary<string, byte[]> Cache = [];

    /// <summary>The TLS records a client sends first when connecting to <paramref name="host"/>.</summary>
    public static byte[] Tls(string host)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(host, out byte[]? cached))
                return cached;

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            using TcpClient server = listener.AcceptTcpClient();
            using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            Task auth = ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host });

            var received = new MemoryStream();
            byte[] buffer = new byte[16384];
            server.ReceiveTimeout = 5000;
            NetworkStream stream = server.GetStream();
            while (TlsParser.TryGetSniFromRecords(received.ToArray(), out _) == ParseResult.NeedMore)
            {
                int n = stream.Read(buffer);
                if (n == 0) break;
                received.Write(buffer, 0, n);
            }
            client.Close();
            try { auth.Wait(2000); } catch { /* the handshake can't complete: expected */ }
            return Cache[host] = received.ToArray();
        }
    }

    /// <summary>The ClientHello handshake message (what QUIC carries in CRYPTO frames).</summary>
    public static byte[] Handshake(string host)
    {
        byte[] records = Tls(host);
        var hs = new List<byte>();
        for (int p = 0; p + 5 <= records.Length;)
        {
            int len = (records[p + 3] << 8) | records[p + 4];
            hs.AddRange(records.AsSpan(p + 5, Math.Min(len, records.Length - p - 5)).ToArray());
            p += 5 + len;
        }
        return hs.ToArray();
    }

    /// <summary>The first UDP datagrams msquic sends when connecting to <paramref name="host"/>.</summary>
    public static async Task<List<byte[]>> QuicDatagrams(string host)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = (IPEndPoint)udp.Client.LocalEndPoint!,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = [SslApplicationProtocol.Http3],
            },
        };
        Task connect = QuicConnection.ConnectAsync(options, cts.Token).AsTask();

        var datagrams = new List<byte[]>();
        try
        {
            while (datagrams.Count < 4)
            {
                UdpReceiveResult r = await udp.ReceiveAsync(cts.Token);
                datagrams.Add(r.Buffer);
                if (datagrams.Count >= 2)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Cancel();
            try { await connect; } catch { /* no server: expected */ }
        }
        return datagrams;
    }
}
