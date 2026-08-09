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
    private readonly IHeadsetMonitor _headset;
    private readonly Func<bool> _viveTrackerPresent;
    private readonly IProcessLauncher _launcher;
    private readonly PresenceLifecycleMachine _machine;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public VirtualHereSRanipalLifecycleManager(
        MonitorConfig config,
        IHeadsetMonitor headset,
        Func<bool> viveTrackerPresent,
        IProcessLauncher? launcher = null,
        Func<DateTime>? clock = null)
    {
        _config = config;
        _headset = headset;
        _viveTrackerPresent = viveTrackerPresent;
        _launcher = launcher ?? new ProcessLauncher();

        _machine = new PresenceLifecycleMachine(
            presenceSignal: () => _headset.IsOnline || _viveTrackerPresent(),
            isRunning: () => _launcher.IsRunning("vhui64") || _launcher.IsRunning("sr_runtime"),
            ensureRunning: EnsureBothRunningAsync,
            shutdown: () =>
            {
                Log.Warn("VhSranipalLifecycle", "Idle with no headset/Vive tracker — shutting down vhui64.exe/sr_runtime.exe.");
                _launcher.Kill("vhui64");
                _launcher.Kill("sr_runtime");
            },
            shutdownDelayMs: _config.VirtualHereSRanipalLifecycle.ShutdownDelayMs,
            clock: clock,
            // Re-ensure BOTH processes every Running tick. isRunning is an OR over vhui64/sr_runtime,
            // so if only sr_runtime dies the OR stays true and the machine's own "not running ->
            // ensure" branch never fires — EnsureBothRunningAsync is per-process guarded and
            // relaunches only whichever one is actually missing, restoring independent crash-recovery.
            runningTick: EnsureBothRunningAsync,
            onTransition: (from, to) => Log.Info("VhSranipalLifecycle", $"Presence state {from} -> {to}."));
    }

    /// <summary>Current presence-machine state. Internal, for test observability only.</summary>
    internal PresenceState State => _machine.State;

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
            try { await TickForTestAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("VhSranipalLifecycle", $"Check cycle threw: {ex.Message}"); }

            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>One poll cycle. Named for test access; the loop and tests both call it.</summary>
    internal async Task TickForTestAsync()
    {
        if (!_config.VirtualHereSRanipalLifecycle.Enabled) return;
        await _machine.TickAsync().ConfigureAwait(false);
    }

    private async Task EnsureBothRunningAsync()
    {
        if (!_launcher.IsRunning("vhui64"))
        {
            Log.Info("VhSranipalLifecycle", "vhui64.exe not running — launching it.");
            var r = await _launcher.EnsureRunningAsync(
                "vhui64", _config.Paths.VirtualHereClientExe, null,
                _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
            if (!r.Success && !r.AlreadyRunning)
                Log.Warn("VhSranipalLifecycle", $"vhui64.exe launch did not confirm success: {r.Error}");
        }

        if (!_launcher.IsRunning("sr_runtime"))
        {
            Log.Info("VhSranipalLifecycle", "sr_runtime.exe not running — launching it.");
            // suppressUacPrompt: sr_runtime.exe manifests requestedExecutionLevel highestAvailable,
            // which pops a UAC prompt on every launch on an admin account — fatal for unattended
            // auto-launch (confirmed live 2026-07-16). See ProcessLauncher's __COMPAT_LAYER note.
            var r = await _launcher.EnsureRunningAsync(
                "sr_runtime", _config.Paths.SRanipalExe, null,
                _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs,
                suppressUacPrompt: true).ConfigureAwait(false);
            if (!r.Success && !r.AlreadyRunning)
                Log.Warn("VhSranipalLifecycle", $"sr_runtime.exe launch did not confirm success: {r.Error}");
        }
    }

    public string? DescribePendingAction()
    {
        if (!_config.VirtualHereSRanipalLifecycle.Enabled) return null;
        var remaining = _machine.ShutdownCountdownRemaining();
        return remaining is TimeSpan t && t > TimeSpan.Zero
            ? $"vhui64/sr_runtime shutdown in {t.TotalSeconds:F0}s"
            : null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
