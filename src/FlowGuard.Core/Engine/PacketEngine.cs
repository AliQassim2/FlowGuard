using System.Runtime.InteropServices;
using FlowGuard.Core.Mapping;
using FlowGuard.Core.Packets;
using WinDivertSharp;

namespace FlowGuard.Core.Engine;

/// <summary>What the engine should do with a captured packet.</summary>
public enum PacketVerdict : byte
{
    /// <summary>Re-inject immediately.</summary>
    Pass = 0,

    /// <summary>Discard (no re-injection).</summary>
    Drop = 1,

    /// <summary>The handler copied the packet and will re-inject it later via <see cref="IPacketInjector"/>.</summary>
    Queued = 2,
}

/// <summary>Per-packet facts handed to the <see cref="IPacketHandler"/>. Stack-only, no allocations.</summary>
public readonly ref struct PacketContext
{
    public PacketContext(int pid, PacketDirection direction, scoped in PacketHeader header, ReadOnlySpan<byte> packet,
        scoped in PacketMeta meta, bool canModify)
    {
        Pid = pid;
        Direction = direction;
        Header = header;
        Packet = packet;
        Meta = meta;
        CanModify = canModify;
    }

    /// <summary>Owning process ID, or 0 when the packet could not be attributed.</summary>
    public int Pid { get; }
    public PacketDirection Direction { get; }
    public PacketHeader Header { get; }

    /// <summary>The raw IP packet. Only valid for the duration of the callback.</summary>
    public ReadOnlySpan<byte> Packet { get; }

    /// <summary>Metadata required to re-inject a copy of this packet later.</summary>
    public PacketMeta Meta { get; }

    /// <summary>
    /// False in sniff mode: the packet has already been delivered, so Drop / Queued verdicts are
    /// meaningless and handlers should only observe.
    /// </summary>
    public bool CanModify { get; }

    public int Length => Packet.Length;
}

/// <summary>
/// Hot-path callback invoked on the capture thread for every packet. Implementations must be
/// fast, thread-safe if <see cref="PacketEngineOptions.WorkerThreads"/> &gt; 1, and must not
/// allocate.
/// </summary>
public interface IPacketHandler
{
    PacketVerdict OnPacket(in PacketContext context);
}

public sealed class PacketEngineOptions
{
    /// <summary>
    /// WinDivert filter. Note: <c>inbound and outbound</c> can never match (a packet has one
    /// direction), so we capture both directions by simply not constraining direction.
    /// </summary>
    public string Filter { get; init; } = "(ip or ipv6) and !loopback";

    /// <summary>
    /// Initial mode. Sniff copies packets instead of diverting them — zero impact on traffic,
    /// but cannot throttle. <see cref="FlowGuardService"/> switches modes automatically.
    /// </summary>
    public bool SniffOnly { get; init; } = true;

    /// <summary>Concurrent WinDivertRecv loops (each with its own buffer).</summary>
    public int WorkerThreads { get; init; } = 1;

    /// <summary>WinDivert filter priority (lower = earlier). 0 is the WinDivert default.</summary>
    public short Priority { get; init; } = 0;

    /// <summary>Driver-side queue length in packets (WinDivert 1.4 max: 8192).</summary>
    public ulong QueueLength { get; init; } = 8192;

    /// <summary>Driver-side max queue time in ms before packets are dropped (1.4 max: 2048).</summary>
    public ulong QueueTimeMs { get; init; } = 2048;
}

/// <summary>
/// Owns the WinDivert handle and the capture → classify → verdict → re-inject loop, and serves
/// as the <see cref="IPacketInjector"/> for packets released later by the shaper.
/// All WinDivert calls are isolated in this class so the binding (WinDivertSharp / WinDivert 1.4)
/// can later be swapped for a direct WinDivert 2.x P/Invoke without touching the rest of Core.
/// </summary>
public sealed class PacketEngine : IPacketInjector, IDisposable
{
    private const int MaxPacketSize = 0xFFFF + 40;
    private static readonly IntPtr InvalidHandle = new(-1);

    private const int ERROR_INVALID_HANDLE = 6;
    private const int ERROR_NO_DATA = 232;
    private const int ERROR_OPERATION_ABORTED = 995;

    private readonly PacketEngineOptions _options;
    private readonly PortPidMapper _mapper;
    private readonly IPacketHandler? _handler;
    private readonly object _lifecycleLock = new();

    private IntPtr _handle = InvalidHandle;
    private Thread[] _workers = [];
    private volatile bool _running;

