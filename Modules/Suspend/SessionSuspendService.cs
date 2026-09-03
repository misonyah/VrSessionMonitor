using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Suspend;

/// <summary>
/// Freezes configured applications for the length of a VR session and thaws them afterwards.
///
/// The point is memory, not CPU. A suspended process stops touching its pages, so Windows evicts
/// them and hands the physical RAM to the session — and it never faults them back, because it is
/// not running. TrimWorkingSet makes that immediate rather than gradual. Measured on this machine
/// on 2026-09-02: a development environment (67 VS Code processes, Unity, 256 node) had pushed RAM
/// to 95% used with 29.5 GB in the pagefile, and VRChat stuttered from paging. Closing those apps
/// recovered 7.6 GB; freezing them recovers the same memory while leaving every editor and its
/// unsaved state exactly where it was.
///
/// Everything about the resume path is defensive, because a process left frozen looks hung with no
/// visible cause and no way for the user to guess why:
///
/// - only processes we actually suspended are ever resumed, keyed on PID
/// - resume runs on app shutdown too, so quitting mid-session cannot strand one
/// - SuspendSafety refuses the VR chain, system processes and this app itself
///
/// This helps only when RAM is the constraint. It does nothing for a session limited by avatar
/// load on VRChat's main thread, which is a different bottleneck with a different fix.
/// </summary>
public sealed class SessionSuspendService
{
    private readonly MonitorConfig _config;
    private readonly IProcessSuspender _suspender;
    private readonly int _currentProcessId;

    /// <summary>PIDs we suspended, with the app they belong to, so resume touches nothing else.</summary>
    private readonly Dictionary<int, string> _suspended = new();
    private readonly object _lock = new();

    public SessionSuspendService(MonitorConfig config, IProcessSuspender suspender, int? currentProcessId = null)
    {
        _config = config;
        _suspender = suspender;
        _currentProcessId = currentProcessId ?? SuspendSafety.CurrentProcessId;
    }

    /// <summary>Plain method rather than an event subscription, matching OptimizationsManager and
    /// SessionPriorityService, so it is directly callable from tests.</summary>
    public void HandleSteamVrRunningChanged(bool isRunning)
    {
        if (isRunning) SuspendConfiguredApps();
        else ResumeAll();
    }

    public void SuspendConfiguredApps()
    {
        var apps = _config.ManagedApps.Where(a => a.SuspendDuringSession).ToList();
        if (apps.Count == 0) return;

        var frozen = 0;

        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.ProcessName)) continue;

            foreach (var pid in _suspender.GetProcessIds(app.ProcessName))
            {
                lock (_lock)
                {
                    if (_suspended.ContainsKey(pid)) continue; // already frozen this session
                }

                if (SuspendSafety.RefusalReason(app.ProcessName, pid, _currentProcessId) is string refusal)
                {
                    Log.Warn("Suspend", $"Refusing to freeze {app.DisplayName} (PID {pid}): {refusal}.");
                    continue;
                }

                if (!_suspender.Suspend(pid))
                {
                    Log.Debug("Suspend", $"Could not freeze {app.DisplayName} (PID {pid}) — probably elevated above this app.");
                    continue;
                }

                lock (_lock) _suspended[pid] = app.DisplayName;
                frozen++;

                // Only after a successful suspend: trimming a running process just makes it fault
                // its pages straight back in, which costs disk I/O and achieves nothing.
                _suspender.TrimWorkingSet(pid);
            }
        }

        if (frozen > 0)
            Log.Info("Suspend", $"Froze {frozen} process(es) for the session and released their memory. All will be resumed when SteamVR stops.");
    }

    public void ResumeAll()
    {
        List<KeyValuePair<int, string>> toResume;
        lock (_lock)
        {
            if (_suspended.Count == 0) return;
            toResume = _suspended.ToList();
            _suspended.Clear();
        }

        var resumed = 0;
        var failed = new List<string>();

        foreach (var (pid, name) in toResume)
        {
            if (!_suspender.IsRunning(pid)) continue; // exited while frozen; nothing to do

            if (_suspender.Resume(pid)) resumed++;
            else failed.Add($"{name} (PID {pid})");
        }

        if (resumed > 0) Log.Info("Suspend", $"Resumed {resumed} process(es) now the session has ended.");

        // Loud, because the symptom of getting this wrong is an application that appears hung and
        // gives the user no way to work out why.
        if (failed.Count > 0)
            Log.Error("Suspend", $"FAILED to resume: {string.Join(", ", failed)}. These are still frozen and will look hung — resume them with Process Explorer, or end and restart them.");
    }

    /// <summary>Which apps are frozen right now, for the status view.</summary>
    public IReadOnlyList<string> FrozenAppNames
    {
        get { lock (_lock) return _suspended.Values.Distinct().OrderBy(n => n).ToList(); }
    }

    public int FrozenCount { get { lock (_lock) return _suspended.Count; } }
}
