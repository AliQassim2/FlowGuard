using System.Runtime.InteropServices;

namespace FlowGuard.Core.Interop;

/// <summary>
/// Source-generated P/Invoke bindings for iphlpapi.dll owner-PID socket tables.
/// All row structs are blittable so the tables can be walked directly over native memory.
/// </summary>
internal static unsafe partial class IpHelperApi
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    public const uint NO_ERROR = 0;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public enum TcpTableClass
    {
        BasicListener = 0,
        BasicConnections = 1,
        BasicAll = 2,
        OwnerPidListener = 3,
        OwnerPidConnections = 4,
        OwnerPidAll = 5,
        OwnerModuleListener = 6,
        OwnerModuleConnections = 7,
        OwnerModuleAll = 8,
    }

    public enum UdpTableClass
    {
        Basic = 0,
        OwnerPid = 1,
        OwnerModule = 2,
    }

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(
        void* pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        TcpTableClass tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedUdpTable(
        void* pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        UdpTableClass tableClass,
        uint reserved);

    // ---- Row layouts (see iprtrmib.h / tcpmib.h / udpmib.h) ----
    // Every table is: DWORD dwNumEntries; ROW table[ANY_SIZE];  (rows are 4-byte aligned)

    [StructLayout(LayoutKind.Sequential)]
    public struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;   // network byte order in the low 16 bits
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MibTcp6RowOwnerPid
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MibUdp6RowOwnerPid
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    /// <summary>TCP state value for sockets in TIME_WAIT (no meaningful owner).</summary>
    public const uint MIB_TCP_STATE_TIME_WAIT = 11;

    /// <summary>Converts the DWORD port field (network order in low word) to a host-order port.</summary>
    public static ushort PortFromDword(uint dwPort)
        => (ushort)(((dwPort & 0xFF) << 8) | ((dwPort >> 8) & 0xFF));
}
