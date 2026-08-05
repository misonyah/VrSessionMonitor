using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text.Json;
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
    /// <summary>Distinct from SRanipalRunning (the sr_runtime.exe process) — this is the
    /// "SRanipalService" Windows Service that actually backs it. Found live 2026-07-27: sr_runtime.exe
    /// can keep running as an orphaned shell after this service dies, with the loopback TCP
    /// connection to it staying "ESTABLISHED" as a zombie link. See SRanipalServiceConfig's doc for
    /// the full incident.</summary>
    public bool SRanipalServiceRunning { get; set; }
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
    private DateTime? _lastSRanipalServiceStartAttemptUtc;

    private static readonly string[] FaceOscParamNames = { "JawOpen", "JawX", "MouthClosed", "MouthX", "LipPucker", "TongueOut", "LipSuckLower", "LipSuckUpper" };
    private readonly OscFreshnessTracker _faceOscTracker = new();
    private DateTime? _lastFaceOscCheckAttemptUtc;
    private bool _lastFaceOscFrozenResult;

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
        status.SRanipalServiceRunning = CheckSRanipalServiceRunning();

        LogTransition("vhui64.exe (VirtualHere client)", _last.VirtualHereRunning, status.VirtualHereRunning);
        LogTransition("sr_runtime.exe (SRanipal)", _last.SRanipalRunning, status.SRanipalRunning);
        LogTransition("VRCFaceTracking.exe", _last.VrcFaceTrackingRunning, status.VrcFaceTrackingRunning);

        if (_last.SRanipalServiceRunning != status.SRanipalServiceRunning)
            Log.Info("FaceTracking", status.SRanipalServiceRunning
                ? $"'{_config.SRanipalService.ServiceName}' Windows Service is now running."
                : $"'{_config.SRanipalService.ServiceName}' Windows Service is NOT running — sr_runtime.exe may be an orphaned shell with no real backing service.");

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
                                   $"sranipalService={status.SRanipalServiceRunning} " +
                                   $"vrcFaceTracking={status.VrcFaceTrackingRunning} moduleProcesses={status.ModuleProcessCount} " +
                                   $"moduleConnectedToSRanipal={status.ModuleConnectedToSRanipal} " +
                                   $"viveCameraDevicePresent={status.ViveCameraDevicePresent}");

        _last = status;

        await HandleCrashRecoveryAsync(status).ConfigureAwait(false);
        await HandleSRanipalServiceRecoveryAsync(status).ConfigureAwait(false);
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

    private bool CheckSRanipalServiceRunning()
    {
        try
        {
            using var sc = new ServiceController(_config.SRanipalService.ServiceName);
            sc.Refresh();
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex)
        {
            Log.Debug("FaceTracking", $"Checking '{_config.SRanipalService.ServiceName}' service status threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>Starts the SRanipalService Windows Service when it's not running — see
    /// SRanipalServiceConfig's doc for why sr_runtime.exe's own process presence can't be trusted
    /// to mean this service is alive too. Cooldown-gated (rather than unconditional like
    /// HandleCrashRecoveryAsync's process relaunches) because a Start() failure here is much more
    /// likely to be persistent — wrong privilege level or a Disabled service — so retrying every
    /// ~5s would just spam identical failures instead of fixing anything.</summary>
    private async Task HandleSRanipalServiceRecoveryAsync(FaceTrackingStatus status)
    {
        if (!_config.SRanipalService.Enabled || status.SRanipalServiceRunning) return;

        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMilliseconds(_config.SRanipalService.RestartCooldownMs);
        if (_lastSRanipalServiceStartAttemptUtc is DateTime last && now - last < cooldown)
        {
            Log.Trace("FaceTracking", $"'{_config.SRanipalService.ServiceName}' start attempt on cooldown ({(cooldown - (now - last)).TotalSeconds:F0}s remaining).");
            return;
        }

        _lastSRanipalServiceStartAttemptUtc = now;
        var serviceName = _config.SRanipalService.ServiceName;
        Log.Warn("FaceTracking", $"'{serviceName}' Windows Service is not running — attempting to start it.");

        await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                sc.Refresh();

                if (sc.Status == ServiceControllerStatus.Running) return; // raced with an external start

                if (sc.StartType == ServiceStartMode.Disabled)
                {
                    Log.Warn("FaceTracking", $"'{serviceName}' is Disabled at the Service Control Manager level — can't start it automatically. Check services.msc.");
                    return;
                }

                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                Log.Info("FaceTracking", $"'{serviceName}' started successfully.");
                SteamVrNotifier.TryNotify(_config, $"Started {serviceName} (was stopped)");
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 5 })
            {
                Log.Warn("FaceTracking", $"Starting '{serviceName}' failed: access denied. This app likely needs to run elevated to control Windows Services.");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                Log.Warn("FaceTracking", $"'{serviceName}' didn't reach the Running state within 10s of Start() — it may still be starting slowly, will re-check next cycle.");
            }
            catch (Exception ex)
            {
                Log.Warn("FaceTracking", $"Starting '{serviceName}' failed: {ex.Message}");
            }
        }).ConfigureAwait(false);
    }

    /// <summary>vhui64.exe gets unconditional crash-recovery — it's the prerequisite that makes the
    /// Vive Facial Tracker's PnP device (and therefore ViveCameraDevicePresent below) ever appear
    /// at all, so gating it on that same signal would be circular.
    ///
    /// sr_runtime.exe is different: confirmed live 2026-07-28, it crashed and got unconditionally
    /// relaunched at 4:12am while the headset was completely off (no tracker, no eye camera, no
    /// VRChat/SteamVR session) — burning real CPU (127s of CPU time within ~2 minutes) for a
    /// runtime nothing needed. Unlike vhui64, ViveCameraDevicePresent doesn't depend on sr_runtime
    /// itself being alive (only on vhui64 having shared the device), so gating sr_runtime's
    /// recovery on it is safe and precise: it still crash-recovers instantly mid-session (the
    /// device stays present the whole time), but stops pointlessly relaunching when nothing is
    /// plugged in. VRCFaceTracking.exe gets no crash-recovery here at all — its start/stop
    /// lifecycle is owned by VrcFaceTrackingLifecycleManager (launched on tracker presence, shut
    /// down after a delay with none present), which would otherwise fight with relaunching here.</summary>
    private async Task HandleCrashRecoveryAsync(FaceTrackingStatus status)
    {
        if (!status.SRanipalRunning && status.ViveCameraDevicePresent)
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

        // ModuleConnectedToSRanipal is just a TCP-established check and can read true while real
        // data is frozen (see FaceTrackingAutoFixConfig.OscFreshnessEnabled's doc) — only worth the
        // HTTP round trip when everything else already looks healthy, since a genuinely disconnected
        // module already triggers the escalation below on its own.
        var oscFrozen = pipelineShouldBeConnected && status.ModuleConnectedToSRanipal &&
                         await CheckFaceOscFrozenAsync().ConfigureAwait(false);

        if (!pipelineShouldBeConnected || (status.ModuleConnectedToSRanipal && !oscFrozen))
        {
            _disconnectedSinceUtc = null;
            // Only a genuine reconnect clears the escalation counter. A momentarily-not-applicable
            // pipeline (e.g. VRCFaceTracking briefly down because THIS method just killed it as
            // part of an escalated attempt) must not silently erase how many attempts already
            // failed, or escalation could loop forever without ever reaching GiveUpAfterAttempts.
            if (status.ModuleConnectedToSRanipal && !oscFrozen)
                _consecutiveFailedFixes = 0;
            return;
        }

        if (oscFrozen)
            Log.Warn("FaceTracking", "VRChat's own face-tracking OSC parameters (JawOpen/JawX/MouthX/LipPucker/etc.) haven't changed " +
                                      "despite ModuleConnectedToSRanipal reading healthy — treating as a stalled connection.");

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

    /// <summary>Ground-truth check alongside ModuleConnectedToSRanipal's TCP-established test — see
    /// VrChatOscQueryClient's, OscFreshnessTracker's, and FaceTrackingAutoFixConfig.OscFreshnessEnabled's
    /// docs for the 2026-07-30 incident this mirrors on the eye-tracking side (a connection that
    /// looks fine while the real data is frozen). Uses per-parameter majority tracking rather than
    /// whole-bundle equality — confirmed live the same night: JawOpen/MouthClosed/LipSuckLower sat
    /// frozen for 15s+ while JawX/MouthX kept jittering, which whole-bundle equality would have
    /// missed entirely. Rate-limited internally to OscFreshnessCheckIntervalMs regardless of how
    /// often the caller checks, so a "not time to check yet" cycle returns the last real result
    /// instead of unconditionally false — confirmed live 2026-08-05 that returning false on every
    /// rate-limited cycle was a real bug, not just a safe default: this method is called every
    /// 5s (FaceTrackingMonitor's outer loop) but only actually re-checks every 10s
    /// (OscFreshnessCheckIntervalMs), so HandleStalledConnectionAsync's sustained-disconnect timer
    /// got reset to null on every other cycle and could never accumulate past 5s — permanently
    /// short of the 10s SustainedDisconnectMs threshold needed to ever trigger the actual restart.
    /// The warning log fired forever; the fix never did.</summary>
    private async Task<bool> CheckFaceOscFrozenAsync()
    {
        if (!_config.FaceTrackingAutoFix.OscFreshnessEnabled) return false;

        var now = DateTime.UtcNow;
        if (_lastFaceOscCheckAttemptUtc is DateTime lastAttempt &&
            now - lastAttempt < TimeSpan.FromMilliseconds(_config.FaceTrackingAutoFix.OscFreshnessCheckIntervalMs))
            return _lastFaceOscFrozenResult;
        _lastFaceOscCheckAttemptUtc = now;

        var current = await VrChatOscQueryClient.FetchParamsAsync(FaceOscParamNames).ConfigureAwait(false);
        if (current is null || current.Count == 0)
        {
            _faceOscTracker.Reset();
            _lastFaceOscFrozenResult = false; // couldn't check this cycle -- don't treat that as evidence of a freeze
            return false;
        }

        _lastFaceOscFrozenResult = _faceOscTracker.Update(current, now, TimeSpan.FromMilliseconds(_config.FaceTrackingAutoFix.OscFreshnessStaleThresholdMs));
        return _lastFaceOscFrozenResult;
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

    /// <summary>Surfaces the stalled-connection sustained/cooldown/backoff timers for the tray —
    /// previously only visible at Trace log level, so an sr_runtime restart (or the escalation to
    /// also restarting VRCFaceTracking) looked unprompted unless you were watching the log. Falls
    /// back to naming the missing prerequisite when the pipeline isn't even in a state this watches
    /// (e.g. VRCFaceTracking not running yet), rather than going silent.</summary>
    public string? DescribePendingAction()
    {
        if (!_config.FaceTrackingAutoFix.Enabled) return null;

        var now = DateTime.UtcNow;

        if (_autoFixBackoffUntilUtc is DateTime backoffUntil && now < backoffUntil)
            return $"auto-fix paused {(backoffUntil - now).TotalSeconds:F0}s (too many failed attempts)";

        if (_disconnectedSinceUtc is DateTime since)
        {
            var sustained = now - since;
            var sustainedThreshold = TimeSpan.FromMilliseconds(_config.FaceTrackingAutoFix.SustainedDisconnectMs);
            if (sustained < sustainedThreshold)
                return $"sr_runtime restart in {(sustainedThreshold - sustained).TotalSeconds:F0}s if still stalled";

            if (_lastSRanipalFixAttemptUtc is DateTime lastAttempt)
            {
                var remaining = TimeSpan.FromMilliseconds(_config.FaceTrackingAutoFix.CooldownMs) - (now - lastAttempt);
                if (remaining > TimeSpan.Zero)
                    return $"next stalled-connection fix in {remaining.TotalSeconds:F0}s";
            }
        }

        if (_last.ModuleConnectedToSRanipal) return null; // healthy — main status line already says so

        // VrcFaceTrackingLifecycleManager already explains "VRCFaceTracking isn't running" (it owns
        // that process's start/stop lifecycle) — don't restate it here, just what's missing once
        // VRCFaceTracking is actually up.
        if (!_last.VrcFaceTrackingRunning) return null;

        var missing = new List<string>();
        if (!_last.SRanipalRunning) missing.Add("sr_runtime");
        else if (_last.ModuleProcessCount == 0) missing.Add("a loaded tracking module");
        if (!_last.ViveCameraDevicePresent) missing.Add("the Vive tracker");

        return missing.Count > 0
            ? $"waiting for {string.Join(", ", missing)} before it can watch for a stalled connection"
            : null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
