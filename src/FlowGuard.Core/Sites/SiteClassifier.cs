using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using FlowGuard.Core.Engine;
using FlowGuard.Core.Packets;

namespace FlowGuard.Core.Sites;

/// <summary>
/// Works out which website each connection belongs to, so traffic from one browser can be split
/// per site (all tabs share one network process, so the PID can't tell them apart).
/// <para>
/// Sources, best first: the server name in the TLS ClientHello (TCP) or in the QUIC Initial
/// packet (UDP, e.g. YouTube video), both readable without any secrets; then DNS answers seen
/// earlier for the remote address. Sites using Encrypted Client Hello together with encrypted
/// DNS can't be recognised.
/// </para>
/// Hot path: one dictionary lookup per packet once a connection is known.
/// </summary>
public sealed class SiteClassifier
{
    private const int MaxAddressCache = 50_000;
    private const int MaxFlows = 200_000;
    private const int MaxHelloPackets = 8;

    private readonly record struct FlowKey(TransportProtocol Protocol, ushort LocalPort, ushort RemotePort, IpAddr Remote);

    private sealed class Flow
    {
        public string? Domain;
        public SiteDefinition? Site;
        public int SiteVersion = -1;
        public long LastSeen;
        public bool HelloDone;
        public int HelloPackets;
        // TCP: the client's first bytes, in sequence order.
        public byte[]? Stream;
        public int StreamLength;
        public uint StreamSeq;
        // QUIC: keys derived from the first Initial packet, and the CRYPTO stream.
        public QuicInitialKeys? Keys;
        public CryptoStreamBuffer? Crypto;
    }

    private readonly ConcurrentDictionary<FlowKey, Flow> _flows = new();
    private readonly ConcurrentDictionary<IpAddr, string> _addressNames = new();
    private readonly ConcurrentDictionary<string, SiteDefinition?> _domainSites = new(StringComparer.OrdinalIgnoreCase);
    private volatile SiteDefinition[] _sites = [];
    private int _version;
    private long _recognised;

    /// <summary>True while at least one site is tracked (otherwise packets skip classification).</summary>
    public bool IsEnabled => _sites.Length > 0;

    public IReadOnlyList<SiteDefinition> Sites => _sites;

    public int FlowCount => _flows.Count;

    /// <summary>Connections whose site was recognised (diagnostics).</summary>
    public long RecognisedCount => Interlocked.Read(ref _recognised);

    /// <summary>Raised (rarely, off the hot path's fast branch) when a connection is matched to a site.</summary>
    public event Action<SiteDefinition, string>? Recognised;

    public void Configure(IEnumerable<SiteDefinition> sites)
    {
        _sites = sites.DistinctBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        _domainSites.Clear();
        Interlocked.Increment(ref _version);
    }

    /// <summary>Learns name → address pairs from DNS responses and the site of each connection.</summary>
    /// <returns>The tracked site the packet's connection belongs to, or null.</returns>
    public SiteDefinition? Classify(in PacketContext context)
    {
        PacketHeader h = context.Header;
        if (!h.HasPorts || h.PayloadOffset < 0)
            return null;

        PacketDirection dir = context.Direction;
        ReadOnlySpan<byte> payload = context.Packet.Slice(h.PayloadOffset);

        if (h.Protocol == TransportProtocol.Udp && dir == PacketDirection.Inbound && h.SourcePort == 53)
        {
            if (payload.Length > 0)
                DnsParser.TryReadAnswers(payload, Remember);
            return null;
        }

        var key = new FlowKey(h.Protocol, h.LocalPort(dir), h.RemotePort(dir), h.RemoteAddress(dir));
        long now = Stopwatch.GetTimestamp();
        if (!_flows.TryGetValue(key, out Flow? flow))
        {
            if (_flows.Count >= MaxFlows)
                return null;
            flow = _flows.GetOrAdd(key, _ => new Flow
            {
                Domain = _addressNames.GetValueOrDefault(key.Remote),
            });
        }
        flow.LastSeen = now;

        if (!flow.HelloDone && dir == PacketDirection.Outbound && payload.Length > 0)
            ReadHello(flow, in h, context.Packet, payload, key.Remote);

        int version = Volatile.Read(ref _version);
        if (flow.SiteVersion != version)
        {
            flow.Site = flow.Domain is null ? null : Match(flow.Domain);
            flow.SiteVersion = version;
        }
        return flow.Site;
    }

