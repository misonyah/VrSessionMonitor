using System.Collections.Concurrent;
using System.Diagnostics;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Fixes the exact bug found in vrc.cmd on 2026-07-15: Windows timeout.exe fails instantly when
/// stdout is redirected ("Input redirection is not supported"), so the old retry loop had zero
/// real delay between "is it running yet?" checks and fired the launch command multiple times
/// before the first instance registered in tasklist — VRChat's own named-pipe log showed
/// "All pipe instances are busy" and SlimeVR visibly started twice.
///
/// This launcher never uses shell delays — it uses Task.Delay to actually wait for the new
/// process to register before letting anyone else attempt a launch, serialized per process name
/// via a static SemaphoreSlim (shared across all ProcessLauncher instances in this app).
///
/// A named Mutex was used here originally, then replaced 2026-07-16 after live-testing surfaced
/// "Object synchronization method was called from an unsynchronized block of code" the first
/// time a launch actually had to wait through the polling loop (every earlier test that night
/// happened to hit the "already running" fast path, which never awaits). Mutex requires
/// ReleaseMutex() to run on the exact thread that called WaitOne() — but this method awaits
/// Task.Delay with ConfigureAwait(false) between acquire and release, so the continuation can
/// legitimately resume on a different thread pool thread, which then can't release the mutex it
/// didn't (as far as the OS is concerned) acquire. SemaphoreSlim has no such thread affinity and
/// is the correct primitive for a lock that needs to survive an await.
/// </summary>
public sealed class ProcessLauncher
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocksByProcessName = new();

    public sealed record LaunchResult(bool AlreadyRunning, bool Started, bool Success, string? Error);

    public async Task<LaunchResult> EnsureRunningAsync(
        string processName,
        string exePath,
        string? args,
        int startupTimeoutMs,
        int pollIntervalMs,
        string? workingDirectory = null,
        bool suppressUacPrompt = false)
    {
        var lockObj = LocksByProcessName.GetOrAdd(processName, _ => new SemaphoreSlim(1, 1));
        var acquired = await lockObj.WaitAsync(LockTimeout).ConfigureAwait(false);

        if (!acquired)
        {
            var msg = $"Timed out waiting for launch lock for '{processName}' — another launch is stuck.";
            Log.Error("ProcessLauncher", msg);
            return new LaunchResult(false, false, false, msg);
        }

        try
        {
            if (IsRunning(processName))
            {
                Log.Debug("ProcessLauncher", $"'{processName}' already running, skipping launch.");
                return new LaunchResult(AlreadyRunning: true, Started: false, Success: true, Error: null);
            }

            if (!File.Exists(exePath))
            {
                var msg = $"Executable not found: {exePath}";
                Log.Error("ProcessLauncher", msg);
                return new LaunchResult(false, false, false, msg);
            }

            Log.Info("ProcessLauncher", $"Launching '{processName}': \"{exePath}\" {args}{(suppressUacPrompt ? " (UAC-suppressed via __COMPAT_LAYER=RunAsInvoker)" : "")}");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args ?? "",
                    // UseShellExecute=true silently ignores custom EnvironmentVariables (a real
                    // .NET behavior, not a bug) — suppressUacPrompt needs false here so the
                    // __COMPAT_LAYER override below actually reaches the child process.
                    UseShellExecute = !suppressUacPrompt,
                    WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exePath) ?? "",
                    WindowStyle = ProcessWindowStyle.Minimized,
                };

                if (suppressUacPrompt)
                {
                    // Same fix already used in et.cmd for this exact class of app: some tools
                    // (confirmed live 2026-07-16 for sr_runtime.exe, which embeds
                    // requestedExecutionLevel level='highestAvailable') trigger a UAC consent
                    // prompt on every launch when the account is admin-capable. Fine for a human
                    // clicking "Yes", fatal for an unattended automated relaunch — nothing is
                    // there to click it, so the launch just hangs. __COMPAT_LAYER=RunAsInvoker is
                    // a real Windows Application Compatibility shim that forces invoker-level
                    // privilege regardless of the manifest's request, skipping the prompt.
                    // "highestAvailable" (not "requireAdministrator") means the app is designed
                    // to still function without elevation, just prefers it when offered.
                    psi.EnvironmentVariables["__COMPAT_LAYER"] = "RunAsInvoker";
                }

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Error("ProcessLauncher", $"Process.Start failed for '{processName}'", ex);
                return new LaunchResult(false, true, false, ex.Message);
            }

            var elapsed = 0;
            while (elapsed < startupTimeoutMs)
            {
                await Task.Delay(pollIntervalMs).ConfigureAwait(false);
                elapsed += pollIntervalMs;

                if (IsRunning(processName))
                {
                    Log.Info("ProcessLauncher", $"'{processName}' confirmed running after {elapsed}ms.");
                    return new LaunchResult(false, true, true, null);
                }

                Log.Trace("ProcessLauncher", $"Still waiting for '{processName}' to appear ({elapsed}/{startupTimeoutMs}ms)...");
            }

            var timeoutMsg = $"'{processName}' did not appear in process list within {startupTimeoutMs}ms after launch.";
            Log.Warn("ProcessLauncher", timeoutMsg);
            return new LaunchResult(false, true, false, timeoutMsg);
        }
        finally
        {
            lockObj.Release();
        }
    }

    public static bool IsRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception ex)
        {
            Log.Debug("ProcessLauncher", $"GetProcessesByName({processName}) threw: {ex.Message}");
            return false;
        }
    }

    public static int? GetProcessId(string processName)
    {
        try
        {
            using var proc = Process.GetProcessesByName(processName).FirstOrDefault();
            return proc?.Id;
        }
        catch (Exception ex)
        {
            Log.Debug("ProcessLauncher", $"GetProcessesByName({processName}) threw: {ex.Message}");
            return null;
        }
    }
}
