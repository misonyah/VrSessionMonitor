using System.Diagnostics;
using System.Runtime.InteropServices;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Suspend;

/// <summary>Suspends, resumes and trims processes. An interface so the session policy can be
/// tested without freezing anything real.</summary>
public interface IProcessSuspender
{
    IReadOnlyList<int> GetProcessIds(string processName);

    /// <summary>Suspends every thread. Returns false if nothing could be suspended.</summary>
    bool Suspend(int processId);

    bool Resume(int processId);

    /// <summary>Releases the process's working set to the standby list, so the physical RAM
    /// becomes available now rather than whenever Windows next feels memory pressure.</summary>
    bool TrimWorkingSet(int processId);

    bool IsRunning(int processId);
}

public sealed class ProcessSuspender : IProcessSuspender
{
    private const int THREAD_SUSPEND_RESUME = 0x0002;
    private const uint PROCESS_SET_QUOTA = 0x0100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(int dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public IReadOnlyList<int> GetProcessIds(string processName)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            try { return procs.Select(p => p.Id).ToList(); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch (Exception ex)
        {
            Log.Debug("Suspend", $"Listing '{processName}' failed: {ex.Message}");
            return Array.Empty<int>();
        }
    }

    public bool IsRunning(int processId)
    {
        try { using var p = Process.GetProcessById(processId); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// Suspends by walking the process's threads rather than calling the undocumented
    /// NtSuspendProcess, so this stays on supported API. Threads created after this point are not
    /// suspended, which is acceptable: a frozen process has nothing running to create one.
    /// </summary>
    public bool Suspend(int processId) => ForEachThread(processId, handle => SuspendThread(handle) != unchecked((uint)-1));

    /// <summary>
    /// Resumes every thread, calling ResumeThread repeatedly until the suspend count reaches zero.
    /// The count is per-thread and nests, so a single call can leave a thread suspended if anything
    /// else suspended it too — and a process left partly suspended looks hung with no clue why.
    /// </summary>
    public bool Resume(int processId) => ForEachThread(processId, handle =>
    {
        var guard = 0;
        int previous;
        do { previous = ResumeThread(handle); } while (previous > 1 && ++guard < 64);
        return previous >= 0;
    });

    public bool TrimWorkingSet(int processId)
    {
        var handle = OpenProcess(PROCESS_SET_QUOTA, false, processId);
        if (handle == IntPtr.Zero) return false;

        try { return EmptyWorkingSet(handle); }
        finally { CloseHandle(handle); }
    }

    private static bool ForEachThread(int processId, Func<IntPtr, bool> action)
    {
        var any = false;

        try
        {
            using var proc = Process.GetProcessById(processId);
            foreach (ProcessThread thread in proc.Threads)
            {
                var handle = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                if (handle == IntPtr.Zero) continue; // exited, or not ours to touch

                try { if (action(handle)) any = true; }
                finally { CloseHandle(handle); }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Suspend", $"Walking threads of PID {processId} failed: {ex.Message}");
        }

        return any;
    }
}
