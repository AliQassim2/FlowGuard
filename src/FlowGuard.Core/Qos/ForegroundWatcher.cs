using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowGuard.Core.Interop;
using FlowGuard.Core.Processes;

namespace FlowGuard.Core.Qos;

/// <summary>
/// Reports the process that owns the foreground window, event-driven via
/// <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c> (no polling). Runs its own message-loop thread
/// so it works without a UI dispatcher. FlowGuard's own windows are skipped
/// (<c>WINEVENT_SKIPOWNPROCESS</c>), so focusing FlowGuard keeps the previous app in focus.
/// </summary>
public sealed unsafe class ForegroundWatcher : IDisposable
{
    private const string FrameHostExe = "ApplicationFrameHost.exe";

    // WinEvent callbacks are static (UnmanagedCallersOnly); only one watcher can be live at a time.
    private static ForegroundWatcher? s_instance;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private Exception? _startError;
    private volatile bool _disposed;

    /// <summary>Raised on the watcher thread with the owning PID and executable name.</summary>
    public event Action<int, string>? ForegroundChanged;

    public int CurrentPid { get; private set; }
    public string? CurrentExe { get; private set; }

    public ForegroundWatcher()
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("Only one ForegroundWatcher may be active at a time.");

        _thread = new Thread(Run) { IsBackground = true, Name = "FlowGuard.ForegroundWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();

        if (_startError is not null)
        {
            Interlocked.CompareExchange(ref s_instance, null, this);
            throw new InvalidOperationException("SetWinEventHook failed.", _startError);
        }
    }

    private void Run()
    {
        _threadId = User32.GetCurrentThreadId();
        IntPtr hook = User32.SetWinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, &OnWinEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        if (hook == IntPtr.Zero)
        {
            _startError = new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            _ready.Set();
            return;
        }

        try
        {
            Handle(User32.GetForegroundWindow()); // initial state
        }
        catch { /* best effort */ }
        _ready.Set();

        User32.MSG msg;
        while (!_disposed && User32.GetMessage(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == User32.WM_TIMER && msg.hwnd == IntPtr.Zero && msg.wParam == _retryTimer)
            {
                OnFrameHostRetry();
                continue;
            }
            User32.TranslateMessage(&msg);
            User32.DispatchMessage(&msg);
        }

        StopRetry();
        User32.UnhookWinEvent(hook);
    }

    // ---- UWP retry: right after EVENT_SYSTEM_FOREGROUND an ApplicationFrameHost frame often has
    // no app CoreWindow attached yet, and no further foreground event follows. Re-check briefly.
    private const uint RetryIntervalMs = 100;
    private const int MaxRetries = 15;
    private nuint _retryTimer;
    private IntPtr _retryFrame;
    private uint _retryHostPid;
    private int _retriesLeft;

    private void StartRetry(IntPtr frame, uint hostPid)
    {
        StopRetry();
        _retryFrame = frame;
        _retryHostPid = hostPid;
        _retriesLeft = MaxRetries;
        _retryTimer = User32.SetTimer(IntPtr.Zero, 0, RetryIntervalMs, IntPtr.Zero);
    }

    private void StopRetry()
    {
        if (_retryTimer != 0)
            User32.KillTimer(IntPtr.Zero, _retryTimer);
        _retryTimer = 0;
        _retryFrame = IntPtr.Zero;
    }

    private void OnFrameHostRetry()
    {
        if (User32.GetForegroundWindow() != _retryFrame)
        {
            StopRetry(); // focus moved on; the WinEvent for the new window handles it
            return;
        }

        uint child = FindHostedChildPid(_retryFrame, _retryHostPid);
        if (child != 0)
        {
            StopRetry();
            Report(ProcessResolver.Resolve((int)child));
        }
        else if (--_retriesLeft <= 0)
        {
            StopRetry();
            Report(ProcessResolver.Resolve((int)_retryHostPid)); // give up: report the host itself
        }
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime)
    {
        try
        {
            s_instance?.Handle(hwnd);
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }
    }

    private void Handle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed)
            return;

        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return;

        StopRetry();
        ProcessIdentity identity = ProcessResolver.Resolve((int)pid);

        // UWP apps are hosted in ApplicationFrameHost; the real app owns a child window.
        if (string.Equals(identity.Name, FrameHostExe, StringComparison.OrdinalIgnoreCase))
        {
            uint child = FindHostedChildPid(hwnd, pid);
            if (child == 0)
            {
                StartRetry(hwnd, pid); // keep the previous foreground until resolved
                return;
            }
            identity = ProcessResolver.Resolve((int)child);
        }

        Report(identity);
    }

    private void Report(ProcessIdentity identity)
    {
        if (identity.Pid == CurrentPid && identity.Name == CurrentExe)
            return;

        CurrentPid = identity.Pid;
        CurrentExe = identity.Name;
        ForegroundChanged?.Invoke(identity.Pid, identity.Name);
    }

    private struct ChildSearch
    {
        public uint HostPid;
        public uint FoundPid;
        public char* Title;
        public int TitleLength;
    }

    /// <summary>
    /// Finds the app hosted by an ApplicationFrameHost frame. Windows only reparents the app's
    /// CoreWindow into the frame while it is active; otherwise (and briefly right after a focus
    /// switch) the CoreWindow is a separate top-level window with the frame's title.
    /// </summary>
    private static uint FindHostedChildPid(IntPtr frame, uint hostPid)
    {
        var search = new ChildSearch { HostPid = hostPid };
        User32.EnumChildWindows(frame, &EnumChild, (nint)Unsafe.AsPointer(ref search));
        if (search.FoundPid != 0)
            return search.FoundPid;

        char* title = stackalloc char[256];
        int length = User32.GetWindowText(frame, title, 256);
        if (length <= 0)
            return 0;
        search.Title = title;
        search.TitleLength = length;
        User32.EnumWindows(&EnumDetachedCoreWindow, (nint)Unsafe.AsPointer(ref search));
        return search.FoundPid;
    }

    [UnmanagedCallersOnly]
    private static int EnumDetachedCoreWindow(IntPtr hwnd, nint lParam)
    {
        var search = (ChildSearch*)lParam;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == search->HostPid)
            return 1;

        char* cls = stackalloc char[64];
        int clsLength = User32.GetClassName(hwnd, cls, 64);
        if (!new ReadOnlySpan<char>(cls, clsLength).SequenceEqual("Windows.UI.Core.CoreWindow"))
            return 1;

        char* text = stackalloc char[256];
        int textLength = User32.GetWindowText(hwnd, text, 256);
        if (!new ReadOnlySpan<char>(text, textLength).SequenceEqual(new ReadOnlySpan<char>(search->Title, search->TitleLength)))
            return 1;

        search->FoundPid = pid;
        return 0; // stop
    }

    [UnmanagedCallersOnly]
    private static int EnumChild(IntPtr hwnd, nint lParam)
    {
        var search = (ChildSearch*)lParam;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0 && pid != search->HostPid)
        {
            search->FoundPid = pid;
            return 0; // stop
        }
        return 1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_threadId != 0)
            User32.PostThreadMessage(_threadId, User32.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        Interlocked.CompareExchange(ref s_instance, null, this);
        _ready.Dispose();
    }
}
