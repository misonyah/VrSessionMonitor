using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public enum SessionState { Idle, HeadsetDetected, PreflightChecks, LaunchingApps, WaitingForStream, LaunchingVrChat, LaunchingSlimeVr, LaunchingOvrToolkit, Complete, Failed }

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

    /// <summary>
    /// PID of the VD Streamer instance the launch chain (Steam/VRChat/SlimeVR/OVR Toolkit) last
    /// completed for. Found necessary live 2026-07-24: a bare headset ping flap (Quest briefly
    /// dropping Wi-Fi for a few seconds, VD Streamer never touched) re-fires
    /// OnHeadsetStateChanged's offline-&gt;online edge and re-ran this whole flow — including
    /// relaunching VRChat — a few minutes after VRChat and SteamVR had both been closed on
    /// purpose, because VD Streamer keeps its stream connection to the headset alive the entire
    /// time regardless of whether anything is actually being worn. The launch chain should only
    /// re-run for a genuinely new VD Streamer instance (fresh PID), not for every ping flap of an
    /// already-running one.
    /// </summary>
    private int? _launchChainCompletedForVdPid;

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
            await LaunchVdStreamerAsync().ConfigureAwait(false);

            var vdPid = ProcessLauncher.GetProcessId("VirtualDesktop.Streamer");

            if (vdPid.HasValue && vdPid == _launchChainCompletedForVdPid)
            {
                Log.Info("Orchestrator", $"Launch chain already completed for this VD Streamer instance (PID {vdPid}) — " +
                                          "this is a headset ping flap, not a new VD session. Skipping Steam/VRChat/SlimeVR/OVR Toolkit relaunch.");
            }
            else
            {
                SetState(SessionState.WaitingForStream);
                var streaming = await WaitForHeadsetStreamAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

                if (!streaming)
                {
                    Log.Warn("Orchestrator", "Timed out waiting for a confirmed VD stream connection from the headset — " +
                                              "skipping Steam/VRChat/SlimeVR launch. Only VD Streamer itself was started, so it's ready to accept a connection whenever you actually open Virtual Desktop.");
                }
                else
                {
                    SetState(SessionState.LaunchingApps);
                    await LaunchSteamAsync().ConfigureAwait(false);

                    SetState(SessionState.LaunchingVrChat);
                    await LaunchVrChatAsync().ConfigureAwait(false);

                    SetState(SessionState.LaunchingSlimeVr);
                    await LaunchSlimeVrAsync().ConfigureAwait(false);

                    SetState(SessionState.LaunchingOvrToolkit);
                    await LaunchOvrToolkitAsync().ConfigureAwait(false);

                    _launchChainCompletedForVdPid = vdPid;
                }
            }

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

    /// <summary>
    /// VD Streamer has to be running before the headset can ever establish a stream to it, so
    /// this one launch stays eager (fired on mere ping-reachability, same as before). Everything
    /// downstream of it (Steam, VRChat, SlimeVR) now waits for WaitForHeadsetStreamAsync to
    /// confirm an actual connection first — see RunSessionStartAsync. Before this split, Steam
    /// launched eagerly here too, which on this machine auto-starts SteamVR; with a headset that
    /// merely responds to ping (powered on, not actually streaming) that meant SteamVR launching
    /// and erroring with no real HMD attached, confirmed live on 2026-07-20.
    /// </summary>
    private async Task LaunchVdStreamerAsync()
    {
        await _launcher.EnsureRunningAsync(
            "VirtualDesktop.Streamer", _config.Paths.VirtualDesktopStreamerExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
    }

    private async Task LaunchSteamAsync()
    {
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

        await DoLaunchVrChatAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Manual restart path for the tray menu — bypasses the AutoLaunchVrChat toggle (a manual
    /// click is an explicit request, not the automatic flow that toggle governs) and always
    /// builds launch args from the CURRENT config. Added after a live incident where a manual
    /// relaunch was hand-typed with the wrong (non-low-power) args, overriding the user's actual
    /// configured preference and popping an unwanted maximized window.
    /// </summary>
    public async Task RestartVrChatAsync()
    {
        Log.Info("Orchestrator", "Manual VRChat restart requested via tray menu.");
        foreach (var proc in Process.GetProcessesByName("VRChat"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log.Warn("Orchestrator", $"Killing existing VRChat process failed: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }

        await DoLaunchVrChatAsync().ConfigureAwait(false);
    }

    private async Task DoLaunchVrChatAsync()
    {
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
        var delayMs = _config.SessionFlow.SlimeVrLaunchDelayMs;
        if (delayMs > 0)
        {
            Log.Debug("Orchestrator", $"Waiting {delayMs}ms before checking SlimeVR — gives SteamVR's own driver-triggered auto-launch a chance to land first.");
            await Task.Delay(delayMs).ConfigureAwait(false);
        }

        await _launcher.EnsureRunningAsync(
            "SlimeVR", _config.Paths.SlimeVrExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
    }

    /// <summary>
    /// Launched via steam://rungameid/&lt;OvrToolkitSteamAppId&gt; rather than through
    /// ProcessLauncher (which requires the target to be a real file — a URL isn't one) or its exe
    /// path directly. Confirmed live 2026-07-22: launching "OVR Toolkit.exe" directly skips
    /// whatever elevation handshake Steam normally does for it, and it dies shortly after starting
    /// with "Process is not running as admin or has failed to get the right elevation level!"
    /// followed by its bridge process and WebSocket server failing. Going through Steam's own
    /// launch protocol (same mechanism already used for the SteamVR stuck-session restart in
    /// SteamVrMonitor.cs) avoided that entirely.
    /// </summary>
    private Task LaunchOvrToolkitAsync()
    {
        if (!_config.SessionFlow.AutoLaunchOvrToolkit) return Task.CompletedTask;

        if (ProcessLauncher.IsRunning("OVR Toolkit"))
        {
            Log.Debug("Orchestrator", "OVR Toolkit already running, skipping launch.");
            return Task.CompletedTask;
        }

        Log.Info("Orchestrator", $"Launching OVR Toolkit via steam://rungameid/{_config.Paths.OvrToolkitSteamAppId}.");
        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{_config.Paths.OvrToolkitSteamAppId}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Orchestrator", "Failed to launch OVR Toolkit via the steam:// protocol", ex);
        }

        return Task.CompletedTask;
    }
}
