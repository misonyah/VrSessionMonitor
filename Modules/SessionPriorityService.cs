using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Holds configured processes at a chosen CPU priority for the length of a VR session, and puts
/// them back afterwards.
///
/// The usual reason is to push a background application — a Unity editor, a compile, a browser —
/// out of the way of the session without closing it. Distinct from the vr-process-priority-boost
/// optimization, which writes IFEO entries applied when a process LAUNCHES; this one adjusts
/// processes that are already running, and restores them.
///
/// Restoring is keyed on PID and on what the process was ACTUALLY at when we changed it, not on
/// an assumed Normal. Two things follow. A process that exits and restarts mid-session is left
/// alone, because the new PID is not the one we lowered — forcing a remembered priority onto an
/// unrelated process would be worse than doing nothing. And a process the user had deliberately
/// set to something unusual goes back to that, not to Normal.
///
/// Every failure is swallowed and logged. Access-denied is routine for a process elevated above us
/// or owned by another user, and a failed convenience tweak must never break session startup.
/// </summary>
public sealed class SessionPriorityService
{
    private readonly MonitorConfig _config;
    private readonly IProcessPriorityController _controller;

    /// <summary>PID to the priority it had before we touched it. Only holds processes we actually
    /// changed, so restore never guesses.</summary>
    private readonly Dictionary<int, AppProcessPriority> _original = new();
    private readonly object _lock = new();

    public SessionPriorityService(MonitorConfig config, IProcessPriorityController controller)
    {
        _config = config;
        _controller = controller;
    }

    /// <summary>Plain method rather than an event subscription, matching OptimizationsManager, so
    /// it is directly callable from tests without a SteamVrMonitor.</summary>
    public void HandleSteamVrRunningChanged(bool isRunning)
    {
        if (isRunning) ApplySessionPriorities();
        else RestoreOriginalPriorities();
    }

    public void ApplySessionPriorities()
    {
        var apps = _config.ManagedApps.Where(a => a.SessionPriority is not null).ToList();
        if (apps.Count == 0) return;

        var changed = 0;

        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.ProcessName)) continue;
            var wanted = app.SessionPriority!.Value;

            foreach (var pid in _controller.GetProcessIds(app.ProcessName))
            {
                lock (_lock)
                {
                    if (_original.ContainsKey(pid)) continue; // already adjusted this session
                }

                var current = _controller.GetPriority(pid);
                if (current is null) continue;                 // vanished between listing and reading
                if (current == wanted) continue;                // already there; nothing to restore later

                if (!_controller.SetPriority(pid, wanted))
                {
                    Log.Debug("Priority", $"Could not set {app.DisplayName} (PID {pid}) to {wanted} — it is probably elevated above this app.");
                    continue;
                }

                lock (_lock) _original[pid] = current.Value;
                changed++;
                Log.Info("Priority", $"{app.DisplayName} (PID {pid}) {current} -> {wanted} for this session.");
            }
        }

        if (changed > 0)
            Log.Info("Priority", $"Adjusted {changed} process(es) for the session; each will be put back when SteamVR stops.");
    }

    public void RestoreOriginalPriorities()
    {
        List<KeyValuePair<int, AppProcessPriority>> toRestore;
        lock (_lock)
        {
            if (_original.Count == 0) return;
            toRestore = _original.ToList();
            _original.Clear();
        }

        var restored = 0;
        foreach (var (pid, original) in toRestore)
        {
            // Only restore a process still at the priority we left it at. If something else moved
            // it since — the user, or the app itself — that is a newer decision than ours.
            var current = _controller.GetPriority(pid);
            if (current is null) continue; // exited during the session, which is fine

            if (_controller.SetPriority(pid, original)) restored++;
        }

        if (restored > 0)
            Log.Info("Priority", $"Restored the original priority of {restored} process(es) now the session has ended.");
    }

    /// <summary>How many processes are currently held at a session priority, for the status view.</summary>
    public int AdjustedCount { get { lock (_lock) return _original.Count; } }
}
