using System.Diagnostics;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Owns vhui64.exe/sr_runtime.exe's start/stop lifecycle — mirrors VrcFaceTrackingLifecycleManager's
/// structure exactly, just with a different presence signal (see below). Added 2026-08-09 after
/// they were found running indefinitely with no headset connected and no VR session active,
/// simply because the previous unconditional crash-recovery in FaceTrackingMonitor only ever
/// asked "is it running?", never "should it be?".
///
/// Presence signal is headset-online (from HeadsetMonitor) OR the Vive Facial Tracker device
/// already being actively present (from FaceTrackingMonitor). Headset-online covers the ordinary
/// "not using VR right now" case directly. The Vive-device fallback exists so an already-active
/// desktop-mode face-tracking session (headset off, tracker still plugged in and in use) doesn't
/// get interrupted — it's only consulted to decide whether to KEEP running once already up, never
/// to decide whether to initially launch (vhui64.exe is the prerequisite that makes the device
/// ever appear at all, so launching based on its presence would be circular — same reasoning
/// FaceTrackingMonitor's old crash-recovery doc used to document, still true here).
///
/// This also folds in what used to be separate crash-recovery: if either process dies while
/// shouldBeRunning is true, the very next cycle's "not running -> launch" branch brings it back,
/// same as VrcFaceTrackingLifecycleManager already does for VRCFaceTracking.
/// </summary>
public sealed class VirtualHereSRanipalLifecycleManager : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly HeadsetMonitor _headset;
    private readonly FaceTrackingMonitor _faceTracking;
    private readonly ProcessLauncher _launcher = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private DateTime? _idleSinceUtc;
    private bool _lastShouldBeRunning;

    public VirtualHereSRanipalLifecycleManager(MonitorConfig config, HeadsetMonitor headset, FaceTrackingMonitor faceTracking)
    {
        _config = config;
        _headset = headset;
        _faceTracking = faceTracking;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("VhSranipalLifecycle", "Started vhui64/sr_runtime presence-based start/stop management.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("VhSranipalLifecycle", "Stopped.");
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
                Log.Debug("VhSranipalLifecycle", $"Check cycle threw: {ex.Message}");
            }

            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task CheckOnceAsync()
    {
        if (!_config.VirtualHereSRanipalLifecycle.Enabled) return;

        var headsetOnline = _headset.IsOnline;
        var viveTrackerPresent = _faceTracking.Current.ViveCameraDevicePresent;
        var shouldBeRunning = headsetOnline || viveTrackerPresent;

        if (shouldBeRunning != _lastShouldBeRunning)
        {
            Log.Info("VhSranipalLifecycle", $"Presence changed: headsetOnline={headsetOnline} viveTracker={viveTrackerPresent} -> shouldBeRunning={shouldBeRunning}.");
            _lastShouldBeRunning = shouldBeRunning;
        }

        if (shouldBeRunning)
        {
            _idleSinceUtc = null;

            if (!ProcessLauncher.IsRunning("vhui64"))
            {
                Log.Info("VhSranipalLifecycle", "vhui64.exe not running — launching it.");
                var result = await _launcher.EnsureRunningAsync(
                    "vhui64", _config.Paths.VirtualHereClientExe, null,
                    _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
                if (!result.Success && !result.AlreadyRunning)
                    Log.Warn("VhSranipalLifecycle", $"vhui64.exe launch did not confirm success: {result.Error}");
            }

            if (!ProcessLauncher.IsRunning("sr_runtime"))
            {
                Log.Info("VhSranipalLifecycle", "sr_runtime.exe not running — launching it.");
                // suppressUacPrompt: sr_runtime.exe's manifest requests requestedExecutionLevel
                // "highestAvailable", which triggers a UAC consent prompt on every launch on an
                // admin-capable account — fine for a human, fatal for this unattended auto-launch
                // (confirmed live 2026-07-16: nothing there to click "Yes", launch just hangs).
                var result = await _launcher.EnsureRunningAsync(
                    "sr_runtime", _config.Paths.SRanipalExe, null,
                    _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs,
                    suppressUacPrompt: true).ConfigureAwait(false);
                if (!result.Success && !result.AlreadyRunning)
                    Log.Warn("VhSranipalLifecycle", $"sr_runtime.exe launch did not confirm success: {result.Error}");
            }

            return;
        }

        // Neither signal present.
        if (!ProcessLauncher.IsRunning("vhui64") && !ProcessLauncher.IsRunning("sr_runtime"))
        {
            _idleSinceUtc = null; // nothing running, nothing to shut down
            return;
        }

        var now = DateTime.UtcNow;
        _idleSinceUtc ??= now;

        var elapsed = now - _idleSinceUtc.Value;
        var threshold = TimeSpan.FromMilliseconds(_config.VirtualHereSRanipalLifecycle.ShutdownDelayMs);
        if (elapsed < threshold) return;

        Log.Warn("VhSranipalLifecycle", $"No headset and no Vive tracker for {elapsed.TotalSeconds:F0}s — shutting down vhui64.exe/sr_runtime.exe.");
        KillIfRunning("vhui64");
        KillIfRunning("sr_runtime");

        _idleSinceUtc = null;
    }

    private static void KillIfRunning(string processName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(processName))
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
            Log.Error("VhSranipalLifecycle", $"Killing {processName}.exe threw", ex);
        }
    }

    /// <summary>Surfaces the shutdown-after-idle countdown for the tray — same reasoning as
    /// VrcFaceTrackingLifecycleManager.DescribePendingAction.</summary>
    public string? DescribePendingAction()
    {
        if (!_config.VirtualHereSRanipalLifecycle.Enabled) return null;

        if (_idleSinceUtc is DateTime since)
        {
            var remaining = TimeSpan.FromMilliseconds(_config.VirtualHereSRanipalLifecycle.ShutdownDelayMs) - (DateTime.UtcNow - since);
            if (remaining > TimeSpan.Zero)
                return $"vhui64/sr_runtime shutdown in {remaining.TotalSeconds:F0}s";
        }

        return null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
