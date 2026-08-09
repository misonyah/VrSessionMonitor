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
    private readonly Func<bool> _anyEyeCameraOnline;
    private readonly Func<bool> _viveTrackerPresent;
    private readonly IProcessLauncher _launcher;
    private readonly PresenceLifecycleMachine _machine;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private DateTime? _refreshedForVrChatStartTime;

    public VrcFaceTrackingLifecycleManager(
        MonitorConfig config,
        Func<bool> anyEyeCameraOnline,
        Func<bool> viveTrackerPresent,
        IProcessLauncher? launcher = null,
        Func<DateTime>? clock = null)
    {
        _config = config;
        _anyEyeCameraOnline = anyEyeCameraOnline;
        _viveTrackerPresent = viveTrackerPresent;
        _launcher = launcher ?? new ProcessLauncher();

        _machine = new PresenceLifecycleMachine(
            presenceSignal: () => _anyEyeCameraOnline() || _viveTrackerPresent(),
            isRunning: () => _launcher.IsRunning("VRCFaceTracking"),
            ensureRunning: EnsureRunningAsync,
            shutdown: () => _launcher.Kill("VRCFaceTracking"),
            shutdownDelayMs: _config.VrcFaceTrackingLifecycle.ShutdownDelayMs,
            clock: clock,
            runningTick: MaybeRestartForMaxUptime);
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
            try { await TickForTestAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("VrcFtLifecycle", $"Check cycle threw: {ex.Message}"); }

            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>One poll cycle. CheckVrChatRestart runs unconditionally every cycle (as before),
    /// then the presence machine advances. Named for test access.</summary>
    internal async Task TickForTestAsync()
    {
        if (!_config.VrcFaceTrackingLifecycle.Enabled) return;
        CheckVrChatRestart();
        await _machine.TickAsync().ConfigureAwait(false);
    }

    private async Task EnsureRunningAsync()
    {
        Log.Info("VrcFtLifecycle", "Tracker detected and VRCFaceTracking isn't running — launching it.");
        var r = await _launcher.EnsureRunningAsync(
            "VRCFaceTracking", _config.Paths.VrcFaceTrackingExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
        if (!r.Success && !r.AlreadyRunning)
            Log.Warn("VrcFtLifecycle", $"VRCFaceTracking launch did not confirm success: {r.Error}");
    }

    /// <summary>
    /// Restarts VRCFaceTracking whenever it predates the currently-running VRChat instance, so its
    /// OSC/OSCQuery handshake never ends up stale against (or simply never established with) the
    /// VRChat that's actually running. Originally written for the 2026-07-21 case (VRChat restarts
    /// while VRCFaceTracking is already running and healthy), but found live on 2026-07-25 to also
    /// matter for a second, more common case it didn't cover: VRCFaceTracking launched (via
    /// presence detection) while VRChat happened to be fully closed, and VRChat was reopened much
    /// later — same staleness, different trigger order.
    ///
    /// An earlier version tracked only the *previous poll's* VRChat start time and reset that
    /// tracking to null the instant VRChat wasn't running — which meant any restart detection was
    /// wiped out during the gap and never fired once VRChat came back, exactly the 07-25 case
    /// (VRChat closed for 18+ hours, ping flaps kept the tray "healthy", VRCFaceTracking launched
    /// partway through the gap, and the eventual VRChat restart was never flagged as new because
    /// there was no prior start time left to compare against). Tracking *which VRChat instance
    /// (by StartTime) has already been refreshed for* instead avoids that gap dependence entirely —
    /// every check directly compares VRCFaceTracking's start time against whatever VRChat instance
    /// is running right now, so it can't matter how long VRChat was absent beforehand.
    /// </summary>
    private void CheckVrChatRestart()
    {
        Process? vrChatProc = null;
        try
        {
            vrChatProc = Process.GetProcessesByName("VRChat").FirstOrDefault();
            if (vrChatProc is null) return; // nothing running to compare against right now

            var vrChatStart = vrChatProc.StartTime;
            if (_refreshedForVrChatStartTime == vrChatStart) return; // already handled this exact VRChat instance

            var vrChatUptime = DateTime.Now - vrChatStart;
            var minUptime = TimeSpan.FromMilliseconds(_config.VrcFaceTrackingLifecycle.MinVrChatUptimeBeforeRestartMs);
            if (vrChatUptime < minUptime) return; // give VRChat's own OSC service time to come up before forcing a handshake retry

            using var vrcft = Process.GetProcessesByName("VRCFaceTracking").FirstOrDefault();
            if (vrcft is null) return; // nothing running to be stale; leave unmarked so a later launch still gets checked

            if (vrcft.StartTime >= vrChatStart)
            {
                _refreshedForVrChatStartTime = vrChatStart; // VRCFaceTracking is fresher than this VRChat instance — nothing to do
                return;
            }

            Log.Warn("VrcFtLifecycle", $"VRCFaceTracking (started {vrcft.StartTime:HH:mm:ss}) predates the currently-running VRChat instance (started {vrChatStart:HH:mm:ss}) — its OSC handshake is likely stale or was never established against it. Restarting it.");
            SteamVrNotifier.TryNotify(_config, "Restarting VRCFaceTracking (stale against current VRChat instance)");

            _launcher.Kill("VRCFaceTracking");

            _refreshedForVrChatStartTime = vrChatStart;
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
            _launcher.Kill("VRCFaceTracking");
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

        var shutdown = _machine.ShutdownCountdownRemaining();
        if (shutdown is TimeSpan s && s > TimeSpan.Zero)
            return $"VRCFaceTracking shutdown in {s.TotalSeconds:F0}s";

        var maxUptimeMs = _config.VrcFaceTrackingLifecycle.MaxContinuousUptimeMs;
        if (maxUptimeMs > 0 && _machine.State == PresenceState.Running)
        {
            using var proc = Process.GetProcessesByName("VRCFaceTracking").FirstOrDefault();
            if (proc is not null)
            {
                var remaining = TimeSpan.FromMilliseconds(maxUptimeMs) - (DateTime.Now - proc.StartTime);
                if (remaining > TimeSpan.Zero)
                    return $"preventive restart due in {FormatDuration(remaining)}";
            }
        }

        if (_machine.State == PresenceState.Idle && !_launcher.IsRunning("VRCFaceTracking"))
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
