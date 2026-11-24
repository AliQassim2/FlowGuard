using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FlowGuard.Core.Sites;

/// <summary>
/// Client Initial packet keys (RFC 9001 §5.2 / RFC 9369 for QUIC v2). Initial packets are
/// encrypted with keys anyone can derive from the client's first Destination Connection ID, so
/// reading the ClientHello's server name needs no secrets — the same thing every middlebox does.
/// </summary>
public sealed class QuicInitialKeys : IDisposable
{
    public const uint Version1 = 0x0000_0001;
    public const uint Version2 = 0x6b33_43cf;

    private static readonly byte[] SaltV1 = Convert.FromHexString("38762cf7f55934b34d179ae6a4c80cadccbb7f0a");
    private static readonly byte[] SaltV2 = Convert.FromHexString("0dede3def700a6db819381be6e269dcbf9bd2ed9");

    private readonly Aes _hp;
    private readonly AesGcm _aead;

    private QuicInitialKeys(uint version, byte[] key, byte[] iv, byte[] hp)
    {
        Version = version;
        Key = key;
        Iv = iv;
        Hp = hp;
        _hp = Aes.Create();
        _hp.Key = hp;
        _aead = new AesGcm(key, 16);
    }

    public uint Version { get; }
    public byte[] Key { get; }
    public byte[] Iv { get; }
    public byte[] Hp { get; }

    public static bool IsSupportedVersion(uint version) => version is Version1 or Version2;

    /// <summary>Derives the client Initial keys for <paramref name="dcid"/>.</summary>
    public static QuicInitialKeys Derive(uint version, ReadOnlySpan<byte> dcid)
    {
        bool v2 = version == Version2;
        byte[] initial = HKDF.Extract(HashAlgorithmName.SHA256, dcid.ToArray(), v2 ? SaltV2 : SaltV1);
        byte[] client = ExpandLabel(initial, "client in", 32);
        string prefix = v2 ? "quicv2 " : "quic ";
        return new QuicInitialKeys(version,
            ExpandLabel(client, prefix + "key", 16),
            ExpandLabel(client, prefix + "iv", 12),
            ExpandLabel(client, prefix + "hp", 16));
    }

    /// <summary>TLS 1.3 HKDF-Expand-Label with an empty context.</summary>
    internal static byte[] ExpandLabel(byte[] secret, string label, int length)
    {
        byte[] full = Encoding.ASCII.GetBytes("tls13 " + label);
        byte[] info = new byte[2 + 1 + full.Length + 1];
        BinaryPrimitives.WriteUInt16BigEndian(info, (ushort)length);
        info[2] = (byte)full.Length;
        full.CopyTo(info, 3);
        return HKDF.Expand(HashAlgorithmName.SHA256, secret, length, info);
    }

    internal void HeaderMask(ReadOnlySpan<byte> sample, Span<byte> mask)
        => _hp.EncryptEcb(sample, mask, PaddingMode.None);

    internal bool Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
        Span<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        try
        {
            _aead.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext,
        Span<byte> tag, ReadOnlySpan<byte> aad)
        => _aead.Encrypt(nonce, plaintext, ciphertext, tag, aad);

    public void Dispose()
    {
        _hp.Dispose();
        _aead.Dispose();
    }
}

/// <summary>Collects CRYPTO frame data (which may arrive split, out of order and over several packets).</summary>
public sealed class CryptoStreamBuffer
{
    private readonly byte[] _data = new byte[TlsParser.MaxHelloBytes];
    private readonly bool[] _have = new bool[TlsParser.MaxHelloBytes];
    private int _contiguous;

    /// <summary>Length of the gap-free prefix received so far.</summary>
    public int Contiguous => _contiguous;

    public ReadOnlySpan<byte> Prefix => _data.AsSpan(0, _contiguous);

    public void Add(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset >= _data.Length)
            return;
        int start = (int)offset;
        int count = Math.Min(data.Length, _data.Length - start);
        data.Slice(0, count).CopyTo(_data.AsSpan(start));
        _have.AsSpan(start, count).Fill(true);
        while (_contiguous < _have.Length && _have[_contiguous])
            _contiguous++;
    }
}

