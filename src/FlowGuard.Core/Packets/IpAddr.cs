using System.Buffers.Binary;
using System.Net;

namespace FlowGuard.Core.Packets;

/// <summary>
/// Allocation-free IPv4/IPv6 address (IPv4 stored as IPv4-mapped IPv6), usable as a dictionary key.
/// </summary>
public readonly record struct IpAddr(ulong Hi, ulong Lo)
{
    private const ulong V4Prefix = 0x0000_FFFF_0000_0000UL;

    public static IpAddr FromV4(ReadOnlySpan<byte> b) => new(0, V4Prefix | BinaryPrimitives.ReadUInt32BigEndian(b));

    public static IpAddr FromV6(ReadOnlySpan<byte> b)
        => new(BinaryPrimitives.ReadUInt64BigEndian(b), BinaryPrimitives.ReadUInt64BigEndian(b.Slice(8)));

    public static IpAddr From(IPAddress address)
    {
        Span<byte> b = stackalloc byte[16];
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address.TryWriteBytes(b, out _);
            return FromV4(b);
        }
        address.TryWriteBytes(b, out _);
        return FromV6(b);
    }

    public bool IsV4 => Hi == 0 && (Lo >> 32) == 0xFFFF;

    private uint V4 => (uint)Lo;

    /// <summary>
    /// False for loopback, private (RFC 1918 / ULA), link-local, multicast and broadcast
    /// addresses: traffic that never crosses the internet line.
    /// </summary>
    public bool IsInternet
    {
        get
        {
            if (IsV4)
            {
                uint a = V4;
                byte first = (byte)(a >> 24);
                return !(first is 0 or 10 or 127 or >= 224 ||
                         (a & 0xFFF0_0000) == 0xAC10_0000 ||   // 172.16.0.0/12
                         (a & 0xFFFF_0000) == 0xC0A8_0000 ||   // 192.168.0.0/16
                         (a & 0xFFFF_0000) == 0xA9FE_0000);    // 169.254.0.0/16
            }
            if (Hi == 0 && Lo is 0 or 1)
                return false;                                   // :: and ::1
            ushort top = (ushort)(Hi >> 48);
            return !((top & 0xFFC0) == 0xFE80 ||                // link-local
                     (top & 0xFE00) == 0xFC00 ||                // unique local
                     (top & 0xFF00) == 0xFF00);                 // multicast
        }
    }

    public IPAddress ToIPAddress()
    {
        if (IsV4)
            return new IPAddress(BinaryPrimitives.ReverseEndianness(V4));
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(b, Hi);
        BinaryPrimitives.WriteUInt64BigEndian(b.Slice(8), Lo);
        return new IPAddress(b);
    }

    public override string ToString() => ToIPAddress().ToString();
}