    // Injection path (shaper scheduler thread): one pinned buffer, serialised by _injectLock,
    // which also prevents the handle from being closed mid-send.
    private readonly object _injectLock = new();
    private byte[]? _injectBytes;
    private WinDivertBuffer? _injectBuffer;

    // Diagnostics (Interlocked so they are safe for multi-worker mode).
    private long _packets;
    private long _bytes;
    private long _unattributed;
    private long _sendFailures;

    public PacketEngine(PortPidMapper mapper, IPacketHandler? handler = null, PacketEngineOptions? options = null)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _handler = handler;
        _options = options ?? new PacketEngineOptions();
        SniffOnly = _options.SniffOnly;
    }

    public bool IsRunning => _running;

    /// <summary>Mode of the current (or next) session.</summary>
    public bool SniffOnly { get; private set; }

    public long PacketCount => Interlocked.Read(ref _packets);
    public long ByteCount => Interlocked.Read(ref _bytes);
    public long UnattributedCount => Interlocked.Read(ref _unattributed);
    public long SendFailureCount => Interlocked.Read(ref _sendFailures);

    /// <summary>Raised (off the hot path) when a worker stops because of an unexpected error.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Opens the WinDivert handle and starts the capture threads. Requires elevation.</summary>
    public void Start() => Start(SniffOnly);

    public void Start(bool sniffOnly)
    {
        lock (_lifecycleLock)
        {
            if (_running)
                return;

            string filter = _options.Filter;
            uint errorPos = 0;
            if (!WinDivert.WinDivertHelperCheckFilter(filter, WinDivertLayer.Network, out string? filterError, ref errorPos))
                throw new ArgumentException($"Invalid WinDivert filter at position {errorPos}: {filterError}", nameof(PacketEngineOptions.Filter));

            var flags = sniffOnly ? WinDivertOpenFlags.Sniff : WinDivertOpenFlags.None;
            IntPtr handle = WinDivert.WinDivertOpen(filter, WinDivertLayer.Network, _options.Priority, flags);
            if (handle == InvalidHandle || handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(DescribeOpenError(error), new System.ComponentModel.Win32Exception(error));
            }

            // Best effort: larger driver queues absorb bursts while the shaper is busy.
            WinDivert.WinDivertSetParam(handle, WinDivertParam.QueueLen, _options.QueueLength);
            WinDivert.WinDivertSetParam(handle, WinDivertParam.QueueTime, _options.QueueTimeMs);

            lock (_injectLock)
            {
                _handle = handle;
                _injectBytes ??= new byte[MaxPacketSize];
                _injectBuffer ??= new WinDivertBuffer(_injectBytes);
            }

            SniffOnly = sniffOnly;
            _running = true;

            int count = Math.Max(1, _options.WorkerThreads);
            _workers = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                _workers[i] = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = $"FlowGuard.Capture#{i}",
                    Priority = ThreadPriority.AboveNormal,
                };
                _workers[i].Start(handle);
            }
        }
    }

    /// <summary>Closes the handle (which unblocks WinDivertRecv) and joins the workers.</summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!_running && _handle == InvalidHandle)
                return;
            _running = false;

            lock (_injectLock)
            {
                if (_handle != InvalidHandle)
                    WinDivert.WinDivertClose(_handle);
                _handle = InvalidHandle;
            }

            foreach (Thread t in _workers)
                t.Join(TimeSpan.FromSeconds(2));
            _workers = [];
        }
    }

    /// <summary>Switches between sniff and divert mode by reopening the handle.</summary>
    public void Restart(bool sniffOnly)
    {
        lock (_lifecycleLock)
        {
            Stop();
            Start(sniffOnly);
        }
    }

    /// <inheritdoc />
    public bool Inject(ReadOnlySpan<byte> packet, in PacketMeta meta)
    {
        if (packet.Length > MaxPacketSize)
            return false;

        lock (_injectLock)
        {
            if (_handle == InvalidHandle || SniffOnly || _injectBuffer is null)
                return false;

            packet.CopyTo(_injectBytes);
            WinDivertAddress address = ToAddress(meta);
            bool ok = WinDivert.WinDivertSend(_handle, _injectBuffer, (uint)packet.Length, ref address);
            if (!ok)
                Interlocked.Increment(ref _sendFailures);
            return ok;
        }
    }

    private void CaptureLoop(object? state)
    {
        IntPtr handle = (IntPtr)state!;

        // One pinned buffer per worker, reused for every packet: the hot loop never allocates.
        byte[] bytes = new byte[MaxPacketSize];
        using var buffer = new WinDivertBuffer(bytes);
        var address = new WinDivertAddress();
        bool sniff = SniffOnly;

        try
        {
            while (_running)
            {
                uint readLength = 0;
                address.Reset();

                if (!WinDivert.WinDivertRecv(handle, buffer, ref address, ref readLength))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (!_running || error is ERROR_INVALID_HANDLE or ERROR_NO_DATA or ERROR_OPERATION_ABORTED)
                        break; // handle closed by Stop()
                    continue;   // transient (e.g. ERROR_INSUFFICIENT_BUFFER for jumbo frames)
                }

                ReadOnlySpan<byte> packet = bytes.AsSpan(0, (int)readLength);
                PacketVerdict verdict = Process(packet, ref address, sniff);

                if (!sniff && verdict == PacketVerdict.Pass)
                {
                    // Packet is unmodified, so the original checksums remain valid.
                    if (!WinDivert.WinDivertSend(handle, buffer, readLength, ref address))
                        Interlocked.Increment(ref _sendFailures);
                }
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Faulted?.Invoke(ex);
        }
    }

    private PacketVerdict Process(ReadOnlySpan<byte> packet, ref WinDivertAddress address, bool sniff)
    {
        Interlocked.Increment(ref _packets);
        Interlocked.Add(ref _bytes, packet.Length);

        if (!PacketParser.TryParse(packet, out PacketHeader header))
            return PacketVerdict.Pass;

        var direction = address.Direction == WinDivertDirection.Inbound
            ? PacketDirection.Inbound
            : PacketDirection.Outbound;

        int pid = 0;
        if (!header.HasPorts || !_mapper.TryGetPid(header.Protocol, header.LocalPort(direction), out pid))
            Interlocked.Increment(ref _unattributed);

        if (_handler is null)
            return PacketVerdict.Pass;

        PacketMeta meta = FromAddress(in address, direction);
        return _handler.OnPacket(new PacketContext(pid, direction, header, packet, meta, canModify: !sniff));
    }

    private static PacketMeta FromAddress(in WinDivertAddress a, PacketDirection direction)
    {
        var flags = PacketMeta.MetaFlags.None;
        if (a.Loopback) flags |= PacketMeta.MetaFlags.Loopback;
        if (a.Impostor) flags |= PacketMeta.MetaFlags.Impostor;
        if (a.PseudoIPChecksum) flags |= PacketMeta.MetaFlags.PseudoIpChecksum;
        if (a.PseudoTCPChecksum) flags |= PacketMeta.MetaFlags.PseudoTcpChecksum;
        if (a.PseudoUDPChecksum) flags |= PacketMeta.MetaFlags.PseudoUdpChecksum;
        return new PacketMeta(a.IfIdx, a.SubIfIdx, direction, flags);
    }

    private static WinDivertAddress ToAddress(in PacketMeta m)
    {
        var a = new WinDivertAddress();
        a.Reset();
        a.IfIdx = m.IfIdx;
        a.SubIfIdx = m.SubIfIdx;
        a.Direction = m.Direction == PacketDirection.Inbound ? WinDivertDirection.Inbound : WinDivertDirection.Outbound;
        a.Loopback = m.Flags.HasFlag(PacketMeta.MetaFlags.Loopback);
        a.Impostor = m.Flags.HasFlag(PacketMeta.MetaFlags.Impostor);
        a.PseudoIPChecksum = m.Flags.HasFlag(PacketMeta.MetaFlags.PseudoIpChecksum);
        a.PseudoTCPChecksum = m.Flags.HasFlag(PacketMeta.MetaFlags.PseudoTcpChecksum);
        a.PseudoUDPChecksum = m.Flags.HasFlag(PacketMeta.MetaFlags.PseudoUdpChecksum);
        return a;
    }

    private static string DescribeOpenError(int error) => error switch
    {
        2 => "WinDivert64.sys was not found next to WinDivert.dll (ERROR_FILE_NOT_FOUND).",
        5 => "Access denied opening WinDivert — FlowGuard must run as Administrator.",
        87 => "WinDivert rejected the filter or parameters (ERROR_INVALID_PARAMETER).",
        577 => "Windows refused to load the WinDivert driver signature (ERROR_INVALID_IMAGE_HASH). " +
               "Check Secure Boot / Memory Integrity, or upgrade to WinDivert 2.x.",
        1275 => "The WinDivert driver is blocked from loading (ERROR_DRIVER_BLOCKED).",
        1753 => "The Base Filtering Engine service is not running (EPT_S_NOT_REGISTERED).",
        _ => $"WinDivertOpen failed with Win32 error {error}.",
    };

    public void Dispose()
    {
        Stop();
        lock (_injectLock)
        {
            _injectBuffer?.Dispose();
            _injectBuffer = null;
        }
    }
}
