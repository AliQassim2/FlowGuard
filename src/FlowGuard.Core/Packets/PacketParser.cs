using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FlowGuard.Core.Packets;

public enum TransportProtocol : byte
{
    Unknown = 0,
    Tcp = 6,
    Udp = 17,
}

public enum PacketDirection : byte
{
    Outbound = 0,
    Inbound = 1,
}

/// <summary>Header facts extracted from a raw IP packet. Plain value type — no allocations.</summary>
public readonly struct PacketHeader
{
    public PacketHeader(byte ipVersion, TransportProtocol protocol, int totalLength,
        ushort sourcePort, ushort destinationPort, bool hasPorts,
        IpAddr source = default, IpAddr destination = default, int transportOffset = 0, int payloadOffset = -1)
    {
        IpVersion = ipVersion;
        Protocol = protocol;
        TotalLength = totalLength;
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        HasPorts = hasPorts;
        Source = source;
        Destination = destination;
        TransportOffset = transportOffset;
        PayloadOffset = payloadOffset;
    }

    public IpAddr Source { get; }
    public IpAddr Destination { get; }

    /// <summary>Offset of the TCP/UDP header inside the packet (valid when <see cref="HasPorts"/>).</summary>
    public int TransportOffset { get; }

    /// <summary>Offset of the TCP/UDP payload, or -1 when the header is truncated or absent.</summary>
    public int PayloadOffset { get; }

    /// <summary>The far end of the connection for the given direction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IpAddr RemoteAddress(PacketDirection direction)
        => direction == PacketDirection.Outbound ? Destination : Source;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort RemotePort(PacketDirection direction)
        => direction == PacketDirection.Outbound ? DestinationPort : SourcePort;

    public byte IpVersion { get; }
    public TransportProtocol Protocol { get; }

    /// <summary>Full IP packet length in bytes (header + payload).</summary>
    public int TotalLength { get; }

    public ushort SourcePort { get; }
    public ushort DestinationPort { get; }

    /// <summary>False for non-TCP/UDP packets and for non-first IP fragments.</summary>
    public bool HasPorts { get; }

    /// <summary>The port that belongs to this machine for the given direction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort LocalPort(PacketDirection direction)
        => direction == PacketDirection.Outbound ? SourcePort : DestinationPort;
}

/// <summary>
/// Zero-allocation IPv4 / IPv6 + TCP / UDP header parser operating on <see cref="ReadOnlySpan{T}"/>.
/// </summary>
public static class PacketParser
{
    private const int MaxIpv6ExtensionHeaders = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> packet, out PacketHeader header)
    {
        if (packet.Length >= 1)
        {
            int version = packet[0] >> 4;
            if (version == 4) return TryParseIpv4(packet, out header);
            if (version == 6) return TryParseIpv6(packet, out header);
        }
        header = default;
        return false;
    }

    private static bool TryParseIpv4(ReadOnlySpan<byte> p, out PacketHeader header)
    {
        header = default;
        if (p.Length < 20)
            return false;

        int ihl = (p[0] & 0x0F) * 4;
        if (ihl < 20 || ihl > p.Length)
            return false;

        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(2, 2));
        if (totalLength == 0) // TSO/LSO can leave this zero; fall back to captured length
            totalLength = p.Length;

        var protocol = (TransportProtocol)p[9];
        int fragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(6, 2)) & 0x1FFF;

        bool isTransport = protocol is TransportProtocol.Tcp or TransportProtocol.Udp;
        if (!isTransport)
            protocol = TransportProtocol.Unknown;

        IpAddr src = IpAddr.FromV4(p.Slice(12, 4));
        IpAddr dst = IpAddr.FromV4(p.Slice(16, 4));
        if (!isTransport || fragmentOffset != 0 || p.Length < ihl + 4)
        {
            header = new PacketHeader(4, protocol, totalLength, 0, 0, hasPorts: false, src, dst);
            return true;
        }

        ReadOnlySpan<byte> l4 = p.Slice(ihl);
        header = new PacketHeader(4, protocol, totalLength,
            BinaryPrimitives.ReadUInt16BigEndian(l4),
            BinaryPrimitives.ReadUInt16BigEndian(l4.Slice(2)),
            hasPorts: true, src, dst, ihl, PayloadOffset(p, ihl, protocol));
        return true;
    }

    private static bool TryParseIpv6(ReadOnlySpan<byte> p, out PacketHeader header)
    {
        header = default;
        if (p.Length < 40)
            return false;

        int totalLength = 40 + BinaryPrimitives.ReadUInt16BigEndian(p.Slice(4, 2));
        byte next = p[6];
        int offset = 40;
        bool fragmentedTail = false;

        // Walk extension headers until we reach the transport header.
        for (int i = 0; i < MaxIpv6ExtensionHeaders; i++)
        {
            switch (next)
            {
                case 0:   // Hop-by-Hop
                case 43:  // Routing
                case 60:  // Destination Options
                    if (p.Length < offset + 2) goto done;
                    next = p[offset];
                    offset += (p[offset + 1] + 1) * 8;
                    continue;

                case 44:  // Fragment
                    if (p.Length < offset + 8) goto done;
                    next = p[offset];
                    fragmentedTail = (BinaryPrimitives.ReadUInt16BigEndian(p.Slice(offset + 2, 2)) & 0xFFF8) != 0;
                    offset += 8;
                    continue;

                case 51:  // Authentication Header
                    if (p.Length < offset + 2) goto done;
                    next = p[offset];
                    offset += (p[offset + 1] + 2) * 4;
                    continue;
            }
            break;
        }
    done:;

        var protocol = next is 6 or 17 ? (TransportProtocol)next : TransportProtocol.Unknown;
        IpAddr src = IpAddr.FromV6(p.Slice(8, 16));
        IpAddr dst = IpAddr.FromV6(p.Slice(24, 16));
        if (protocol == TransportProtocol.Unknown || fragmentedTail || p.Length < offset + 4)
        {
            header = new PacketHeader(6, protocol, totalLength, 0, 0, hasPorts: false, src, dst);
            return true;
        }

        ReadOnlySpan<byte> l4 = p.Slice(offset);
        header = new PacketHeader(6, protocol, totalLength,
            BinaryPrimitives.ReadUInt16BigEndian(l4),
            BinaryPrimitives.ReadUInt16BigEndian(l4.Slice(2)),
            hasPorts: true, src, dst, offset, PayloadOffset(p, offset, protocol));
        return true;
    }

    private static int PayloadOffset(ReadOnlySpan<byte> p, int l4, TransportProtocol protocol)
    {
        if (protocol == TransportProtocol.Udp)
            return p.Length >= l4 + 8 ? l4 + 8 : -1;
        if (p.Length < l4 + 20)
            return -1;
        int dataOffset = (p[l4 + 12] >> 4) * 4;
        return dataOffset >= 20 && p.Length >= l4 + dataOffset ? l4 + dataOffset : -1;
    }
}
