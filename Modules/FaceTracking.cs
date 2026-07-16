using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class ModuleActivity
{
    public int Pid { get; init; }
    public double? CpuPercentOverWindow { get; init; }
}

public sealed class FaceTrackingStatus
{
    public bool VirtualHereRunning { get; set; }
    public bool SRanipalRunning { get; set; }
    public bool VrcFaceTrackingRunning { get; set; }
    public int ModuleProcessCount { get; set; }
    public List<ModuleActivity> Modules { get; set; } = new();
    /// <summary>Real liveness: an ESTABLISHED TCP connection exists from some process to one of
    /// SRanipal's own local listening ports. Confirmed live 2026-07-16 via netstat: the
    /// face-tracking VRCFaceTracking.ModuleProcess connects to 127.0.0.1:1001/:1002, which are
    /// sr_runtime.exe's own listening ports — a much stronger signal than either process just
    /// being alive.</summary>
    public bool ModuleConnectedToSRanipal { get; set; }
    /// <summary>Whether the Vive Facial Tracker's USB device is actually attached right now via
    /// VirtualHere (server runs ON the headset; the tracker plugs into the Quest's USB-C hub).
    /// Confirmed live 2026-07-16: this can be false even while ModuleConnectedToSRanipal reads
    /// true and vhui64.exe (the client) is running — the module&lt;-&gt;SRanipal TCP link doesn't
    /// necessarily tear down just because the upstream USB feed vanished. Checked via a direct
    /// WMI Win32_PnPEntity query, which (unlike the Get-PnpDevice cmdlet) only enumerates
    /// currently-active devices — no match means genuinely not attached, not just "unknown".</summary>
    public bool ViveCameraDevicePresent { get; set; }
}

/// <summary>
/// Monitors and self-heals the face-tracking pipeline: Vive Facial Tracker (USB) -> VirtualHere
/// (vhui64.exe, backed by the "vhclient" Windows Service) -> SRanipal (sr_runtime.exe) ->
/// VRCFaceTracking's face module (one of its VRCFaceTracking.ModuleProcess.exe children).
///
/// Investigated 2026-07-16 whether the same graceful UI-automation restart used for Baballonia
/// (see EyeTracking.cs) would work here — it doesn't:
///  - sr_runtime.exe is headless: FindFirst for a top-level window returns nothing at all.
///  - VRCFaceTracking.exe has a real window (confirmed WindowVisualState=Normal, IsOffscreen=
///    false, a real bounding rectangle) but its UI framework exposes NO automatable child
///    elements — FindAll(Children) on the window returns nothing. No "reload module" button is
///    reachable via UI Automation, unlike Baballonia's Avalonia UI.
/// So the only available remediation is a blunt kill+relaunch of the process(es) involved, not a
/// graceful in-app click.
///
/// vhui64.exe runs as a real Windows Service ("vhclient" / "VirtualHere Client USB Sharing") plus
/// a separate tray/UI client process (confirmed via Get-Service). This monitor only
/// crash-recovers the client PROCESS the same way as everything else (ProcessLauncher). Restarting
/// the underlying SERVICE would need a different mechanism (sc.exe / ServiceController) — not
/// implemented; flagged as a smaller follow-up if the client-level restart ever proves
/// insufficient.
///
/// See HandleStalledConnectionAsync's doc for the full 2026-07-16 escalation history: a plain
/// sr_runtime.exe kill+relaunch was confirmed (via live logs across a 14+ minute episode) to
/// never recover a stalled module<->SRanipal connection on its own, so repeated failures now
/// escalate to also restarting VRCFaceTracking.exe, with a backoff if even that keeps failing.
/// </summary>
public sealed class FaceTrackingMonitor : IDisposable
{
    // The Vive Facial Tracker's USB device identity once shared via VirtualHere and picked up by
    // Windows locally. Confirmed live 2026-07-16 via Get-PnpDevice -FriendlyName.
    private const string ViveCameraDeviceName = "HTC Multimedia Camera";

    private readonly MonitorConfig _config;
    private readonly ProcessLauncher _launcher = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private FaceTrackingStatus _last = new();

    private readonly Dictionary<int, TimeSpan> _lastCpuTimeByPid = new();
    private DateTime _lastSampleAtUtc = DateTime.MinValue;

