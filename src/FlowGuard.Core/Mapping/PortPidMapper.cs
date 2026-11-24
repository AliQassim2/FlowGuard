using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowGuard.Core.Interop;
using FlowGuard.Core.Packets;

namespace FlowGuard.Core.Mapping;

/// <summary>
/// Thread-safe, lock-free (for readers) map of local TCP/UDP port → owning process ID.
/// <para>
/// A background thread snapshots the IPv4 + IPv6 TCP and UDP owner-PID tables from iphlpapi.dll
/// into flat <c>int[65536]</c> arrays and publishes them with a single reference swap. The packet
/// thread therefore pays one volatile read and one array index per lookup — no locks, no
/// allocations, no dictionary hashing.
/// </para>
/// <para>
/// UDP tables are included for both address families, so QUIC (UDP/443) traffic from browsers is
/// attributed exactly like TCP.
/// </para>
/// </summary>
public sealed unsafe class PortPidMapper : IDisposable
{
    private const int PortCount = 65536;

    /// <summary>A published, read-only (to consumers) port→PID view.</summary>
    private sealed class Snapshot
    {
        public readonly int[] Tcp = new int[PortCount];
        public readonly int[] Udp = new int[PortCount];
    }

    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _minRefreshSpacing;
    private readonly int _graceRefreshes;

    // Published snapshot (read by any thread) and the back buffer (refresher-only).
    private Snapshot _current = new();
    private Snapshot _spare = new();

    // Refresher-private scratch state.
    private readonly int[] _freshTcp = new int[PortCount];
    private readonly int[] _freshUdp = new int[PortCount];
    private readonly byte[] _ageTcp = new byte[PortCount];
    private readonly byte[] _ageUdp = new byte[PortCount];
    private void* _buffer;
    private int _bufferSize;

    private readonly object _refreshLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;
    private int _refreshRequested;
    private long _lastRefreshTimestamp;

    /// <param name="refreshInterval">Periodic refresh cadence (default 1 s).</param>
    /// <param name="minRefreshSpacing">
    /// Lower bound between refreshes triggered by lookup misses (default 50 ms), so a burst of
    /// packets for an unknown socket cannot hammer iphlpapi.
    /// </param>
    /// <param name="graceRefreshes">
    /// How many refreshes a port keeps its last owner after the socket disappears from the OS
    /// tables (default 3). Lets trailing FIN/ACK and late QUIC packets still be attributed.
    /// </param>
    public PortPidMapper(
        TimeSpan? refreshInterval = null,
        TimeSpan? minRefreshSpacing = null,
        int graceRefreshes = 3)
    {
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(1);
        _minRefreshSpacing = minRefreshSpacing ?? TimeSpan.FromMilliseconds(50);
        _graceRefreshes = Math.Clamp(graceRefreshes, 0, byte.MaxValue);

        Refresh(); // populate synchronously so the first lookups already hit

        _thread = new Thread(RefreshLoop)
        {
            IsBackground = true,
            Name = "FlowGuard.PortPidMapper",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Number of completed table refreshes.</summary>
    public long RefreshCount { get; private set; }

    /// <summary>Wall time spent by the most recent refresh.</summary>
    public TimeSpan LastRefreshDuration { get; private set; }

    /// <summary>
    /// Resolves the process that owns <paramref name="localPort"/>. Safe to call from any thread;
    /// allocation-free. On a miss, an expedited background refresh is requested so brand-new
    /// sockets resolve within ~<c>minRefreshSpacing</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPid(TransportProtocol protocol, ushort localPort, out int pid)
    {
        Snapshot snap = Volatile.Read(ref _current);
        pid = protocol switch
        {
            TransportProtocol.Tcp => snap.Tcp[localPort],
            TransportProtocol.Udp => snap.Udp[localPort],
            _ => 0,
        };

        if (pid != 0)
            return true;

        if (protocol is TransportProtocol.Tcp or TransportProtocol.Udp)
            RequestRefresh();
        return false;
    }

    /// <summary>Asks the background thread to refresh soon (coalesced, non-blocking).</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshRequested, 1) == 0)
            _wake.Set();
    }

    /// <summary>Synchronously rebuilds and publishes the port tables.</summary>
    public void Refresh()
    {
        lock (_refreshLock)
        {
            if (_disposed)
                return;

            long start = Stopwatch.GetTimestamp();

            Array.Clear(_freshTcp);
            Array.Clear(_freshUdp);

            LoadTcp(IpHelperApi.AF_INET);
            LoadTcp(IpHelperApi.AF_INET6);
            LoadUdp(IpHelperApi.AF_INET);
            LoadUdp(IpHelperApi.AF_INET6);

            Snapshot current = _current;
            Snapshot next = _spare;
            Merge(_freshTcp, current.Tcp, next.Tcp, _ageTcp);
            Merge(_freshUdp, current.Udp, next.Udp, _ageUdp);

            // Publish. Readers that grabbed `current` an instant ago keep a consistent view; it is
            // only overwritten on the *next* refresh, which is at least minRefreshSpacing away.
            Volatile.Write(ref _current, next);
            _spare = current;

            long end = Stopwatch.GetTimestamp();
            _lastRefreshTimestamp = end;
            LastRefreshDuration = Stopwatch.GetElapsedTime(start, end);
            RefreshCount++;
        }
    }

    /// <summary>
    /// Returns the published table as (protocol, port, pid) tuples.
    /// Diagnostic helper — allocates; do not call from the packet path.
    /// </summary>
    public IReadOnlyList<(TransportProtocol Protocol, ushort Port, int Pid)> GetEntries()
    {
        Snapshot snap = Volatile.Read(ref _current);
        var list = new List<(TransportProtocol, ushort, int)>();
        for (int port = 0; port < PortCount; port++)
        {
            if (snap.Tcp[port] != 0) list.Add((TransportProtocol.Tcp, (ushort)port, snap.Tcp[port]));
            if (snap.Udp[port] != 0) list.Add((TransportProtocol.Udp, (ushort)port, snap.Udp[port]));
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------

    private void RefreshLoop()
    {
        while (!_disposed)
        {
            _wake.WaitOne(_refreshInterval);
            if (_disposed)
                break;

            // Respect the minimum spacing for miss-triggered refreshes.
            TimeSpan since = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastRefreshTimestamp));
            if (since < _minRefreshSpacing)
                Thread.Sleep(_minRefreshSpacing - since);

            Interlocked.Exchange(ref _refreshRequested, 0);
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                // Never let a transient iphlpapi failure kill the refresher.
                Debug.WriteLine($"[PortPidMapper] refresh failed: {ex}");
            }
        }
    }

