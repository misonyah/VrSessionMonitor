namespace VrSessionMonitor.Modules;

/// <summary>
/// Testability seam over ProcessLauncher's outward actions so state-driven callers
/// (SessionOrchestrator, the presence lifecycle managers) can run in unit tests without
/// touching real OS processes. ProcessLauncher implements this; FakeProcessLauncher (test
/// project) records calls instead. The concrete statics on ProcessLauncher stay in place for
/// the UI code that calls them directly.
/// </summary>
public interface IProcessLauncher
{
    Task<ProcessLauncher.LaunchResult> EnsureRunningAsync(
        string processName, string exePath, string? args,
        int startupTimeoutMs, int pollIntervalMs,
        string? workingDirectory = null, bool suppressUacPrompt = false);

    bool IsRunning(string processName);
    int? GetProcessId(string processName);

    /// <summary>Start time of the (first, current-session) process with this name, or null if none
    /// is running. Lets start-time comparisons (VRCFaceTracking-vs-VRChat staleness, max-uptime)
    /// go through the seam instead of touching System.Diagnostics.Process directly, so they're
    /// unit-testable.</summary>
    DateTime? GetStartTime(string processName);

    /// <summary>Kill every process with this name (entire process tree), waiting briefly for exit.
    /// Consolidates the identical GetProcessesByName(...).Kill loop the managers each hand-rolled.</summary>
    void Kill(string processName);

    void KillOrphanedChildIfLauncherGone(string launcherProcessName, string launcherExePath, string orphanChildProcessName);

    /// <summary>Which PIDs were started by VrSessionMonitor — used to tell a managed start from a
    /// manual one. Process-wide on the real launcher (see ProcessLauncher.SharedProvenance):
    /// several components construct their own ProcessLauncher, and provenance must span all of
    /// them.</summary>
    LaunchProvenance Provenance { get; }
}
