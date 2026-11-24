using System.Buffers.Binary;
using System.Net;
using FlowGuard.Core.Engine;
using FlowGuard.Core.Packets;

namespace FlowGuard.Core.Tests;

/// <summary>Builds raw IPv4 TCP / UDP packets for handler tests.</summary>
internal static class TestPackets
{
    public static readonly IPAddress Local = IPAddress.Parse("192.168.1.20");
    public static readonly IPAddress Server = IPAddress.Parse("142.250.1.1");

    public static byte[] Tcp(IPAddress src, IPAddress dst, ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload,
        uint seq = 1000, int size = 0)
    {
        int length = Math.Max(20 + 20 + payload.Length, size);
        var p = Ip(src, dst, 6, length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(24), seq);
        p[32] = 5 << 4;                    // data offset: 20 bytes
        p[33] = 0x18;                      // PSH, ACK
        payload.CopyTo(p.AsSpan(40));
        return p;
    }

    public static byte[] Udp(IPAddress src, IPAddress dst, ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        var p = Ip(src, dst, 17, 20 + 8 + payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), (ushort)(8 + payload.Length));
        payload.CopyTo(p.AsSpan(28));
        return p;
    }

    private static byte[] Ip(IPAddress src, IPAddress dst, byte protocol, int length)
    {
        var p = new byte[length];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)length);
        p[8] = 64;
        p[9] = protocol;
        src.GetAddressBytes().CopyTo(p, 12);
        dst.GetAddressBytes().CopyTo(p, 16);
        return p;
    }

    public static PacketContext Context(int pid, PacketDirection direction, byte[] packet, bool canModify = true)
    {
        Assert.True(PacketParser.TryParse(packet, out PacketHeader header));
        return new PacketContext(pid, direction, header, packet, default(PacketMeta), canModify);
    }

    /// <summary>A minimal DNS response: one question, then the given A records.</summary>
    public static byte[] DnsResponse(string name, params IPAddress[] answers)
    {
        var b = new List<byte> { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, (byte)answers.Length, 0, 0, 0, 0 };
        foreach (string label in name.Split('.'))
        {
            b.Add((byte)label.Length);
            b.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        b.AddRange([0, 0, 1, 0, 1]);
        foreach (IPAddress a in answers)
        {
            b.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]); // pointer to the question name
            b.AddRange(a.GetAddressBytes());
        }
        return b.ToArray();
    }
}
