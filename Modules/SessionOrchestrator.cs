using System.Net;
using System.Net.NetworkInformation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public enum SessionState { Idle, HeadsetDetected, PreflightChecks, LaunchingApps, WaitingForStream, LaunchingVrChat, LaunchingSlimeVr, Complete, Failed }

/// <summary>
/// Ties the individual monitors/launchers into the actual session-start sequence, replacing
/// vrc.cmd. Key differences from the old script, both driven by what live testing turned up on
/// 2026-07-15:
///  - VD-port "connected" detection now requires an ESTABLISHED connection specifically FROM the
///    headset's LAN IP, not just any connection on port 38830 (the old script matched VD
///    Streamer's outbound WAN connection to Virtual Desktop's own cloud service and would have
///    launched VRChat before the headset was actually streaming).
///  - Every launch goes through ProcessLauncher's mutex + wait-for-confirmation, eliminating the
///    double-launch race (confirmed responsible for SlimeVR starting twice and VRChat's
///    "All pipe instances are busy" error in the same test run).
/// </summary>
public sealed class SessionOrchestrator
{
    private readonly MonitorConfig _config;
    private readonly SlimeVrTrackerMonitor _trackers;
    private readonly UpdateChecker _updateChecker;
    private readonly AdbController _adb;
    private readonly ProcessLauncher _launcher = new();

    private readonly SemaphoreSlim _runGate = new(1, 1);

    public SessionState State { get; private set; } = SessionState.Idle;
    public event EventHandler<SessionState>? StateChanged;

    public SessionOrchestrator(MonitorConfig config, SlimeVrTrackerMonitor trackers, UpdateChecker updateChecker, AdbController adb)
    {
        _config = config;
        _trackers = trackers;
        _updateChecker = updateChecker;
        _adb = adb;
    }

    private void SetState(SessionState s)
    {
        State = s;
        Log.Info("Orchestrator", $"State -> {s}");
        StateChanged?.Invoke(this, s);
    }

