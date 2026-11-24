using FlowGuard.Core.Packets;

namespace FlowGuard.Core.Engine;

/// <summary>
/// Driver-independent copy of the WinDivert address metadata needed to re-inject a packet later.
/// Keeps WinDivertSharp types out of the shaping code.
/// </summary>
public readonly struct PacketMeta
{
    [Flags]
    internal enum MetaFlags : byte
    {
        None = 0,
        Loopback = 1,
        Impostor = 2,
        PseudoIpChecksum = 4,
        PseudoTcpChecksum = 8,
        PseudoUdpChecksum = 16,
    }

    internal PacketMeta(uint ifIdx, uint subIfIdx, PacketDirection direction, MetaFlags flags)
    {
        IfIdx = ifIdx;
        SubIfIdx = subIfIdx;
        Direction = direction;
        Flags = flags;
    }

    public uint IfIdx { get; }
    public uint SubIfIdx { get; }
    public PacketDirection Direction { get; }
    internal MetaFlags Flags { get; }
}

/// <summary>Re-injects packets that were held back by the shaper.</summary>
public interface IPacketInjector
{
    /// <returns>False if the packet could not be sent (engine stopped or driver error).</returns>
    bool Inject(ReadOnlySpan<byte> packet, in PacketMeta meta);
}