/// <summary>Removes header protection from and decrypts client Initial packets.</summary>
public static class QuicInitial
{
    /// <summary>
    /// True for a QUIC long-header Initial packet of a version we understand. Returns its version
    /// and Destination Connection ID.
    /// </summary>
    public static bool IsInitial(ReadOnlySpan<byte> d, out uint version, out ReadOnlySpan<byte> dcid)
    {
        version = 0;
        dcid = default;
        if (d.Length < 7 || (d[0] & 0xC0) != 0xC0)
            return false;
        version = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(1));
        if (!QuicInitialKeys.IsSupportedVersion(version))
            return false;
        int type = (d[0] >> 4) & 0x03;
        if (type != (version == QuicInitialKeys.Version2 ? 1 : 0))
            return false;
        int dcidLength = d[5];
        if (dcidLength > 20 || d.Length < 6 + dcidLength + 1)
            return false;
        dcid = d.Slice(6, dcidLength);
        return true;
    }

    /// <summary>
    /// Decrypts the first (Initial) packet of a UDP datagram and feeds its CRYPTO frames into
    /// <paramref name="crypto"/>.
    /// </summary>
    /// <returns>False when the datagram isn't a decryptable client Initial packet.</returns>
    public static bool TryReadCrypto(ReadOnlySpan<byte> d, QuicInitialKeys keys, CryptoStreamBuffer crypto)
    {
        if (!IsInitial(d, out uint version, out ReadOnlySpan<byte> dcid) || version != keys.Version)
            return false;

        int p = 6 + dcid.Length;
        int scidLength = d[p];
        p += 1 + scidLength;
        if (p >= d.Length || !TryReadVarint(d, ref p, out long tokenLength) || tokenLength > d.Length)
            return false;
        p += (int)tokenLength;
        if (!TryReadVarint(d, ref p, out long length) || length < 20 || p + length > d.Length)
            return false;

        int pnOffset = p;
        if (pnOffset + 4 + 16 > d.Length)
            return false;

        Span<byte> mask = stackalloc byte[16];
        keys.HeaderMask(d.Slice(pnOffset + 4, 16), mask);
        byte first = (byte)(d[0] ^ (mask[0] & 0x0F));
        int pnLength = (first & 0x03) + 1;

        int headerLength = pnOffset + pnLength;
        int payloadEnd = pnOffset + (int)length;
        int cipherLength = payloadEnd - headerLength - 16;
        if (cipherLength <= 0 || cipherLength > 4096)
            return false;

        Span<byte> header = stackalloc byte[headerLength];
        d.Slice(0, headerLength).CopyTo(header);
        header[0] = first;
        long packetNumber = 0;
        for (int i = 0; i < pnLength; i++)
        {
            header[pnOffset + i] ^= mask[1 + i];
            packetNumber = (packetNumber << 8) | header[pnOffset + i];
        }

        Span<byte> nonce = stackalloc byte[12];
        keys.Iv.CopyTo(nonce);
        for (int i = 0; i < 8; i++)
            nonce[11 - i] ^= (byte)(packetNumber >> (8 * i));

        Span<byte> plain = stackalloc byte[cipherLength];
        if (!keys.Decrypt(nonce, d.Slice(headerLength, cipherLength), d.Slice(headerLength + cipherLength, 16), plain, header))
            return false;

        ReadFrames(plain, crypto);
        return true;
    }

    private static void ReadFrames(ReadOnlySpan<byte> f, CryptoStreamBuffer crypto)
    {
        int p = 0;
        while (p < f.Length)
        {
            byte type = f[p++];
            switch (type)
            {
                case 0x00: // PADDING
                case 0x01: // PING
                    continue;
                case 0x02: // ACK
                case 0x03: // ACK + ECN
                {
                    if (!TryReadVarint(f, ref p, out _) || !TryReadVarint(f, ref p, out _) ||
                        !TryReadVarint(f, ref p, out long ranges) || !TryReadVarint(f, ref p, out _))
                        return;
                    for (long i = 0; i < ranges; i++)
                        if (!TryReadVarint(f, ref p, out _) || !TryReadVarint(f, ref p, out _))
                            return;
                    if (type == 0x03)
                        for (int i = 0; i < 3; i++)
                            if (!TryReadVarint(f, ref p, out _))
                                return;
                    continue;
                }
                case 0x06: // CRYPTO
                {
                    if (!TryReadVarint(f, ref p, out long offset) || !TryReadVarint(f, ref p, out long length) ||
                        length < 0 || p + length > f.Length)
                        return;
                    crypto.Add(offset, f.Slice(p, (int)length));
                    p += (int)length;
                    continue;
                }
                default:
                    return; // CONNECTION_CLOSE or something unexpected: nothing more for us
            }
        }
    }

    public static bool TryReadVarint(ReadOnlySpan<byte> d, ref int p, out long value)
    {
        value = 0;
        if (p >= d.Length)
            return false;
        int length = 1 << (d[p] >> 6);
        if (p + length > d.Length)
            return false;
        value = d[p] & 0x3F;
        for (int i = 1; i < length; i++)
            value = (value << 8) | d[p + i];
        p += length;
        return true;
    }

    /// <summary>
    /// Builds a protected client Initial packet (tests only): the exact inverse of
    /// <see cref="TryReadCrypto"/>, used to check splitting and reordering.
    /// </summary>
    internal static byte[] Protect(QuicInitialKeys keys, ReadOnlySpan<byte> dcid, ReadOnlySpan<byte> frames, uint packetNumber)
    {
        uint version = keys.Version;
        int typeBits = version == QuicInitialKeys.Version2 ? 1 : 0;
        const int pnLength = 4;
        int payloadLength = frames.Length + 16;
        var header = new List<byte> { (byte)(0xC0 | (typeBits << 4) | (pnLength - 1)) };
        header.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(version)));
        header.Add((byte)dcid.Length);
        header.AddRange(dcid.ToArray());
        header.Add(0);                         // SCID length
        header.Add(0);                         // token length
        int lengthField = pnLength + payloadLength;
        header.Add((byte)(0x40 | (lengthField >> 8)));
        header.Add((byte)lengthField);
        int pnOffset = header.Count;
        for (int i = pnLength - 1; i >= 0; i--)
            header.Add((byte)(packetNumber >> (8 * i)));

        byte[] aad = header.ToArray();
        Span<byte> nonce = stackalloc byte[12];
        keys.Iv.CopyTo(nonce);
        for (int i = 0; i < 8; i++)
            nonce[11 - i] ^= (byte)((long)packetNumber >> (8 * i));

        byte[] packet = new byte[aad.Length + payloadLength];
        aad.CopyTo(packet, 0);
        keys.Encrypt(nonce, frames, packet.AsSpan(aad.Length, frames.Length), packet.AsSpan(aad.Length + frames.Length, 16), aad);

        Span<byte> mask = stackalloc byte[16];
        keys.HeaderMask(packet.AsSpan(pnOffset + 4, 16), mask);
        packet[0] ^= (byte)(mask[0] & 0x0F);
        for (int i = 0; i < pnLength; i++)
            packet[pnOffset + i] ^= mask[1 + i];
        return packet;
    }
}