    public void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        if (e.IsOnline)
        {
            _ = RunSessionStartAsync(); // fire-and-forget; internal gate prevents overlap
        }
        else
        {
            Log.Info("Orchestrator", "Headset went offline. Not auto-stopping anything — leaving session as-is.");
        }
    }

    public async Task RunSessionStartAsync()
    {
        if (!await _runGate.WaitAsync(0).ConfigureAwait(false))
        {
            Log.Debug("Orchestrator", "Session-start already in progress, ignoring re-trigger.");
            return;
        }

        try
        {
            SetState(SessionState.HeadsetDetected);
            Log.Info("Orchestrator", "=== Headset online — beginning session-start flow ===");

            SetState(SessionState.PreflightChecks);
            await RunPreflightChecksAsync().ConfigureAwait(false);

            SetState(SessionState.LaunchingApps);
            await LaunchCoreAppsAsync().ConfigureAwait(false);

            SetState(SessionState.WaitingForStream);
            var streaming = await WaitForHeadsetStreamAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (!streaming)
                Log.Warn("Orchestrator", "Timed out waiting for a confirmed VD stream connection from the headset. Continuing anyway — VRChat launch may be premature.");

            SetState(SessionState.LaunchingVrChat);
            await LaunchVrChatAsync().ConfigureAwait(false);

            SetState(SessionState.LaunchingSlimeVr);
            await LaunchSlimeVrAsync().ConfigureAwait(false);

            SetState(SessionState.Complete);
            Log.Info("Orchestrator", "=== Session-start flow complete ===");
        }
        catch (Exception ex)
        {
            SetState(SessionState.Failed);
            Log.Error("Orchestrator", "Session-start flow threw an unhandled exception", ex);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task RunPreflightChecksAsync()
    {
        Log.Info("Orchestrator", "Pre-flight: checking SlimeVR trackers (before server is even running)...");
        await _trackers.CheckAllAsync().ConfigureAwait(false);
        Log.Info("Orchestrator", $"Pre-flight tracker summary: {_trackers.Summarize()}");

        Log.Info("Orchestrator", "Pre-flight: running update checks (notify-only)...");
        try
        {
            await _updateChecker.RunAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Orchestrator", $"Update check phase threw, continuing anyway: {ex.Message}");
        }

        Log.Info("Orchestrator", "Pre-flight: attempting best-effort ADB connection to headset...");
        try
        {
            var connected = await _adb.TryConnectAsync().ConfigureAwait(false);
            if (connected)
            {
                await _adb.TryGetBatteryPercentAsync().ConfigureAwait(false);
                await _adb.TryLaunchVirtualDesktopAppAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Orchestrator", $"ADB phase threw, continuing anyway (non-fatal by design): {ex.Message}");
        }
    }

    private async Task LaunchCoreAppsAsync()
    {
        await _launcher.EnsureRunningAsync(
            "VirtualDesktop.Streamer", _config.Paths.VirtualDesktopStreamerExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);

        await _launcher.EnsureRunningAsync(
            "steam", _config.Paths.SteamExe, "-no-browser",
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for an ESTABLISHED TCP connection on the VD port whose REMOTE address is the
    /// headset's own LAN IP — not just any connection on that port. This is the fix for the
    /// false-positive found in vrc.cmd (it matched VD Streamer's outbound connection to Virtual
    /// Desktop's cloud service, remote IP was a public WAN address, not the headset).
    /// </summary>
    private async Task<bool> WaitForHeadsetStreamAsync(TimeSpan timeout)
    {
        var headsetIp = IPAddress.Parse(_config.Network.HeadsetIp);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var match = props.GetActiveTcpConnections().FirstOrDefault(c =>
                c.LocalEndPoint.Port == _config.Network.VirtualDesktopPort &&
                c.State == TcpState.Established &&
                c.RemoteEndPoint.Address.Equals(headsetIp));

            if (match is not null)
            {
                Log.Info("Orchestrator", $"Confirmed VD stream: {match.LocalEndPoint} <-> {match.RemoteEndPoint} (ESTABLISHED)");
                return true;
            }

            Log.Trace("Orchestrator", $"No established VD connection from {headsetIp} yet, waiting...");
            await Task.Delay(1000).ConfigureAwait(false);
        }

        return false;
    }

    private async Task LaunchVrChatAsync()
    {
        if (!_config.SessionFlow.AutoLaunchVrChat)
        {
            Log.Info("Orchestrator", "Auto-start VRChat is disabled via the tray toggle — skipping.");
            return;
        }

        if (ProcessLauncher.IsRunning("VRChat"))
        {
            Log.Debug("Orchestrator", "VRChat already running, skipping launch.");
            return;
        }

        // Matches the working pattern from vrc.cmd: VD Streamer wraps VRChat's own launcher.
        var sf = _config.SessionFlow;
        var args = sf.VrChatLowPowerMode
            ? $"\"{_config.Paths.VrChatLaunchExe}\" --watch-avatars -monitor {sf.VrChatLowPowerMonitor} -screen-width {sf.VrChatLowPowerWidth} -screen-height {sf.VrChatLowPowerHeight} -screen-fullscreen 0 --fps={sf.VrChatLowPowerFps} -force-d3d11-no-singlethreaded"
            : $"\"{_config.Paths.VrChatLaunchExe}\" --watch-avatars -monitor 2 -screen-width 1920 -screen-height 1080 -screen-fullscreen 1 --fps=120 -force-d3d11-no-singlethreaded";

        if (sf.VrChatLowPowerMode)
            Log.Info("Orchestrator", $"Launching VRChat in low-power windowed mode ({sf.VrChatLowPowerWidth}x{sf.VrChatLowPowerHeight}@{sf.VrChatLowPowerFps}fps, monitor {sf.VrChatLowPowerMonitor}).");

        var result = await _launcher.EnsureRunningAsync(
            "VRChat", _config.Paths.VirtualDesktopStreamerExe, args,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);

        if (!result.Success)
            Log.Warn("Orchestrator", $"VRChat launch did not confirm success: {result.Error}. " +
                                      "This mirrors a transient 'system cannot find the drive specified' error seen during testing — it may self-heal; check tasklist manually.");
    }

    private async Task LaunchSlimeVrAsync()
    {
        await _launcher.EnsureRunningAsync(
            "SlimeVR", _config.Paths.SlimeVrExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
    }
}
