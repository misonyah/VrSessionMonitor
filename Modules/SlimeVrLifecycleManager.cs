using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Stops SlimeVR (slimevr.exe + its detached java child) once you're done with VR — SteamVR not
/// running AND headset offline AND the trackers motionless (see SlimeVrMotionMonitor) — held for
/// SlimeVrLifecycleConfig.ShutdownDelayMs. Built on the same PresenceLifecycleMachine as the
/// Vh/VRCFaceTracking managers, but this one ONLY stops: ensureRunning is a no-op, since launching
/// SlimeVR stays owned by SessionOrchestrator.LaunchSlimeVrAsync. Presence (keep-running) is true
/// while SteamVR is up OR the headset is online OR the trackers are still moving.
/// </summary>
public sealed class SlimeVrLifecycleManager : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly IProcessLauncher _launcher;
    private readonly PresenceLifecycleMachine _machine;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public SlimeVrLifecycleManager(
        MonitorConfig config,
        Func<bool> steamVrRunning,
        Func<bool> headsetOnline,
        Func<bool> trackersIdle,
        IProcessLauncher? launcher = null,
        Func<DateTime>? clock = null)
    {
        _config = config;
        _launcher = launcher ?? new ProcessLauncher();

        _machine = new PresenceLifecycleMachine(
            presenceSignal: () => steamVrRunning() || headsetOnline() || !trackersIdle(),
            isRunning: () => _launcher.IsRunning("SlimeVR"),
            ensureRunning: () => Task.CompletedTask, // only stops; never launches SlimeVR
            shutdown: () =>
            {
                Log.Warn("SlimeVrLifecycle", "VR off (SteamVR + headset) and trackers idle — stopping SlimeVR.");
                _launcher.Kill("SlimeVR");
                _launcher.KillOrphanedChildIfLauncherGone("SlimeVR", _config.LaunchTargetFor("slimevr", _config.Paths.SlimeVrExe), "java");
            },
            shutdownDelayMs: _config.SlimeVrLifecycle.ShutdownDelayMs,
            clock: clock,
            onTransition: (from, to) => Log.Info("SlimeVrLifecycle", $"Presence state {from} -> {to}."));
    }

    internal PresenceState State => _machine.State;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("SlimeVrLifecycle", "Started SlimeVR auto-stop management.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("SlimeVrLifecycle", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await TickForTestAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("SlimeVrLifecycle", $"Check cycle threw: {ex.Message}"); }

            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    internal async Task TickForTestAsync()
    {
        if (!(_config.GetApp("slimevr")?.Enabled ?? _config.SlimeVrLifecycle.Enabled)) return;
        await _machine.TickAsync().ConfigureAwait(false);
    }

    public string? DescribePendingAction()
    {
        if (!(_config.GetApp("slimevr")?.Enabled ?? _config.SlimeVrLifecycle.Enabled)) return null;
        var remaining = _machine.ShutdownCountdownRemaining();
        return remaining is TimeSpan t && t > TimeSpan.Zero
            ? $"SlimeVR stop in {t.TotalSeconds:F0}s"
            : null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
