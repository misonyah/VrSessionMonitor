using System.Collections.Concurrent;

namespace VrSessionMonitor.Modules;

public enum AppStartOrigin
{
    NotRunning,
    /// <summary>Running under a PID VrSessionMonitor started itself.</summary>
    Managed,
    /// <summary>Running under a PID we never started — launched by hand, by Steam, or by another app.</summary>
    Manual,
}

/// <summary>
/// Tracks which PIDs VrSessionMonitor started, so an app running under a PID we never launched can
/// be identified as externally started. Deliberately keyed on PID rather than process name: a user
/// restarting VRChat by hand after the monitor launched it produces the same name with a new PID,
/// and that must read as manual rather than inheriting the old verdict.
///
/// Safe for concurrent use: on the real launcher (ProcessLauncher.SharedProvenance) this is one
/// process-wide static shared across independent launch paths — SessionOrchestrator launching
/// VRChat and, say, SlimeVrLifecycleManager launching SlimeVR can call RecordLaunched from
/// different threads at the same time — and it's also read from a polling loop, so plain
/// Dictionary reads/writes are not an option here.
/// </summary>
public sealed class LaunchProvenance
{
    private readonly ConcurrentDictionary<string, int> _launchedPids = new(StringComparer.OrdinalIgnoreCase);

    public void RecordLaunched(string processName, int pid) => _launchedPids[processName] = pid;

    public void Forget(string processName) => _launchedPids.TryRemove(processName, out _);

    public AppStartOrigin Classify(string processName, int? currentPid)
    {
        if (currentPid is not int pid) return AppStartOrigin.NotRunning;
        return _launchedPids.TryGetValue(processName, out var known) && known == pid
            ? AppStartOrigin.Managed
            : AppStartOrigin.Manual;
    }
}
