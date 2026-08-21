using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Stateless;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public enum SessionState { Idle, HeadsetDetected, PreflightChecks, LaunchingApps, WaitingForStream, LaunchingVrChat, LaunchingSlimeVr, LaunchingVrOverlay, Complete, Failed }

public enum SessionTrigger
{
    StartSession, BeginPreflight, PreflightDone, VdLaunched,
    PingFlapDetected, AwaitStream, StreamConfirmed, StreamTimedOut,
    VrChatPhase, SlimeVrPhase, VrOverlayPhase, ChainComplete, Fault
}

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

    private readonly StateMachine<SessionState, SessionTrigger> _sm = BuildStateMachine();
    private readonly IProcessLauncher _launcher;
    private readonly Func<DateTime> _clock;
    private readonly Func<TimeSpan, Task<bool>>? _streamWaiterOverride;
    private readonly Func<Task>? _preflightOverride;
    private readonly Action<string> _launchUri;

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

    /// <summary>When the headset was last seen going offline — used to tell a genuine short ping
    /// flap apart from a real new session when deciding whether _launchChainCompletedForVdPid's
    /// PID match still applies. See MinHeadsetOfflineDurationForNewSessionMs.</summary>
    private DateTime? _headsetWentOfflineAtUtc;

    public SessionState State => _sm.State;
    public event EventHandler<SessionState>? StateChanged;
    /// <summary>Fires once per RunPreflightChecksAsync with every UpdateChecker finding (not just
    /// the actionable ones) — consumers decide what's worth surfacing (see TrayApplicationContext,
    /// which balloon-notifies only for PossiblyOutdated results).</summary>
    public event EventHandler<List<UpdateFinding>>? UpdateFindingsAvailable;

    public SessionOrchestrator(
        MonitorConfig config, SlimeVrTrackerMonitor trackers, UpdateChecker updateChecker, AdbController adb,
        IProcessLauncher? launcher = null, Func<DateTime>? clock = null,
        Func<TimeSpan, Task<bool>>? streamWaiter = null, Func<Task>? preflightOverride = null,
        Action<string>? launchUri = null)
    {
        _config = config;
        _trackers = trackers;
        _updateChecker = updateChecker;
        _adb = adb;
        _launcher = launcher ?? new ProcessLauncher();
        _clock = clock ?? (() => DateTime.UtcNow);
        _streamWaiterOverride = streamWaiter;
        _preflightOverride = preflightOverride;
        _launchUri = launchUri ?? (uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }));

        _sm.OnTransitioned(t =>
        {
            Log.Info("Orchestrator", $"State -> {t.Destination}");
            StateChanged?.Invoke(this, t.Destination);
        });
    }

    private async Task FireAsync(SessionTrigger trigger) => await _sm.FireAsync(trigger).ConfigureAwait(false);

    /// <summary>
    /// The session-start transition graph as a pure Stateless definition — no side effects, so it
    /// can be unit-tested directly and reused by the driver. The launch WORK stays in
    /// RunSessionStartAsync; this only encodes which transitions are legal. Illegal transitions
    /// throw when fired, which is the point: the old linear SetState() walk couldn't catch them.
    /// The two historically bug-prone branches are the LaunchingApps fork (PingFlapDetected vs
    /// AwaitStream) and the WaitingForStream fork (StreamConfirmed vs StreamTimedOut).
    /// </summary>
    internal static StateMachine<SessionState, SessionTrigger> BuildStateMachine(SessionState initial = SessionState.Idle)
    {
        var sm = new StateMachine<SessionState, SessionTrigger>(initial);

        sm.Configure(SessionState.Idle)
            .Permit(SessionTrigger.StartSession, SessionState.HeadsetDetected);

        sm.Configure(SessionState.HeadsetDetected)
            .Permit(SessionTrigger.BeginPreflight, SessionState.PreflightChecks)
            .Permit(SessionTrigger.Fault, SessionState.Failed);

        sm.Configure(SessionState.PreflightChecks)
            .Permit(SessionTrigger.PreflightDone, SessionState.LaunchingApps)
            .Permit(SessionTrigger.Fault, SessionState.Failed);

        sm.Configure(SessionState.LaunchingApps)
            .PermitReentry(SessionTrigger.PreflightDone)   // harmless if re-entered; keeps graph total
            .Permit(SessionTrigger.PingFlapDetected, SessionState.Complete)
            .Permit(SessionTrigger.AwaitStream, SessionState.WaitingForStream)
            .Permit(SessionTrigger.VrChatPhase, SessionState.LaunchingVrChat)
            .Permit(SessionTrigger.Fault, SessionState.Failed);

        sm.Configure(SessionState.WaitingForStream)
            .Permit(SessionTrigger.StreamConfirmed, SessionState.LaunchingApps)
            .Permit(SessionTrigger.StreamTimedOut, SessionState.Complete)
            .Permit(SessionTrigger.Fault, SessionState.Failed);

        sm.Configure(SessionState.LaunchingVrChat)
            .Permit(SessionTrigger.SlimeVrPhase, SessionState.LaunchingSlimeVr)
            .Permit(SessionTrigger.Fault, SessionState.Failed);

        sm.Configure(SessionState.LaunchingSlimeVr)
            .Permit(SessionTrigger.VrOverlayPhase, SessionState.LaunchingVrOverlay)
            .Permit(SessionTrigger.Fault, SessionState.Failed);

        sm.Configure(SessionState.LaunchingVrOverlay)
            .Permit(SessionTrigger.ChainComplete, SessionState.Complete)
            .Permit(SessionTrigger.Fault, SessionState.Failed);

        sm.Configure(SessionState.Complete)
            .Permit(SessionTrigger.StartSession, SessionState.HeadsetDetected);

        sm.Configure(SessionState.Failed)
            .Permit(SessionTrigger.StartSession, SessionState.HeadsetDetected);

        return sm;
    }

    public void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        if (e.IsOnline)
        {
            _ = RunSessionStartAsync(); // fire-and-forget; internal gate prevents overlap
        }
        else
        {
            _headsetWentOfflineAtUtc = _clock();
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
            await FireAsync(SessionTrigger.StartSession);   // -> HeadsetDetected
            Log.Info("Orchestrator", "=== Headset online — beginning session-start flow ===");

            await FireAsync(SessionTrigger.BeginPreflight); // -> PreflightChecks
            await (_preflightOverride?.Invoke() ?? RunPreflightChecksAsync()).ConfigureAwait(false);

            await FireAsync(SessionTrigger.PreflightDone);  // -> LaunchingApps
            await LaunchVdStreamerAsync().ConfigureAwait(false);

            var vdPid = _launcher.GetProcessId("VirtualDesktop.Streamer");

            var offlineDuration = _headsetWentOfflineAtUtc.HasValue ? _clock() - _headsetWentOfflineAtUtc.Value : (TimeSpan?)null;
            var wasBriefFlap = offlineDuration.HasValue &&
                               offlineDuration.Value.TotalMilliseconds < _config.SessionFlow.MinHeadsetOfflineDurationForNewSessionMs;

            // Adopt an already-in-progress session instead of re-running the launch chain. A freshly
            // (re)started monitor has no memory of a launch chain a PREVIOUS process completed
            // (_launchChainCompletedForVdPid is null), so without this a monitor restart mid-session —
            // or right after you've closed VRChat/SteamVR while the headset stays connected — would
            // re-fire the whole chain and relaunch everything you just closed (the real 1am churn).
            // HeadsetMonitor always assumes offline at startup, so its first "online" event looks
            // identical whether the headset was already up when the monitor started or just connected;
            // this state check is what tells them apart. "Session already in progress" = VRChat is
            // running, or a VD stream from the headset is already established (that stream survives
            // closing VRChat/SteamVR, which is exactly the churn case). For a genuine new session
            // neither is true yet at this first launch attempt — the headset is merely pingable; the
            // stream comes up later, during the wait below — so this never suppresses a real launch.
            // Only evaluated on the first session-start; after that the ping-flap guard owns re-triggers.
            var adoptInProgressSession = false;
            if (_launchChainCompletedForVdPid is null)
            {
                var vrChatUp = _launcher.IsRunning("VRChat");
                var streamUp = IsHeadsetStreamEstablished();
                adoptInProgressSession = vrChatUp || streamUp;
                if (adoptInProgressSession)
                    Log.Info("Orchestrator", $"A VR session is already in progress at startup (VRChat running={vrChatUp}, VD stream established={streamUp}) — adopting it instead of re-running the launch chain, since this monitor just (re)started into an existing session.");
            }

            if (adoptInProgressSession)
            {
                _launchChainCompletedForVdPid = vdPid;
                await FireAsync(SessionTrigger.PingFlapDetected); // -> Complete (skip launches)
            }
            else if (vdPid.HasValue && vdPid == _launchChainCompletedForVdPid && wasBriefFlap)
            {
                Log.Info("Orchestrator", $"Launch chain already completed for this VD Streamer instance (PID {vdPid}) and the headset was only offline for " +
                                          $"{offlineDuration!.Value.TotalSeconds:F0}s — this is a headset ping flap, not a new VD session. Skipping Steam/VRChat/SlimeVR/OVR Toolkit relaunch.");
                await FireAsync(SessionTrigger.PingFlapDetected); // -> Complete
            }
            else
            {
                if (vdPid.HasValue && vdPid == _launchChainCompletedForVdPid)
                    Log.Info("Orchestrator", $"VD Streamer PID {vdPid} is unchanged, but the headset was offline for " +
                                              $"{(offlineDuration.HasValue ? $"{offlineDuration.Value.TotalMinutes:F0}m" : "an unknown duration")} " +
                                              $"(>= {_config.SessionFlow.MinHeadsetOfflineDurationForNewSessionMs}ms threshold) — treating as a genuine new session, not a ping flap.");

                bool proceed;
                if (!_config.SessionFlow.RequireVdStream)
                {
                    // SteamVR-direct: no Virtual Desktop stream will ever appear, so don't wait for
                    // one — the headset being reachable is enough. Stay in LaunchingApps and go
                    // straight to the launch phases (LaunchingApps already permits VrChatPhase), so
                    // the overlay + VRChat actually launch instead of timing out on a stream that
                    // never comes.
                    Log.Info("Orchestrator", "RequireVdStream is off (SteamVR-direct) — skipping the VD-stream wait and launching now.");
                    proceed = true;
                }
                else
                {
                    await FireAsync(SessionTrigger.AwaitStream);      // -> WaitingForStream
                    var streaming = await (_streamWaiterOverride?.Invoke(TimeSpan.FromSeconds(60))
                                           ?? WaitForHeadsetStreamAsync(TimeSpan.FromSeconds(60))).ConfigureAwait(false);

                    if (!streaming)
                    {
                        Log.Warn("Orchestrator", "Timed out waiting for a confirmed VD stream connection from the headset — " +
                                                  "skipping Steam/VRChat/SlimeVR launch. Only VD Streamer itself was started, so it's ready to accept a connection whenever you actually open Virtual Desktop. (If you run SteamVR directly without Virtual Desktop, turn off SessionFlow.RequireVdStream.)");
                        await FireAsync(SessionTrigger.StreamTimedOut); // -> Complete
                        proceed = false;
                    }
                    else
                    {
                        await FireAsync(SessionTrigger.StreamConfirmed); // -> LaunchingApps (Steam)
                        proceed = true;
                    }
                }

                if (proceed)
                {
                    await LaunchSteamAsync().ConfigureAwait(false);

                    await FireAsync(SessionTrigger.VrChatPhase);     // -> LaunchingVrChat
                    await LaunchVrChatAsync().ConfigureAwait(false);

                    await FireAsync(SessionTrigger.SlimeVrPhase);    // -> LaunchingSlimeVr
                    await LaunchSlimeVrAsync().ConfigureAwait(false);

                    await FireAsync(SessionTrigger.VrOverlayPhase);  // -> LaunchingVrOverlay
                    await LaunchVrOverlayAsync().ConfigureAwait(false);

                    _launchChainCompletedForVdPid = vdPid;
                    await FireAsync(SessionTrigger.ChainComplete);   // -> Complete
                }
            }

            Log.Info("Orchestrator", "=== Session-start flow complete ===");
        }
        catch (Exception ex)
        {
            if (_sm.CanFire(SessionTrigger.Fault)) await FireAsync(SessionTrigger.Fault);
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
            var findings = await _updateChecker.RunAllAsync().ConfigureAwait(false);
            UpdateFindingsAvailable?.Invoke(this, findings);
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
    /// <summary>One-shot: is there an ESTABLISHED TCP connection on the VD port right now whose
    /// remote address is the headset's own LAN IP? Used both by the wait loop below and by the
    /// "adopt an already-in-progress session" check in RunSessionStartAsync. Never throws — a
    /// failure to enumerate connections just reads as "no stream".</summary>
    private bool IsHeadsetStreamEstablished()
    {
        try
        {
            var headsetIp = IPAddress.Parse(_config.Network.HeadsetIp);
            var props = IPGlobalProperties.GetIPGlobalProperties();
            return props.GetActiveTcpConnections().Any(c =>
                c.LocalEndPoint.Port == _config.Network.VirtualDesktopPort &&
                c.State == TcpState.Established &&
                c.RemoteEndPoint.Address.Equals(headsetIp));
        }
        catch (Exception ex)
        {
            Log.Debug("Orchestrator", $"IsHeadsetStreamEstablished check threw: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> WaitForHeadsetStreamAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (IsHeadsetStreamEstablished())
            {
                Log.Info("Orchestrator", $"Confirmed VD stream from {_config.Network.HeadsetIp} (ESTABLISHED).");
                return true;
            }

            Log.Trace("Orchestrator", $"No established VD connection from {_config.Network.HeadsetIp} yet, waiting...");
            await Task.Delay(1000).ConfigureAwait(false);
        }

        return false;
    }

    private async Task LaunchVrChatAsync()
    {
        if (!(_config.GetApp("vrchat")?.Enabled ?? _config.SessionFlow.AutoLaunchVrChat))
        {
            Log.Info("Orchestrator", "Auto-start VRChat is disabled via the tray toggle — skipping.");
            return;
        }

        await DoLaunchVrChatAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Manual restart path for the tray menu — bypasses the AutoLaunchVrChat toggle (a manual
    /// click is an explicit request, not the automatic flow that toggle governs) and always
    /// re-evaluates the CURRENT config's background-mode setting. Added after a live incident
    /// where a manual relaunch was hand-typed with the wrong (non-background) args, overriding
    /// the user's actual configured preference and popping an unwanted maximized window.
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

    /// <summary>
    /// VRChat now launches via steam://rungameid (see PathsConfig.VrChatSteamAppId's doc for why —
    /// same Steam Input binding-activation bug already fixed for OVR Toolkit). VD Streamer no
    /// longer wraps VRChat's launcher to thread args through; confirmed live 2026-08-09 that VD's
    /// streaming only needs VD Streamer running in the background, not to be VRChat's parent
    /// process, so it's launched standalone and independently of VRChat here. The background-mode
    /// launch args this used to pass dynamically must now be set once as VRChat's static Steam
    /// "Launch Options" instead, since steam:// takes no arguments.
    /// </summary>
    private async Task DoLaunchVrChatAsync()
    {
        await _launcher.EnsureRunningAsync(
            "VirtualDesktop.Streamer", _config.Paths.VirtualDesktopStreamerExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);

        if (_launcher.IsRunning("VRChat"))
        {
            Log.Debug("Orchestrator", "VRChat already running, skipping launch.");
            return;
        }

        Log.Info("Orchestrator", $"Launching VRChat via steam://rungameid/{_config.Paths.VrChatSteamAppId}.");
        try
        {
            _launchUri($"steam://rungameid/{_config.Paths.VrChatSteamAppId}");
        }
        catch (Exception ex)
        {
            Log.Error("Orchestrator", "Failed to launch VRChat via the steam:// protocol", ex);
            return;
        }

        var elapsed = 0;
        while (elapsed < _config.Polling.ProcessLaunchTimeoutMs)
        {
            await Task.Delay(_config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
            elapsed += _config.Polling.ProcessPollIntervalMs;

            if (_launcher.IsRunning("VRChat"))
            {
                Log.Info("Orchestrator", $"VRChat confirmed running after {elapsed}ms.");
                if (_launcher.GetProcessId("VRChat") is int vrchatPid)
                    _launcher.Provenance.RecordLaunched("VRChat", vrchatPid);
                return;
            }
        }

        Log.Warn("Orchestrator", $"VRChat did not appear in the process list within {_config.Polling.ProcessLaunchTimeoutMs}ms after the steam:// launch.");
    }

    private async Task LaunchSlimeVrAsync()
    {
        var delayMs = _config.SessionFlow.SlimeVrLaunchDelayMs;
        if (delayMs > 0)
        {
            Log.Debug("Orchestrator", $"Waiting {delayMs}ms before checking SlimeVR — gives SteamVR's own driver-triggered auto-launch a chance to land first.");
            await Task.Delay(delayMs).ConfigureAwait(false);
        }

        _launcher.KillOrphanedChildIfLauncherGone("SlimeVR", _config.Paths.SlimeVrExe, "java");

        await _launcher.EnsureRunningAsync(
            "SlimeVR", _config.Paths.SlimeVrExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
    }

    /// <summary>
    /// Launches the configured VR overlay (SessionFlow.VrOverlay) via steam://rungameid — the same
    /// elevation-safe path OVR Toolkit always needed (see PathsConfig.OvrToolkitSteamAppId's doc).
    /// None = skip. Fire-and-forget: Steam handles a not-installed app itself, so this only logs an
    /// informational "Launching …". Notifications are unaffected (OpenVR IVRNotifications).
    ///
    /// Deliberately does NOT confirm-and-record into LaunchProvenance the way DoLaunchVrChatAsync
    /// does: unlike VRChat, OVR Toolkit/XSOverlay have no downstream logic that branches on
    /// Manual-vs-Managed (ManagedAppWindowService applies window rules to both alike), so a
    /// confirm-with-timeout loop here would only add launch latency for no observable behaviour
    /// change. Net effect: these two overlays always report AppStartOrigin.Manual in
    /// ManagedAppWindowService.CurrentOrigins even when this monitor launched them — acceptable
    /// today since nothing reads that origin for them, but worth revisiting if that changes.
    /// </summary>
    /// <summary>
    /// Resolves which overlay is actually selected per the Apps-tab model (GetApp), falling back
    /// to the legacy SessionFlow.VrOverlay enum only when neither "ovrtoolkit" nor "xsoverlay" has
    /// an entry yet. Mirrors the GetApp(id)?.Enabled ?? legacy pattern the lifecycle managers use,
    /// adapted for a mutually-exclusive pair instead of a single bool.
    /// </summary>
    private VrOverlayChoice ResolveVrOverlayChoice()
    {
        if (_config.GetApp("ovrtoolkit")?.Enabled == true) return VrOverlayChoice.OvrToolkit;
        if (_config.GetApp("xsoverlay")?.Enabled == true) return VrOverlayChoice.XSOverlay;
        return _config.SessionFlow.VrOverlay;
    }

    private Task LaunchVrOverlayAsync()
    {
        var (appId, processName, label) = ResolveVrOverlayChoice() switch
        {
            VrOverlayChoice.OvrToolkit => (_config.Paths.OvrToolkitSteamAppId, "OVR Toolkit", "OVR Toolkit"),
            VrOverlayChoice.XSOverlay  => (_config.Paths.XSOverlaySteamAppId, "XSOverlay", "XSOverlay"),
            _                          => (null, null, null),
        };

        if (appId is null)
        {
            Log.Debug("Orchestrator", "No VR overlay selected (VrOverlay=None) — skipping overlay launch.");
            return Task.CompletedTask;
        }

        if (_launcher.IsRunning(processName!))
        {
            Log.Debug("Orchestrator", $"{label} already running, skipping launch.");
            return Task.CompletedTask;
        }

        Log.Info("Orchestrator", $"Launching {label} via steam://rungameid/{appId}.");
        try
        {
            _launchUri($"steam://rungameid/{appId}");
        }
        catch (Exception ex)
        {
            Log.Error("Orchestrator", $"Failed to launch {label} via the steam:// protocol", ex);
        }

        return Task.CompletedTask;
    }
}