    private DateTime? _disconnectedSinceUtc;
    private DateTime? _lastSRanipalFixAttemptUtc;
    private int _consecutiveFailedFixes;
    private DateTime? _autoFixBackoffUntilUtc;

    public FaceTrackingStatus Current => _last;

    public FaceTrackingMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("FaceTracking", "Started face-tracking pipeline monitoring (VirtualHere/SRanipal/VRCFaceTracking).");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("FaceTracking", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await CheckOnceAsync().ConfigureAwait(false);
            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task<FaceTrackingStatus> CheckOnceAsync()
    {
        var status = new FaceTrackingStatus
        {
            VirtualHereRunning = IsRunning("vhui64"),
            SRanipalRunning = IsRunning("sr_runtime"),
            VrcFaceTrackingRunning = IsRunning("VRCFaceTracking"),
        };

        status.Modules = SampleModuleActivity();
        status.ModuleProcessCount = status.Modules.Count;
        status.ModuleConnectedToSRanipal = CheckModuleConnectedToSRanipal();
        status.ViveCameraDevicePresent = CheckViveCameraDevicePresent();

        LogTransition("vhui64.exe (VirtualHere client)", _last.VirtualHereRunning, status.VirtualHereRunning);
        LogTransition("sr_runtime.exe (SRanipal)", _last.SRanipalRunning, status.SRanipalRunning);
        LogTransition("VRCFaceTracking.exe", _last.VrcFaceTrackingRunning, status.VrcFaceTrackingRunning);

        if (status.VrcFaceTrackingRunning)
        {
            var hadModules = _last.ModuleProcessCount > 0;
            var hasModules = status.ModuleProcessCount > 0;
            if (hadModules && !hasModules)
                Log.Warn("FaceTracking", "VRCFaceTracking is running but its module process count dropped to 0.");
            else if (!hadModules && hasModules)
                Log.Info("FaceTracking", $"VRCFaceTracking module process(es) came up: {status.ModuleProcessCount} active.");
            else if (hasModules && status.ModuleProcessCount != _last.ModuleProcessCount)
                Log.Info("FaceTracking", $"VRCFaceTracking module process count changed: {_last.ModuleProcessCount} -> {status.ModuleProcessCount}.");
        }

        if (_last.ModuleConnectedToSRanipal != status.ModuleConnectedToSRanipal)
        {
            Log.Info("FaceTracking", status.ModuleConnectedToSRanipal
                ? "A VRCFaceTracking module now has a live connection to SRanipal."
                : "No VRCFaceTracking module is connected to SRanipal anymore.");
            if (status.ModuleConnectedToSRanipal)
                SteamVrNotifier.TryNotify(_config, "Face tracking recovered");
        }

        if (_last.ViveCameraDevicePresent != status.ViveCameraDevicePresent)
        {
            Log.Info("FaceTracking", status.ViveCameraDevicePresent
                ? "Vive Facial Tracker ('HTC Multimedia Camera') is now attached via VirtualHere."
                : "Vive Facial Tracker ('HTC Multimedia Camera') is no longer attached — VirtualHere's share to the headset-side server is down.");
            SteamVrNotifier.TryNotify(_config, status.ViveCameraDevicePresent
                ? "Vive Facial Tracker attached"
                : "Vive Facial Tracker disconnected");
        }

        foreach (var m in status.Modules)
        {
            var cpuStr = m.CpuPercentOverWindow is double pct ? $"{pct:F1}%" : "n/a (just started)";
            Log.Trace("FaceTracking", $"Module PID {m.Pid}: CPU={cpuStr} (informational only, not a liveness signal)");
        }

        Log.Trace("FaceTracking", $"vhui64={status.VirtualHereRunning} sr_runtime={status.SRanipalRunning} " +
                                   $"vrcFaceTracking={status.VrcFaceTrackingRunning} moduleProcesses={status.ModuleProcessCount} " +
                                   $"moduleConnectedToSRanipal={status.ModuleConnectedToSRanipal} " +
                                   $"viveCameraDevicePresent={status.ViveCameraDevicePresent}");

        _last = status;

        await HandleCrashRecoveryAsync(status).ConfigureAwait(false);
        await HandleStalledConnectionAsync(status).ConfigureAwait(false);

        return status;
    }

    private bool CheckModuleConnectedToSRanipal()
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            return props.GetActiveTcpConnections().Any(c =>
                c.State == TcpState.Established &&
                c.RemoteEndPoint.Address.Equals(IPAddress.Loopback) &&
                c.RemoteEndPoint.Port >= _config.Network.SRanipalPortRangeStart &&
                c.RemoteEndPoint.Port <= _config.Network.SRanipalPortRangeEnd);
        }
        catch (Exception ex)
        {
            Log.Debug("FaceTracking", $"GetActiveTcpConnections() threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>Queries WMI directly (Win32_PnPEntity) rather than shelling out to the
    /// Get-PnpDevice cmdlet, both for speed (this runs every ~5s) and because Get-PnpDevice's
    /// underlying provider returns historical/ghost entries for devices that aren't currently
    /// attached (all showing Present=False) — confirmed live 2026-07-16 it listed 11 stale
    /// entries while the device was disconnected. A plain Win32_PnPEntity query only enumerates
    /// currently-active devices, so any match here means it's genuinely attached right now.</summary>
    private static bool CheckViveCameraDevicePresent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_PnPEntity WHERE Name = '{ViveCameraDeviceName}'");
            using var results = searcher.Get();
            return results.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Debug("FaceTracking", $"WMI Win32_PnPEntity query threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>sr_runtime.exe and vhui64.exe get unconditional crash-recovery here.
    /// VRCFaceTracking.exe does NOT — its start/stop lifecycle is owned by
    /// VrcFaceTrackingLifecycleManager (launched on tracker presence, shut down after a delay
    /// with none present), which would otherwise fight with unconditional relaunching here.</summary>
    private async Task HandleCrashRecoveryAsync(FaceTrackingStatus status)
    {
        if (!status.SRanipalRunning)
            // suppressUacPrompt: sr_runtime.exe's manifest requests requestedExecutionLevel
            // "highestAvailable", which triggers a UAC consent prompt on every launch on an
            // admin-capable account — fine for a human, fatal for this unattended auto-relaunch
            // (confirmed live 2026-07-16: nothing there to click "Yes", launch just hangs). See
            // ProcessLauncher.EnsureRunningAsync's suppressUacPrompt doc for the mechanism.
            await RelaunchAsync("sr_runtime", _config.Paths.SRanipalExe, suppressUacPrompt: true).ConfigureAwait(false);

        if (!status.VirtualHereRunning)
            await RelaunchAsync("vhui64", _config.Paths.VirtualHereClientExe).ConfigureAwait(false);
    }

    private async Task RelaunchAsync(string processName, string exePath, bool suppressUacPrompt = false)
    {
        Log.Warn("FaceTracking", $"'{processName}' is not running — attempting to relaunch it.");
        var result = await _launcher.EnsureRunningAsync(
            processName, exePath, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs,
            suppressUacPrompt: suppressUacPrompt).ConfigureAwait(false);

        if (!result.Success && !result.AlreadyRunning)
            Log.Warn("FaceTracking", $"Relaunch of '{processName}' did not confirm success: {result.Error}");
    }

    /// <summary>If SRanipal and VRCFaceTracking are both alive with a module loaded, but nothing
    /// is actually connected to SRanipal, that's the "connection dead but nobody crashed" case —
    /// same failure shape as a stuck eye camera, but with no graceful fix available (see class
    /// doc). Requires the disconnect to be sustained (not a single blip) and respects a cooldown
    /// so a persistently broken link doesn't get kill-looped.
    ///
    /// Gated on ViveCameraDevicePresent: if the camera itself isn't attached (VirtualHere's
    /// share to the headset-side server is down), restarting sr_runtime.exe can't fix that — it's
    /// not a software problem on this end, so don't waste a kill+relaunch cycle on it. Just
    /// log/alert via the ViveCameraDevicePresent transition above instead.
    ///
    /// ESCALATION (added 2026-07-16 after live evidence): killing and relaunching sr_runtime.exe
    /// alone is NOT sufficient to fix a stalled connection. Logs from a real 14+ minute episode
    /// showed ModuleConnectedToSRanipal reading false on every single check immediately after a
    /// relaunch, across 6+ consecutive attempts, with ViveCameraDevicePresent staying true the
    /// entire time (ruling out USB/attach flapping) — a physical replug during that same episode
    /// didn't help either. The likely explanation: VRCFaceTracking's module process only attempts
    /// its SRanipal connection once, at its own startup, and never retries on its own — a fresh
    /// sr_runtime listening on the same ports is irrelevant if nothing ever asks it for a new
    /// connection. After <see cref="FaceTrackingAutoFixConfig.EscalateToVrcFaceTrackingRestartAfterAttempts"/>
    /// consecutive failed sr_runtime-only attempts, this also kills VRCFaceTracking.exe itself —
    /// deliberately NOT relaunched here, since VrcFaceTrackingLifecycleManager already owns that
    /// process's lifecycle and will bring it back within one of its own ~5s poll cycles as long as
    /// a tracker is still present, forcing every module to reload and actually retry the
    /// connection. If escalated attempts also keep failing, GiveUpAfterAttempts stops automated
    /// recovery for a long backoff instead of hammering both processes forever — at that point the
    /// fault is very likely upstream (VirtualHere on the headset, the physical USB link) and no
    /// amount of local restarting will fix it.</summary>
    private async Task HandleStalledConnectionAsync(FaceTrackingStatus status)
    {
        if (!_config.FaceTrackingAutoFix.Enabled) return;

        var now = DateTime.UtcNow;

        if (_autoFixBackoffUntilUtc is DateTime backoffUntil)
        {
            if (now < backoffUntil)
            {
                Log.Trace("FaceTracking", $"Auto-fix backed off after repeated failures ({(backoffUntil - now).TotalSeconds:F0}s remaining before retrying).");
                return;
            }
            _autoFixBackoffUntilUtc = null;
            _consecutiveFailedFixes = 0;
            Log.Info("FaceTracking", "Auto-fix backoff period elapsed — will attempt recovery again if still stalled.");
        }

        var pipelineShouldBeConnected = status.SRanipalRunning && status.VrcFaceTrackingRunning &&
                                         status.ModuleProcessCount > 0 && status.ViveCameraDevicePresent;
        if (!pipelineShouldBeConnected || status.ModuleConnectedToSRanipal)
        {
            _disconnectedSinceUtc = null;
            // Only a genuine reconnect clears the escalation counter. A momentarily-not-applicable
            // pipeline (e.g. VRCFaceTracking briefly down because THIS method just killed it as
            // part of an escalated attempt) must not silently erase how many attempts already
            // failed, or escalation could loop forever without ever reaching GiveUpAfterAttempts.
            if (status.ModuleConnectedToSRanipal)
                _consecutiveFailedFixes = 0;
            return;
        }

        _disconnectedSinceUtc ??= now;

        var sustainedFor = now - _disconnectedSinceUtc.Value;
        if (sustainedFor.TotalMilliseconds < _config.FaceTrackingAutoFix.SustainedDisconnectMs)
            return;

        var cooldown = TimeSpan.FromMilliseconds(_config.FaceTrackingAutoFix.CooldownMs);
        if (_lastSRanipalFixAttemptUtc is DateTime last && now - last < cooldown)
        {
            Log.Trace("FaceTracking", $"Stalled-connection fix on cooldown ({(cooldown - (now - last)).TotalSeconds:F0}s remaining).");
            return;
        }

        _lastSRanipalFixAttemptUtc = now;
        _consecutiveFailedFixes++;
        var escalate = _consecutiveFailedFixes >= _config.FaceTrackingAutoFix.EscalateToVrcFaceTrackingRestartAfterAttempts;

        Log.Warn("FaceTracking", $"Module<->SRanipal connection has been down for {sustainedFor.TotalSeconds:F0}s while both processes are running (attempt {_consecutiveFailedFixes}) — killing and relaunching sr_runtime.exe{(escalate ? ", and also restarting VRCFaceTracking.exe since sr_runtime-only fixes haven't worked" : "")}.");
        SteamVrNotifier.TryNotify(_config, escalate ? "Face tracking stalled — restarting SRanipal + VRCFaceTracking" : "Face tracking stalled — restarting SRanipal");

        try
        {
            foreach (var proc in Process.GetProcessesByName("sr_runtime"))
            {
                try
                {
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
            Log.Error("FaceTracking", "Killing sr_runtime.exe threw", ex);
        }

        if (escalate)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("VRCFaceTracking"))
                {
                    try
                    {
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
                Log.Error("FaceTracking", "Killing VRCFaceTracking.exe threw", ex);
            }
        }

        await Task.Delay(500).ConfigureAwait(false); // let the ports fully release before relaunch

        var result = await _launcher.EnsureRunningAsync(
            "sr_runtime", _config.Paths.SRanipalExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs,
            suppressUacPrompt: true).ConfigureAwait(false);

        Log.Info("FaceTracking", result.Success
            ? "sr_runtime.exe relaunched after stalled-connection fix."
            : $"sr_runtime.exe relaunch did not confirm success: {result.Error}");

        if (escalate)
            Log.Info("FaceTracking", "VRCFaceTracking.exe killed — VrcFaceTrackingLifecycleManager will relaunch it since a tracker is still present.");

        if (_consecutiveFailedFixes >= _config.FaceTrackingAutoFix.GiveUpAfterAttempts)
        {
            _autoFixBackoffUntilUtc = now + TimeSpan.FromMilliseconds(_config.FaceTrackingAutoFix.GiveUpCooldownMs);
            Log.Error("FaceTracking", $"Auto-fix attempted {_consecutiveFailedFixes} times without the connection recovering — pausing automatic recovery for {_config.FaceTrackingAutoFix.GiveUpCooldownMs / 60000.0:F0} min. Likely an upstream issue (VirtualHere on the headset, or the physical USB link) that local restarts can't fix — check manually.");
            SteamVrNotifier.TryNotify(_config, "Face tracking auto-fix giving up — check manually");
        }

        _disconnectedSinceUtc = null;
    }

    private List<ModuleActivity> SampleModuleActivity()
    {
        var now = DateTime.UtcNow;
        var elapsed = _lastSampleAtUtc == DateTime.MinValue ? TimeSpan.Zero : now - _lastSampleAtUtc;
        _lastSampleAtUtc = now;

        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName("VRCFaceTracking.ModuleProcess");
        }
        catch (Exception ex)
        {
            Log.Debug("FaceTracking", $"GetProcessesByName(VRCFaceTracking.ModuleProcess) threw: {ex.Message}");
            return new List<ModuleActivity>();
        }

        var seenPids = new HashSet<int>();
        var results = new List<ModuleActivity>();

        foreach (var proc in procs)
        {
            int pid;
            TimeSpan cpuTime;
            try
            {
                pid = proc.Id;
                cpuTime = proc.TotalProcessorTime;
            }
            catch (Exception ex)
            {
                Log.Debug("FaceTracking", $"Reading process info failed (likely exited mid-scan): {ex.Message}");
                continue;
            }
            finally
            {
                proc.Dispose();
            }

            seenPids.Add(pid);

            double? cpuPercent = null;
            if (_lastCpuTimeByPid.TryGetValue(pid, out var previousCpuTime) && elapsed > TimeSpan.Zero)
            {
                var cpuDeltaMs = (cpuTime - previousCpuTime).TotalMilliseconds;
                cpuPercent = Math.Max(0, cpuDeltaMs) / elapsed.TotalMilliseconds * 100.0;
            }

            _lastCpuTimeByPid[pid] = cpuTime;
            results.Add(new ModuleActivity { Pid = pid, CpuPercentOverWindow = cpuPercent });
        }

        foreach (var staleKey in _lastCpuTimeByPid.Keys.Where(k => !seenPids.Contains(k)).ToList())
        {
            _lastCpuTimeByPid.Remove(staleKey);
        }

        return results;
    }

    private static void LogTransition(string label, bool wasRunning, bool isRunning)
    {
        if (wasRunning == isRunning) return;
        if (isRunning)
            Log.Info("FaceTracking", $"{label} started.");
        else
            Log.Warn("FaceTracking", $"{label} stopped.");
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception ex)
        {
            Log.Debug("FaceTracking", $"GetProcessesByName({processName}) threw: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
