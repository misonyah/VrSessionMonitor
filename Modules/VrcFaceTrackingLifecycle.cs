using System.Diagnostics;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Owns VRCFaceTracking.exe's start/stop lifecycle, coordinating across both EyeTrackingMonitor
/// and FaceTrackingMonitor (VRCFaceTracking is the shared downstream consumer for both pipelines
/// — Baballonia feeds it via UDP for eye tracking, SRanipal feeds it via TCP for face tracking).
///
/// Launches VRCFaceTracking the moment either an eye camera or the Vive Facial Tracker is
/// detected present, and shuts it down after a sustained period with NEITHER present — rather
/// than running unconditionally like everything else. This intentionally replaces plain
/// crash-recovery for VRCFaceTracking specifically (see FaceTrackingMonitor.HandleCrashRecoveryAsync,
/// which still unconditionally crash-recovers sr_runtime.exe and vhui64.exe, just not this one).
///
/// Also enforces VrcFaceTrackingLifecycleConfig.MaxContinuousUptimeMs — see that field's doc for
/// the full 2026-07-16 incident this exists to prevent: VRCFaceTracking.exe silently wedging its
/// OSC output after a long unbroken run, invisible to every other health signal this app has.
/// </summary>
public sealed class VrcFaceTrackingLifecycleManager : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly EyeTrackingMonitor _eyeTracking;
    private readonly FaceTrackingMonitor _faceTracking;
    private readonly ProcessLauncher _launcher = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private DateTime? _noTrackerSinceUtc;
    private bool _lastAnyTrackerPresent;
    private DateTime? _lastVrChatStartTime;

    public VrcFaceTrackingLifecycleManager(MonitorConfig config, EyeTrackingMonitor eyeTracking, FaceTrackingMonitor faceTracking)
    {
        _config = config;
        _eyeTracking = eyeTracking;
        _faceTracking = faceTracking;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("VrcFtLifecycle", "Started VRCFaceTracking presence-based start/stop management.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("VrcFtLifecycle", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("VrcFtLifecycle", $"Check cycle threw: {ex.Message}");
            }

            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task CheckOnceAsync()
    {
        if (!_config.VrcFaceTrackingLifecycle.Enabled) return;

        CheckVrChatRestart();

        var anyEyeCameraOnline = _eyeTracking.Current.Any(c => c.Online);
        var viveTrackerPresent = _faceTracking.Current.ViveCameraDevicePresent;
        var anyTrackerPresent = anyEyeCameraOnline || viveTrackerPresent;

        if (anyTrackerPresent != _lastAnyTrackerPresent)
        {
            Log.Info("VrcFtLifecycle", $"Tracker presence changed: eyeCamera={anyEyeCameraOnline} viveTracker={viveTrackerPresent} -> anyPresent={anyTrackerPresent}.");
            _lastAnyTrackerPresent = anyTrackerPresent;
        }

        if (anyTrackerPresent)
        {
            _noTrackerSinceUtc = null;

            if (!ProcessLauncher.IsRunning("VRCFaceTracking"))
            {
                Log.Info("VrcFtLifecycle", $"Tracker detected (eyeCamera={anyEyeCameraOnline}, viveTracker={viveTrackerPresent}) and VRCFaceTracking isn't running — launching it.");
                var result = await _launcher.EnsureRunningAsync(
                    "VRCFaceTracking", _config.Paths.VrcFaceTrackingExe, null,
                    _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);

                if (!result.Success && !result.AlreadyRunning)
                    Log.Warn("VrcFtLifecycle", $"VRCFaceTracking launch did not confirm success: {result.Error}");
            }
            else
            {
                MaybeRestartForMaxUptime();
            }

            return;
        }

        // No tracker present at all.
        if (!ProcessLauncher.IsRunning("VRCFaceTracking"))
        {
            _noTrackerSinceUtc = null; // nothing running, nothing to shut down
            return;
        }

        var now = DateTime.UtcNow;
        _noTrackerSinceUtc ??= now;

        var elapsed = now - _noTrackerSinceUtc.Value;
        var threshold = TimeSpan.FromMilliseconds(_config.VrcFaceTrackingLifecycle.ShutdownDelayMs);
        if (elapsed < threshold) return;

        Log.Warn("VrcFtLifecycle", $"No eye camera or Vive tracker detected for {elapsed.TotalSeconds:F0}s — shutting down VRCFaceTracking.exe.");
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
            Log.Error("VrcFtLifecycle", "Killing VRCFaceTracking.exe threw", ex);
        }

        _noTrackerSinceUtc = null;
    }

    /// <summary>
    /// Restarts VRCFaceTracking when VRChat has restarted more recently than VRCFaceTracking
    /// itself. Found live on 2026-07-21: VRCFaceTracking started, connected fine to SRanipal, and
    /// reported healthy on every signal this app checks — but VRChat then restarted (SteamVR had
    /// to be force-restarted that session) and face/eye tracking never resumed, because
    /// VRCFaceTracking's OSC/OSCQuery handshake was still pointed at the old, now-dead VRChat
    /// instance. A manual VRCFaceTracking restart fixed it. Compares OS-reported process start
    /// times (not our own bookkeeping) so this stays correct regardless of what started either
    /// process.
    /// </summary>
    private void CheckVrChatRestart()
    {
        Process? vrChatProc = null;
        try
        {
            vrChatProc = Process.GetProcessesByName("VRChat").FirstOrDefault();
            if (vrChatProc is null)
            {
                _lastVrChatStartTime = null;
                return;
            }

            var vrChatStart = vrChatProc.StartTime;
            var isNewVrChatInstance = _lastVrChatStartTime.HasValue && vrChatStart > _lastVrChatStartTime.Value;
            _lastVrChatStartTime = vrChatStart;
            if (!isNewVrChatInstance) return;

            using var vrcft = Process.GetProcessesByName("VRCFaceTracking").FirstOrDefault();
            if (vrcft is null || vrcft.StartTime >= vrChatStart) return;

            Log.Warn("VrcFtLifecycle", $"VRChat restarted (new instance at {vrChatStart:HH:mm:ss}) after VRCFaceTracking was already running (started {vrcft.StartTime:HH:mm:ss}) — its OSC handshake is likely stale against the new instance. Restarting it.");
            SteamVrNotifier.TryNotify(_config, "Restarting VRCFaceTracking (VRChat restarted)");

            foreach (var proc in Process.GetProcessesByName("VRCFaceTracking"))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Warn("VrcFtLifecycle", $"Killing VRCFaceTracking.exe for VRChat-restart refresh threw: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("VrcFtLifecycle", $"VRChat-restart check threw: {ex.Message}");
        }
        finally
        {
            vrChatProc?.Dispose();
        }
    }

    /// <summary>See VrcFaceTrackingLifecycleConfig.MaxContinuousUptimeMs for why this exists.
    /// Uses the OS-reported process start time (not our own bookkeeping) so it stays correct even
    /// if VRCFaceTracking was started outside this monitor or across a monitor restart. Only kills
    /// the process — deliberately does not relaunch here, since this only runs from the
    /// already-running branch of a cycle where anyTrackerPresent is true, so the very next cycle's
    /// "not running -> launch" branch above will bring it back fresh within one poll interval.</summary>
    private void MaybeRestartForMaxUptime()
    {
        var maxUptimeMs = _config.VrcFaceTrackingLifecycle.MaxContinuousUptimeMs;
        if (maxUptimeMs <= 0) return; // 0 = disabled

        Process? proc = null;
        try
        {
            proc = Process.GetProcessesByName("VRCFaceTracking").FirstOrDefault();
            if (proc is null) return;

            var uptime = DateTime.Now - proc.StartTime;
            if (uptime.TotalMilliseconds < maxUptimeMs) return;

            Log.Warn("VrcFtLifecycle", $"VRCFaceTracking.exe has been running continuously for {uptime.TotalHours:F1}h (limit {maxUptimeMs / 3600000.0:F1}h) — restarting it preventively before it can silently wedge its OSC output. It'll relaunch fresh on the next check since a tracker is still present.");
        }
        catch (Exception ex)
        {
            Log.Debug("VrcFtLifecycle", $"Checking VRCFaceTracking.exe uptime threw: {ex.Message}");
            return;
        }
        finally
        {
            proc?.Dispose();
        }

        SteamVrNotifier.TryNotify(_config, "Restarting VRCFaceTracking (preventive, long uptime)");
        try
        {
            foreach (var p in Process.GetProcessesByName("VRCFaceTracking"))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("VrcFtLifecycle", "Killing VRCFaceTracking.exe for preventive restart threw", ex);
        }
    }

    /// <summary>Surfaces the shutdown-after-no-tracker countdown and the preventive-restart-due
    /// countdown for the tray — previously the only way to know either was imminent was to already
    /// be watching the log, which is exactly what made the 2026-07-24 VRCFaceTracking pop-up look
    /// unprompted until the log was checked after the fact.</summary>
    public string? DescribePendingAction()
    {
        if (!_config.VrcFaceTrackingLifecycle.Enabled) return null;

        var now = DateTime.UtcNow;

        if (_noTrackerSinceUtc is DateTime since)
        {
            var remaining = TimeSpan.FromMilliseconds(_config.VrcFaceTrackingLifecycle.ShutdownDelayMs) - (now - since);
            if (remaining > TimeSpan.Zero)
                return $"VRCFaceTracking shutdown in {remaining.TotalSeconds:F0}s";
        }

        var maxUptimeMs = _config.VrcFaceTrackingLifecycle.MaxContinuousUptimeMs;
        if (maxUptimeMs > 0 && _lastAnyTrackerPresent)
        {
            using var proc = Process.GetProcessesByName("VRCFaceTracking").FirstOrDefault();
            if (proc is not null)
            {
                var remaining = TimeSpan.FromMilliseconds(maxUptimeMs) - (DateTime.Now - proc.StartTime);
                if (remaining > TimeSpan.Zero)
                    return $"preventive restart due in {FormatDuration(remaining)}";
            }
        }

        if (!_lastAnyTrackerPresent && !ProcessLauncher.IsRunning("VRCFaceTracking"))
            return "waiting for an eye camera or Vive tracker to launch VRCFaceTracking";

        return null;
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:F1}h" : $"{span.TotalMinutes:F0}m";

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
