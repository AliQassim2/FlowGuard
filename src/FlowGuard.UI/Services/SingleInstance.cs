using System.Runtime.InteropServices;

namespace FlowGuard.UI.Services;

/// <summary>
/// Keeps one FlowGuard per session (two instances would fight over WinDivert and the focus hook).
/// A second launch signals the first to show its window, then exits.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FlowGuard.SingleInstance.6F1C2B7E";
    private const string EventName = @"Local\FlowGuard.Activate.6F1C2B7E";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>Returns null (after asking the running instance to show itself) if FlowGuard is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (!created)
        {
            mutex.Dispose();
            try
            {
                AllowSetForegroundWindow(AsfwAny); // let the running instance take the foreground
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(EventName);
                existing.Set();
            }
            catch
            {
                // The other instance is starting up or shutting down; nothing else to do.
            }
            return null;
        }

        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, EventName));
    }

    /// <summary>Invokes <paramref name="onActivate"/> (on a thread-pool thread) whenever another launch is attempted.</summary>
    public void Listen(Action onActivate)
        => _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread */ }
        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int dwProcessId);
}
