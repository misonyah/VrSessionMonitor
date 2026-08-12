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
public sealed class ProcessLauncher : IProcessLauncher
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocksByProcessName = new();

    /// <summary>This app's own Windows session (the interactive user session for a tray app).
    /// -1 means "couldn't determine" — in which case session-scoping is disabled and matching
    /// falls back to the old all-sessions behavior.</summary>
    private static readonly int CurrentSessionId = TryGetCurrentSessionId();

    private static int TryGetCurrentSessionId()
    {
        try { using var me = Process.GetCurrentProcess(); return me.SessionId; }
        catch { return -1; }
    }

    /// <summary>Every process this monitor launches/owns (the vhui64 CLIENT, sr_runtime, VRChat,
    /// SlimeVR, VRCFaceTracking, the VR overlays) runs in the interactive user session — never
    /// Session 0. Scoping all name matching to our own session stops IsRunning/Kill from ever
    /// matching an unrelated Session-0 SERVICE that happens to share an exe name. Confirmed live
    /// 2026-08-10: the VirtualHere USB *service* runs as vhui64.exe in Session 0, and the idle
    /// shutdown kept trying — and failing with Win32 "Access is denied" every cycle — to kill it.
    /// SessionId is read off the snapshot GetProcessesByName already populated; if it throws (e.g.
    /// a protected process), treat it as "not ours".</summary>
    private static bool InCurrentSession(Process p)
    {
        if (CurrentSessionId < 0) return true; // couldn't determine ours — don't filter
        try { return p.SessionId == CurrentSessionId; }
        catch { return false; }
    }

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
        var procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.Any(InCurrentSession);
        }
        catch (Exception ex)
        {
            Log.Debug("ProcessLauncher", $"GetProcessesByName({processName}) threw: {ex.Message}");
            return false;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public static int? GetProcessId(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.FirstOrDefault(InCurrentSession)?.Id;
        }
        catch (Exception ex)
        {
            Log.Debug("ProcessLauncher", $"GetProcessesByName({processName}) threw: {ex.Message}");
            return null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public static DateTime? GetStartTime(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            var proc = procs.FirstOrDefault(InCurrentSession);
            return proc?.StartTime;
        }
        catch (Exception ex)
        {
            Log.Debug("ProcessLauncher", $"GetStartTime({processName}) threw: {ex.Message}");
            return null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>
    /// Some launcher-style apps (e.g. SlimeVR's slimevr.exe, which spawns a jre\bin\java.exe
    /// child that hosts the actual server/RPC ports) can leave that child running after the
    /// launcher process itself is gone — confirmed live 2026-08-09, a java.exe from the previous
    /// day was still holding SlimeVR's RPC port (21110) more than 24h later, with no slimevr.exe
    /// alive, causing every subsequent launch attempt to fail with "Ports are busy" until someone
    /// manually killed it. Call this before launching such an app: if no live instance of
    /// launcherProcessName is found, kill any orphanChildProcessName process whose executable
    /// path lives under the same install directory as launcherExePath (path-scoped so this never
    /// touches an unrelated process that just happens to share the child's name).
    /// </summary>
    public static void KillOrphanedChildIfLauncherGone(string launcherProcessName, string launcherExePath, string orphanChildProcessName)
    {
        if (IsRunning(launcherProcessName))
            return;

        var installDir = Path.GetDirectoryName(launcherExePath);
        if (installDir == null)
            return;

        // Match against the install dir WITH a trailing separator, so a sibling directory whose
        // name is a superset of this one — e.g. a "...\common\SlimeVR2\jre\bin\java.exe" against
        // this launcher's "...\common\SlimeVR" — can't satisfy StartsWith and get an unrelated
        // process killed. This reap is the one place a name-matched process is force-killed, so the
        // path scope has to be exact.
        var installDirPrefix = installDir.EndsWith(Path.DirectorySeparatorChar)
            ? installDir
            : installDir + Path.DirectorySeparatorChar;

        foreach (var proc in Process.GetProcessesByName(orphanChildProcessName))
        {
            try
            {
                var modulePath = proc.MainModule?.FileName;
                if (InCurrentSession(proc) && modulePath != null && modulePath.StartsWith(installDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("ProcessLauncher",
                        $"Killing orphaned '{orphanChildProcessName}' process (PID {proc.Id}) under '{installDir}' - no live '{launcherProcessName}' launcher owns it.");
                    proc.Kill();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("ProcessLauncher", $"Failed to inspect/kill '{orphanChildProcessName}' process {proc.Id}: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    // --- IProcessLauncher instance members (forward to the statics that UI code still calls) ---

    bool IProcessLauncher.IsRunning(string processName) => IsRunning(processName);

    int? IProcessLauncher.GetProcessId(string processName) => GetProcessId(processName);

    void IProcessLauncher.KillOrphanedChildIfLauncherGone(string launcherProcessName, string launcherExePath, string orphanChildProcessName)
        => KillOrphanedChildIfLauncherGone(launcherProcessName, launcherExePath, orphanChildProcessName);

    DateTime? IProcessLauncher.GetStartTime(string processName) => GetStartTime(processName);

    public void Kill(string processName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!InCurrentSession(proc)) continue; // never touch a Session-0 namesake service
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("ProcessLauncher", $"Killing {processName}.exe threw", ex);
        }
    }
}
