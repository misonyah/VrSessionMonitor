using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Modules;

namespace VrSessionMonitor.Tests.Fakes;

/// <summary>In-memory IProcessLauncher for tests. EnsureRunningAsync marks the process "running";
/// Kill clears it. All calls are recorded in order for assertion.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    public readonly List<string> EnsureRunningCalls = new();
    public readonly List<string> KillCalls = new();
    public readonly HashSet<string> Running = new();

    /// <summary>Optional: PID returned by GetProcessId, keyed by process name.</summary>
    public readonly Dictionary<string, int?> ProcessIds = new();

    /// <summary>Optional: start time returned by GetStartTime, keyed by process name. Unset = null
    /// (i.e. "not running"), which is what the start-time comparison paths treat as a no-op.</summary>
    public readonly Dictionary<string, DateTime?> StartTimes = new();

    public Task<ProcessLauncher.LaunchResult> EnsureRunningAsync(
        string processName, string exePath, string? args,
        int startupTimeoutMs, int pollIntervalMs,
        string? workingDirectory = null, bool suppressUacPrompt = false)
    {
        EnsureRunningCalls.Add(processName);
        var already = Running.Contains(processName);
        Running.Add(processName);
        return Task.FromResult(new ProcessLauncher.LaunchResult(
            AlreadyRunning: already, Started: !already, Success: true, Error: null));
    }

    public bool IsRunning(string processName) => Running.Contains(processName);

    public int? GetProcessId(string processName) =>
        ProcessIds.TryGetValue(processName, out var pid) ? pid : (Running.Contains(processName) ? 1 : null);

    public DateTime? GetStartTime(string processName) =>
        StartTimes.TryGetValue(processName, out var t) ? t : null;

    public void Kill(string processName)
    {
        KillCalls.Add(processName);
        Running.Remove(processName);
    }

    public void KillOrphanedChildIfLauncherGone(string launcherProcessName, string launcherExePath, string orphanChildProcessName)
    {
        // No orphan concept in the fake; record via KillCalls so callers can assert it ran.
        KillCalls.Add($"orphan:{orphanChildProcessName}");
    }
}
