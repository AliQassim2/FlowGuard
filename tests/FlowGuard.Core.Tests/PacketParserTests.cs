using System.Buffers.Binary;
using FlowGuard.Core.Packets;

namespace FlowGuard.Core.Tests;

public class PacketParserTests
{
    private static byte[] Ipv4(byte protocol, ushort srcPort, ushort dstPort, int payload = 20, ushort fragField = 0)
    {
        int l4 = protocol == 6 ? 20 : 8;
        var p = new byte[20 + l4 + payload];
        p[0] = 0x45;                                              // v4, IHL 5
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), fragField);
        p[8] = 64;
        p[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), dstPort);
        return p;
    }

    private static byte[] Ipv6(byte nextHeader, ushort srcPort, ushort dstPort, byte[]? extension = null, byte extNext = 0)
    {
        byte[] ext = extension ?? [];
        var p = new byte[40 + ext.Length + 8 + 10];
        p[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)(p.Length - 40));
        p[6] = extension is null ? nextHeader : extNext;
        p[7] = 64;
        ext.CopyTo(p, 40);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(40 + ext.Length), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(42 + ext.Length), dstPort);
        return p;
    }

    [Fact]
    public void Parses_ipv4_tcp()
    {
        byte[] p = Ipv4(6, 51000, 443);
        Assert.True(PacketParser.TryParse(p, out PacketHeader h));
        Assert.Equal(4, h.IpVersion);
        Assert.Equal(TransportProtocol.Tcp, h.Protocol);
        Assert.True(h.HasPorts);
        Assert.Equal(51000, h.SourcePort);
        Assert.Equal(443, h.DestinationPort);
        Assert.Equal(p.Length, h.TotalLength);
        Assert.Equal(51000, h.LocalPort(PacketDirection.Outbound));
        Assert.Equal(443, h.LocalPort(PacketDirection.Inbound));
    }

    [Fact]
    public void Parses_ipv4_udp()
    {
        Assert.True(PacketParser.TryParse(Ipv4(17, 443, 60000), out PacketHeader h));
        Assert.Equal(TransportProtocol.Udp, h.Protocol);
        Assert.Equal(60000, h.LocalPort(PacketDirection.Inbound));
    }

    [Fact]
    public void Non_first_ipv4_fragment_has_no_ports()
    {
        Assert.True(PacketParser.TryParse(Ipv4(17, 1, 2, fragField: 0x00B9), out PacketHeader h));
        Assert.False(h.HasPorts);
        Assert.Equal(TransportProtocol.Udp, h.Protocol);
    }

    [Fact]
    public void Icmp_is_parsed_without_ports()
    {
        Assert.True(PacketParser.TryParse(Ipv4(1, 0, 0), out PacketHeader h));
        Assert.Equal(TransportProtocol.Unknown, h.Protocol);
        Assert.False(h.HasPorts);
    }

    [Fact]
    public void Parses_ipv6_udp_quic()
    {
        byte[] p = Ipv6(17, 55555, 443);
        Assert.True(PacketParser.TryParse(p, out PacketHeader h));
        Assert.Equal(6, h.IpVersion);
        Assert.Equal(TransportProtocol.Udp, h.Protocol);
        Assert.Equal(55555, h.SourcePort);
        Assert.Equal(443, h.DestinationPort);
        Assert.Equal(p.Length, h.TotalLength);
    }

    [Fact]
    public void Walks_ipv6_hop_by_hop_extension()
    {
        byte[] hopByHop = new byte[8];
        hopByHop[0] = 6; // next header: TCP
        hopByHop[1] = 0; // (0 + 1) * 8 = 8 bytes
        Assert.True(PacketParser.TryParse(Ipv6(0, 40000, 80, hopByHop, extNext: 0), out PacketHeader h));
        Assert.Equal(TransportProtocol.Tcp, h.Protocol);
        Assert.Equal(40000, h.SourcePort);
        Assert.Equal(80, h.DestinationPort);
    }

    [Fact]
    public void Ipv6_non_first_fragment_has_no_ports()
    {
        byte[] frag = new byte[8];
        frag[0] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(frag.AsSpan(2), 0x0100); // offset != 0
        Assert.True(PacketParser.TryParse(Ipv6(0, 1, 2, frag, extNext: 44), out PacketHeader h));
        Assert.False(h.HasPorts);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x45, 0, 0 })]
    [InlineData(new byte[] { 0x10, 0, 0, 0 })]
    public void Rejects_truncated_or_unknown(byte[] packet)
    {
        Assert.False(PacketParser.TryParse(packet, out _));
    }

    [Fact]
    public void Truncated_transport_header_is_reported_without_ports()
    {
        byte[] p = Ipv4(6, 1, 2)[..22]; // IPv4 header + 2 bytes
        Assert.True(PacketParser.TryParse(p, out PacketHeader h));
        Assert.False(h.HasPorts);
    }
}