    /// <summary>
    /// fresh  — owners seen in this refresh (0 = absent)
    /// prev   — previously published table
    /// next   — table being built for publication
    /// age    — refreshes since a port was last seen in the OS table
    /// </summary>
    private void Merge(int[] fresh, int[] prev, int[] next, byte[] age)
    {
        int grace = _graceRefreshes;
        for (int i = 0; i < PortCount; i++)
        {
            int pid = fresh[i];
            if (pid != 0)
            {
                next[i] = pid;
                age[i] = 0;
            }
            else if (prev[i] != 0 && age[i] < grace)
            {
                next[i] = prev[i];
                age[i]++;
            }
            else
            {
                next[i] = 0;
                age[i] = 0;
            }
        }
    }

    private void LoadTcp(int addressFamily)
    {
        if (!FetchTable(addressFamily, isTcp: true))
            return;

        uint count = *(uint*)_buffer;
        byte* rows = (byte*)_buffer + sizeof(uint);
        int[] map = _freshTcp;

        if (addressFamily == IpHelperApi.AF_INET)
        {
            var row = (IpHelperApi.MibTcpRowOwnerPid*)rows;
            for (uint i = 0; i < count; i++, row++)
                SetOwner(map, IpHelperApi.PortFromDword(row->LocalPort), row->OwningPid, row->State);
        }
        else
        {
            var row = (IpHelperApi.MibTcp6RowOwnerPid*)rows;
            for (uint i = 0; i < count; i++, row++)
                SetOwner(map, IpHelperApi.PortFromDword(row->LocalPort), row->OwningPid, row->State);
        }
    }

    private void LoadUdp(int addressFamily)
    {
        if (!FetchTable(addressFamily, isTcp: false))
            return;

        uint count = *(uint*)_buffer;
        byte* rows = (byte*)_buffer + sizeof(uint);
        int[] map = _freshUdp;

        if (addressFamily == IpHelperApi.AF_INET)
        {
            var row = (IpHelperApi.MibUdpRowOwnerPid*)rows;
            for (uint i = 0; i < count; i++, row++)
                SetOwner(map, IpHelperApi.PortFromDword(row->LocalPort), row->OwningPid, state: 0);
        }
        else
        {
            var row = (IpHelperApi.MibUdp6RowOwnerPid*)rows;
            for (uint i = 0; i < count; i++, row++)
                SetOwner(map, IpHelperApi.PortFromDword(row->LocalPort), row->OwningPid, state: 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetOwner(int[] map, ushort port, uint pid, uint state)
    {
        // TIME_WAIT rows are owned by PID 0; never let them clobber a live owner of the same port.
        if (pid == 0 || state == IpHelperApi.MIB_TCP_STATE_TIME_WAIT)
            return;
        map[port] = (int)pid;
    }

    /// <summary>Fills <see cref="_buffer"/> with the requested table, growing it as needed.</summary>
    private bool FetchTable(int addressFamily, bool isTcp)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int size = _bufferSize;
            uint result = isTcp
                ? IpHelperApi.GetExtendedTcpTable(_buffer, ref size, false, addressFamily,
                    IpHelperApi.TcpTableClass.OwnerPidAll, 0)
                : IpHelperApi.GetExtendedUdpTable(_buffer, ref size, false, addressFamily,
                    IpHelperApi.UdpTableClass.OwnerPid, 0);

            if (result == IpHelperApi.NO_ERROR)
                return _buffer != null;

            if (result != IpHelperApi.ERROR_INSUFFICIENT_BUFFER)
            {
                Debug.WriteLine($"[PortPidMapper] GetExtended{(isTcp ? "Tcp" : "Udp")}Table(af={addressFamily}) failed: {result}");
                return false;
            }

            // Grow with headroom: new sockets can appear between the size probe and the fetch.
            GrowBuffer(size + size / 4 + 4096);
        }
        return false;
    }

    private void GrowBuffer(int newSize)
    {
        if (newSize <= _bufferSize)
            return;
        if (_buffer != null)
            NativeMemory.Free(_buffer);
        _buffer = NativeMemory.Alloc((nuint)newSize);
        _bufferSize = newSize;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));

        lock (_refreshLock)
        {
            if (_buffer != null)
            {
                NativeMemory.Free(_buffer);
                _buffer = null;
                _bufferSize = 0;
            }
        }
        _wake.Dispose();
    }
}
