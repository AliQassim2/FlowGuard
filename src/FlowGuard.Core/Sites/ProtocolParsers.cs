using System.Buffers.Binary;
using System.Text;
using FlowGuard.Core.Packets;

namespace FlowGuard.Core.Sites;

/// <summary>Reads the A / AAAA answers of a DNS response (UDP payload).</summary>
public static class DnsParser
{
    /// <summary>
    /// Calls <paramref name="onAddress"/> for every A / AAAA record in a response, paired with the
    /// name that was asked for (not the CNAME target): rules are written against the names
    /// people type, e.g. youtube.com rather than youtube-ui.l.google.com.
    /// </summary>
    /// <returns>False when the payload isn't a well-formed response.</returns>
    public static bool TryReadAnswers(ReadOnlySpan<byte> dns, Action<string, IpAddr> onAddress)
    {
        if (dns.Length < 12 || (dns[2] & 0x80) == 0)   // QR bit: must be a response
            return false;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(dns.Slice(4));
        int answers = BinaryPrimitives.ReadUInt16BigEndian(dns.Slice(6));
        if (questions != 1 || answers == 0)
            return false;

        int p = 12;
        string? question = ReadName(dns, ref p);
        if (question is null || p + 4 > dns.Length)
            return false;
        p += 4; // QTYPE, QCLASS

        for (int i = 0; i < answers && i < 64; i++)
        {
            if (!SkipName(dns, ref p) || p + 10 > dns.Length)
                return false;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(dns.Slice(p));
            int length = BinaryPrimitives.ReadUInt16BigEndian(dns.Slice(p + 8));
            p += 10;
            if (p + length > dns.Length)
                return false;
            if (type == 1 && length == 4)
                onAddress(question, IpAddr.FromV4(dns.Slice(p, 4)));
            else if (type == 28 && length == 16)
                onAddress(question, IpAddr.FromV6(dns.Slice(p, 16)));
            p += length;
        }
        return true;
    }

    private static string? ReadName(ReadOnlySpan<byte> dns, ref int p)
    {
        var sb = new StringBuilder(64);
        int jumps = 0, cursor = p;
        bool jumped = false;
        while (cursor < dns.Length)
        {
            byte len = dns[cursor];
            if (len == 0)
            {
                if (!jumped) p = cursor + 1;
                return sb.Length == 0 ? null : sb.ToString().ToLowerInvariant();
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= dns.Length || ++jumps > 8) return null;
                if (!jumped) p = cursor + 2;
                jumped = true;
                cursor = ((len & 0x3F) << 8) | dns[cursor + 1];
                continue;
            }
            if (cursor + 1 + len > dns.Length || sb.Length + len > 253) return null;
            if (sb.Length > 0) sb.Append('.');
            foreach (byte b in dns.Slice(cursor + 1, len)) sb.Append((char)b);
            cursor += 1 + len;
        }
        return null;
    }

    private static bool SkipName(ReadOnlySpan<byte> dns, ref int p)
    {
        while (p < dns.Length)
        {
            byte len = dns[p];
            if (len == 0) { p++; return true; }
            if ((len & 0xC0) == 0xC0) { p += 2; return p <= dns.Length; }
            p += 1 + len;
        }
        return false;
    }
}

public enum ParseResult : byte
{
    /// <summary>Found it.</summary>
    Found,

    /// <summary>Looks right so far; more bytes are needed.</summary>
    NeedMore,

    /// <summary>Not a TLS ClientHello (or no server name in it).</summary>
    No,
}

/// <summary>Extracts the server name (SNI) from a TLS ClientHello.</summary>
public static class TlsParser
{
    /// <summary>Largest ClientHello we are willing to buffer.</summary>
    public const int MaxHelloBytes = 16 * 1024;

    /// <summary>Parses the start of a TCP stream: one or more TLS records carrying a ClientHello.</summary>
    public static ParseResult TryGetSniFromRecords(ReadOnlySpan<byte> stream, out string? sni)
    {
        sni = null;
        if (stream.Length == 0)
            return ParseResult.NeedMore;
        if (stream[0] != 0x16)
            return ParseResult.No;

        // Join the bodies of consecutive handshake records (a big hello may span several).
        byte[] hs = new byte[Math.Min(stream.Length, MaxHelloBytes)];
        int total = 0, p = 0;
        while (p + 5 <= stream.Length)
        {
            if (stream[p] != 0x16 || stream[p + 1] != 0x03)
                return ParseResult.No;
            int length = BinaryPrimitives.ReadUInt16BigEndian(stream.Slice(p + 3));
            if (length == 0 || length > MaxHelloBytes)
                return ParseResult.No;
            int available = Math.Min(length, stream.Length - p - 5);
            if (total + available > hs.Length)
                return ParseResult.No;
            stream.Slice(p + 5, available).CopyTo(hs.AsSpan(total));
            total += available;
            p += 5 + length;
        }
        return TryGetSniFromHandshake(hs.AsSpan(0, total), out sni);
    }

    /// <summary>Parses a handshake message (as carried in QUIC CRYPTO frames, or a TLS record body).</summary>
    public static ParseResult TryGetSniFromHandshake(ReadOnlySpan<byte> h, out string? sni)
    {
        sni = null;
        if (h.Length < 4)
            return ParseResult.NeedMore;
        if (h[0] != 0x01)
            return ParseResult.No;
        int length = (h[1] << 16) | (h[2] << 8) | h[3];
        if (length > MaxHelloBytes)
            return ParseResult.No;
        if (h.Length < 4 + length)
            return ParseResult.NeedMore;

        ReadOnlySpan<byte> b = h.Slice(4, length);
        int p = 2 + 32;                                              // legacy_version, random
        if (!Skip8(b, ref p) || !Skip16(b, ref p) || !Skip8(b, ref p)) // session id, ciphers, compression
            return ParseResult.No;
        if (p + 2 > b.Length)
            return ParseResult.No;
        int extEnd = p + 2 + BinaryPrimitives.ReadUInt16BigEndian(b.Slice(p));
        p += 2;
        if (extEnd > b.Length)
            return ParseResult.No;

        while (p + 4 <= extEnd)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(p));
            int len = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(p + 2));
            p += 4;
            if (p + len > extEnd)
                return ParseResult.No;
            if (type == 0 && len >= 5)
            {
                ReadOnlySpan<byte> e = b.Slice(p, len);
                // server_name_list length (2), name_type (1) = host_name, name length (2), name
                int nameLength = BinaryPrimitives.ReadUInt16BigEndian(e.Slice(3));
                if (e[2] != 0 || 5 + nameLength > e.Length || nameLength == 0)
                    return ParseResult.No;
                sni = Ascii(e.Slice(5, nameLength));
                return sni is null ? ParseResult.No : ParseResult.Found;
            }
            p += len;
        }
        return ParseResult.No;
    }

    private static bool Skip8(ReadOnlySpan<byte> b, ref int p)
    {
        if (p + 1 > b.Length) return false;
        p += 1 + b[p];
        return p <= b.Length;
    }

    private static bool Skip16(ReadOnlySpan<byte> b, ref int p)
    {
        if (p + 2 > b.Length) return false;
        p += 2 + BinaryPrimitives.ReadUInt16BigEndian(b.Slice(p));
        return p <= b.Length;
    }

    private static string? Ascii(ReadOnlySpan<byte> name)
    {
        foreach (byte c in name)
            if (c is < 0x21 or > 0x7E) return null;
        return Encoding.ASCII.GetString(name).ToLowerInvariant().TrimEnd('.');
    }
}
