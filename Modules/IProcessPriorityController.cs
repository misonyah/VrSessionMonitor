using System.Diagnostics;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>Reads and writes another process's CPU priority. An interface so the session-priority
/// policy can be tested without real processes.</summary>
public interface IProcessPriorityController
{
    /// <summary>PIDs of every running process with this name, so an app that runs several (SlimeVR
    /// runs five) has all of them adjusted rather than an arbitrary one.</summary>
    IReadOnlyList<int> GetProcessIds(string processName);

    /// <summary>Current priority, or null if the process is gone or unreadable.</summary>
    AppProcessPriority? GetPriority(int processId);

    /// <summary>Returns false when the change was refused; never throws.</summary>
    bool SetPriority(int processId, AppProcessPriority priority);
}

public sealed class ProcessPriorityController : IProcessPriorityController
{
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
            Log.Debug("Priority", $"Listing '{processName}' failed: {ex.Message}");
            return Array.Empty<int>();
        }
    }

    public AppProcessPriority? GetPriority(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            return FromClass(proc.PriorityClass);
        }
        catch (Exception ex)
        {
            // Gone, or owned by a user we cannot read. Both are ordinary.
            Log.Debug("Priority", $"Reading priority of PID {processId} failed: {ex.Message}");
            return null;
        }
    }

    public bool SetPriority(int processId, AppProcessPriority priority)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            proc.PriorityClass = ToClass(priority);
            return true;
        }
        catch (Exception ex)
        {
            // Access denied is the common one: a process elevated above us, or owned by another
            // user. Normal, and never worth failing a session start over.
            Log.Debug("Priority", $"Setting PID {processId} to {priority} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Realtime maps to High deliberately — see AppProcessPriority. A process already
    /// running at Realtime is reported as High so restoring it never RAISES it there.</summary>
    private static AppProcessPriority? FromClass(ProcessPriorityClass cls) => cls switch
    {
        ProcessPriorityClass.Idle => AppProcessPriority.Idle,
        ProcessPriorityClass.BelowNormal => AppProcessPriority.BelowNormal,
        ProcessPriorityClass.Normal => AppProcessPriority.Normal,
        ProcessPriorityClass.AboveNormal => AppProcessPriority.AboveNormal,
        ProcessPriorityClass.High => AppProcessPriority.High,
        ProcessPriorityClass.RealTime => AppProcessPriority.High,
        _ => null,
    };

    private static ProcessPriorityClass ToClass(AppProcessPriority priority) => priority switch
    {
        AppProcessPriority.Idle => ProcessPriorityClass.Idle,
        AppProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        AppProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
        AppProcessPriority.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };
}