    /// <summary>Forgets connections idle for longer than <paramref name="idle"/>.</summary>
    public void Prune(TimeSpan idle)
    {
        long cutoff = Stopwatch.GetTimestamp() - (long)(idle.TotalSeconds * Stopwatch.Frequency);
        foreach ((FlowKey key, Flow flow) in _flows)
        {
            if (flow.LastSeen < cutoff && _flows.TryRemove(key, out Flow? removed))
                lock (removed) removed.Keys?.Dispose();
        }
        if (_addressNames.Count > MaxAddressCache)
            _addressNames.Clear();
    }

    /// <summary>Remembers that <paramref name="address"/> serves <paramref name="domain"/>.</summary>
    public void Remember(string domain, IpAddr address)
    {
        if (_addressNames.Count < MaxAddressCache)
            _addressNames[address] = domain;
    }

    public SiteDefinition? Match(string domain)
    {
        if (_domainSites.TryGetValue(domain, out SiteDefinition? cached))
            return cached;
        SiteDefinition? match = null;
        foreach (SiteDefinition s in _sites)
        {
            if (s.Matches(domain))
            {
                match = s;
                break;
            }
        }
        if (_domainSites.Count < 10_000)
            _domainSites[domain] = match;
        return match;
    }

    // ------------------------------------------------------------------ ClientHello

    private void ReadHello(Flow flow, in PacketHeader h, ReadOnlySpan<byte> packet, ReadOnlySpan<byte> payload, IpAddr remote)
    {
        lock (flow)
        {
            if (flow.HelloDone)
                return;
            ParseResult result;
            string? sni;
            if (h.Protocol == TransportProtocol.Tcp)
                result = ReadTcpHello(flow, packet.Slice(h.TransportOffset), payload, out sni);
            else
                result = ReadQuicHello(flow, payload, out sni);

            if (result == ParseResult.NeedMore && ++flow.HelloPackets < MaxHelloPackets)
                return;

            flow.HelloDone = true;
            flow.Stream = null;
            flow.Crypto = null;
            flow.Keys?.Dispose();
            flow.Keys = null;
            if (result == ParseResult.Found && sni is not null)
            {
                flow.Domain = sni;
                flow.SiteVersion = -1;
                Remember(sni, remote);
                Interlocked.Increment(ref _recognised);
                if (Recognised is { } handler && Match(sni) is { } site)
                    handler(site, sni);
            }
        }
    }

    private static ParseResult ReadTcpHello(Flow flow, ReadOnlySpan<byte> tcp, ReadOnlySpan<byte> payload, out string? sni)
    {
        sni = null;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(tcp.Slice(4));
        if (flow.Stream is null)
        {
            if (payload[0] != 0x16)
                return ParseResult.No; // not TLS
            ParseResult first = TlsParser.TryGetSniFromRecords(payload, out sni);
            if (first != ParseResult.NeedMore)
                return first;
            flow.Stream = new byte[TlsParser.MaxHelloBytes];
            flow.StreamSeq = seq;
            int n = Math.Min(payload.Length, flow.Stream.Length);
            payload.Slice(0, n).CopyTo(flow.Stream);
            flow.StreamLength = n;
            return ParseResult.NeedMore;
        }

        // Append only the next in-order segment; retransmissions and reordering are skipped.
        if (seq != unchecked(flow.StreamSeq + (uint)flow.StreamLength))
            return ParseResult.NeedMore;
        int count = Math.Min(payload.Length, flow.Stream.Length - flow.StreamLength);
        if (count <= 0)
            return ParseResult.No;
        payload.Slice(0, count).CopyTo(flow.Stream.AsSpan(flow.StreamLength));
        flow.StreamLength += count;
        return TlsParser.TryGetSniFromRecords(flow.Stream.AsSpan(0, flow.StreamLength), out sni);
    }

    private static ParseResult ReadQuicHello(Flow flow, ReadOnlySpan<byte> payload, out string? sni)
    {
        sni = null;
        if (!QuicInitial.IsInitial(payload, out uint version, out ReadOnlySpan<byte> dcid))
            return flow.Keys is null ? ParseResult.No : ParseResult.NeedMore;

        // Initial keys come from the DCID of the client's very first Initial packet.
        flow.Keys ??= QuicInitialKeys.Derive(version, dcid);
        flow.Crypto ??= new CryptoStreamBuffer();
        if (!QuicInitial.TryReadCrypto(payload, flow.Keys, flow.Crypto))
            return ParseResult.NeedMore;
        return TlsParser.TryGetSniFromHandshake(flow.Crypto.Prefix, out sni);
    }
}
