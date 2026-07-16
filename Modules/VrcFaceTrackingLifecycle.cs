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

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
