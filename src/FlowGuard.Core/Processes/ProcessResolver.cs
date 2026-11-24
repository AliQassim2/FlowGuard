using System.Diagnostics;
using FlowGuard.Core.Interop;

namespace FlowGuard.Core.Processes;

/// <summary>Identity of a process: executable name/path plus creation time (guards against PID reuse).</summary>
public readonly record struct ProcessIdentity(int Pid, string Name, string? Path, long CreationTime);

/// <summary>Resolves PIDs to executable names via QueryFullProcessImageName (cheap, works elevated).</summary>
public static unsafe class ProcessResolver
{
    public const int UnattributedPid = 0;
    public const int SystemPid = 4;

    public static ProcessIdentity Resolve(int pid)
    {
        if (pid == UnattributedPid)
            return new ProcessIdentity(pid, "(unattributed)", null, 0);
        if (pid == SystemPid)
            return new ProcessIdentity(pid, "System", null, 0);

        IntPtr h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                long creation = Kernel32.GetProcessTimes(h, out long c, out _, out _, out _) ? c : 0;
                char* buffer = stackalloc char[1024];
                int size = 1024;
                if (Kernel32.QueryFullProcessImageName(h, 0, buffer, ref size))
                {
                    string path = new(buffer, 0, size);
                    return new ProcessIdentity(pid, System.IO.Path.GetFileName(path), path, creation);
                }
                return new ProcessIdentity(pid, FallbackName(pid), null, creation);
            }
            finally
            {
                Kernel32.CloseHandle(h);
            }
        }

        return new ProcessIdentity(pid, FallbackName(pid), null, 0);
    }

    /// <summary>
    /// Current creation time of <paramref name="pid"/>; null when the process no longer exists.
    /// Returns 0 when the process exists but can't be queried (protected processes).
    /// </summary>
    public static long? GetCreationTime(int pid)
    {
        if (pid is UnattributedPid or SystemPid)
            return 0;

        IntPtr h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            // ERROR_INVALID_PARAMETER (87) ⇒ no such process; anything else (access denied) ⇒ alive.
            return System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 87 ? null : 0;
        }
        try
        {
            if (Kernel32.GetExitCodeProcess(h, out uint code) && code != Kernel32.STILL_ACTIVE)
                return null; // exited, handle kept alive by someone else
            return Kernel32.GetProcessTimes(h, out long c, out _, out _, out _) ? c : 0;
        }
        finally
        {
            Kernel32.CloseHandle(h);
        }
    }

    /// <summary>Normalises a user-supplied process name ("Steam", "steam.exe") to "steam.exe".</summary>
    public static string NormalizeExeName(string name)
    {
        string trimmed = System.IO.Path.GetFileName(name.Trim());
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".exe";
    }

    private static string FallbackName(int pid)
    {
        // Protected processes refuse PROCESS_QUERY_LIMITED_INFORMATION; Process uses
        // NtQuerySystemInformation instead, which still yields the image name.
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return $"pid {pid}";
        }
    }
}
