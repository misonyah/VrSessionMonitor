using System.Diagnostics;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Optimizations;
#if INCLUDE_HOME_ASSISTANT
using VrSessionMonitor.Modules.HomeAssistant;
#endif

namespace VrSessionMonitor.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly MonitorConfig _config;
    private readonly string _configPath;
    private readonly HeadsetMonitor _headset;
    private readonly SlimeVrTrackerMonitor _trackers;
    private readonly SteamVrMonitor _steamVr;
    private readonly VrChatMonitor _vrChat;
    private readonly FaceTrackingMonitor _faceTracking;
    private readonly EyeTrackingMonitor _eyeTracking;
    private readonly VrcFaceTrackingLifecycleManager _vrcFtLifecycle;
    private readonly VrcOscLifecycleManager _vrcOscLifecycle;
    private readonly VirtualHereSRanipalLifecycleManager _vhSranipalLifecycle;
    private readonly SlimeVrMotionMonitor _slimeMotion;
    private readonly SlimeVrLifecycleManager _slimeLifecycle;
    private readonly FirmwareNotificationListener _firmwareNotify;
    private readonly UpdateChecker _updateChecker;
    private readonly AdbController _adb;
    private readonly SessionOrchestrator _orchestrator;
    private readonly ManagedAppWindowService _managedApps;
    private readonly OptimizationsManager _optimizations;
    private readonly SettingsForm _settingsForm;
#if INCLUDE_HOME_ASSISTANT
    private readonly HomeAssistantClient _homeAssistantClient;
    private readonly HomeAssistantAreaDiscovery _homeAssistantDiscovery;
    private List<LightInfo> _homeAssistantLightsInSelectedArea = new();
    private HomeAssistantLightsManager? _homeAssistantManager;
    private HmdActivityMonitor? _hmdActivity;
    private VrChatOscAfkListener? _vrChatOscAfk;
    private readonly VrChatGroupAutomationMonitor _groupAutomation;

    internal bool HomeAssistantIsConnected => _homeAssistantClient.IsConnected;
#endif

    public TrayApplicationContext()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _config = MonitorConfig.LoadOrCreateDefault(_configPath);

        Log.Init(_config.Paths.LogDirectory);

        // Logged after a live incident where the running tray process was 4 days older than the
        // latest committed fix — it had never been rebuilt/restarted, so none of several days'
        // worth of fixes were actually active. Compare this against `git log` to catch that early
        // instead of having to check Get-Process/file mtimes by hand.
        var buildTime = File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location);
        Log.Info("Tray", $"Build timestamp: {buildTime:yyyy-MM-dd HH:mm:ss} (exe last written then — compare against `git log` if behavior seems stale)");

        Log.Info("Tray", $"Config loaded from {_configPath}");
        Log.Info("Tray", $"Headset target: {_config.Network.HeadsetIp} ({_config.Network.HeadsetName})");
        Log.Info("Tray", $"Trackers configured: {_config.Trackers.Count}");

        // Fire-and-forget: may pop a single UAC prompt on first run (or again if declined) — see
        // SRanipalServicePermissions' doc for why this is a one-time grant, not a recurring ask.
        _ = SRanipalServicePermissions.EnsureStartStopPermissionAsync(_config);

        _headset = new HeadsetMonitor(_config);
        _trackers = new SlimeVrTrackerMonitor(_config);
        _steamVr = new SteamVrMonitor(_config);
        _vrChat = new VrChatMonitor(_config);
        _faceTracking = new FaceTrackingMonitor(_config);
        _eyeTracking = new EyeTrackingMonitor(_config);
        _vrcFtLifecycle = new VrcFaceTrackingLifecycleManager(
            _config,
            () => _eyeTracking.Current.Any(c => c.Online),
            () => _faceTracking.Current.ViveCameraDevicePresent);
        _vrcOscLifecycle = new VrcOscLifecycleManager(_config, _vrChat);
        _vhSranipalLifecycle = new VirtualHereSRanipalLifecycleManager(
            _config, _headset, () => _faceTracking.Current.ViveCameraDevicePresent);
        _slimeMotion = new SlimeVrMotionMonitor(_config);
        _slimeLifecycle = new SlimeVrLifecycleManager(
            _config,
            () => _steamVr.Current.VrServerRunning,
            () => _headset.IsOnline,
            () => _slimeMotion.TrackersIdle);
        _firmwareNotify = new FirmwareNotificationListener(_config);
        _updateChecker = new UpdateChecker(_config);
        _adb = new AdbController(_config);
        _orchestrator = new SessionOrchestrator(_config, _trackers, _updateChecker, _adb);
        _managedApps = new ManagedAppWindowService(_config, new ProcessLauncher());
        var optimizationChecks = OptimizationRegistry.BuildAll(
            new RegistryAccessor(), new WindowsServiceController(),
            CpuInfo.GetName, PowercfgRunner.RunAsync, PowercfgRunner.RunElevatedAsync);
        _optimizations = new OptimizationsManager(_config, _configPath, optimizationChecks);
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantClient = new HomeAssistantClient(_config);
        _homeAssistantDiscovery = new HomeAssistantAreaDiscovery(_homeAssistantClient);
        _homeAssistantManager = new HomeAssistantLightsManager(_config, _homeAssistantClient, _headset);
        _hmdActivity = new HmdActivityMonitor(_config);
        _vrChatOscAfk = new VrChatOscAfkListener(_config);
        _groupAutomation = new VrChatGroupAutomationMonitor(_config);
#endif

        // Replaces the old tray ContextMenuStrip entirely (see git history around 2026-08-09) —
        // see SettingsForm's own doc comment for why. Created once and shown/hidden from here on,
        // never recreated.
        _settingsForm = new SettingsForm(this, _config, _configPath, _headset, _trackers, _steamVr,
            _vrChat, _faceTracking, _eyeTracking, _vrcFtLifecycle, _vrcOscLifecycle, _vhSranipalLifecycle, _slimeLifecycle, _firmwareNotify, _orchestrator, _optimizations, _managedApps);

        _notifyIcon = new NotifyIcon
        {
            // Pulls the icon straight off this exe's own embedded resource (set via
            // <ApplicationIcon>icon.ico</ApplicationIcon> in the csproj) rather than shipping a
            // second copy of the file — one source of truth for both the taskbar/Explorer icon
            // and the tray icon. Falls back to the generic system icon on the off chance
            // extraction fails for some reason (e.g. running from a path Windows can't resolve).
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application,
            Text = "VR Session Monitor",
            Visible = true,
        };

        // Logs the exception text that would otherwise only ever appear in a JIT-debugging popup
        // dialog (or nowhere at all, for a non-UI-thread exception) — added after several rounds of
        // asking the user to hand-transcribe a crash dialog during the 2026-08-02/03 tray-menu
        // investigation. Application.ThreadException only fires for the WinForms UI thread; the
        // AppDomain handler covers everything else, though the process still terminates after
        // either fires (this is purely so the crash gets into the log before that happens).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Tray", $"UnhandledException: {e.ExceptionObject}");
        System.Windows.Forms.Application.ThreadException += (_, e) => Log.Error("Tray", $"ThreadException: {e.Exception}");

        // Left-click opens the Status tab, right-click opens the Settings tab — replaces the old
        // right-click ContextMenuStrip entirely. A plain Form doesn't have that menu's handle-
        // creation fragility (see SettingsForm's doc), so there's no retry/rebuild machinery
        // needed here the way ShowTrayMenuWithRetry/RebuildMenuInstance used to require.
        _notifyIcon.MouseUp += (_, e) =>
        {
            Log.Trace("Tray", $"NotifyIcon.MouseUp button={e.Button}");
            if (e.Button == MouseButtons.Left) _settingsForm.ToggleOnTab(0);
            else if (e.Button == MouseButtons.Right) _settingsForm.ToggleOnTab(_settingsForm.SettingsTabIndex);
            else if (e.Button == MouseButtons.Middle && _settingsForm.HomeAssistantTabIndex >= 0) _settingsForm.ToggleOnTab(_settingsForm.HomeAssistantTabIndex);
        };

        _headset.StateChanged += OnHeadsetStateChanged;
        _headset.StateChanged += _orchestrator.OnHeadsetStateChanged;
        _orchestrator.StateChanged += (_, state) => _settingsForm.UpdateSessionStatus(state.ToString());
        _orchestrator.UpdateFindingsAvailable += OnUpdateFindingsAvailable;
        _firmwareNotify.NotificationReceived += (_, _) => _settingsForm.RefreshFirmwareLabel();
        _steamVr.FullyRunningChanged += running => _ = _optimizations.HandleSteamVrRunningChangedAsync(running);

        _headset.Start();
        _trackers.Start();
        _steamVr.Start();
        _vrChat.Start();
        _faceTracking.Start();
        _eyeTracking.Start();
        _vrcFtLifecycle.Start();
        _vrcOscLifecycle.Start();
        _vhSranipalLifecycle.Start();
        _slimeMotion.Start();
        _slimeLifecycle.Start();
        _firmwareNotify.Start();
        _managedApps.Start();
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantClient.Start();
        _homeAssistantManager!.Start();
        // _hmdActivity intentionally NOT started: confirmed live 2026-07-29 (Windows Application
        // Event Log, .NET Runtime event 1026) that its raw OpenVR IVRSystem function-table
        // marshaling crashed the entire process with an unhandled access violation (0xc0000005) in
        // coreclr.dll -- a native AV can't be caught by try/catch and takes down every other
        // monitor with it. AFK detection still works via VrChatOscAfkListener below (VRChat's own
        // /avatar/parameters/AFK over OSC, no native interop) -- only the "took the headset off
        // without toggling AFK" half of detection is lost until this interop is fixed or replaced.
        _vrChatOscAfk!.AfkChanged += (_, afk) => _homeAssistantManager!.OnOscAfkChanged(afk);
        _vrChatOscAfk.Start();
        _groupAutomation.Start();
        if (_config.HomeAssistant.Enabled)
            _ = WaitForHomeAssistantConnectionThenRefreshAsync(notifyOnFailure: false);
#endif

        var statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        statusTimer.Tick += (_, _) => { _settingsForm.RefreshStatus(); _ = _settingsForm.RefreshOptimizationsTabAsync(); _settingsForm.RefreshAppsTab(); UpdateTrayTooltip(); };
        statusTimer.Start();

        Log.Info("Tray", "VR Session Monitor started and all background monitors running.");
        _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Started. Waiting for headset...", ToolTipIcon.Info);
    }

    private void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        UpdateTrayTooltip();
        if (e.IsOnline)
            _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Headset detected — starting session flow.", ToolTipIcon.Info);

        _settingsForm.UpdateHeadsetStatus(e.IsOnline);
    }

    /// <summary>Single owner of the tray tooltip text. Both the headset-state handler and the
    /// periodic refresh route through here — two independent writers would overwrite each other,
    /// making the tooltip flip between whichever fired last.</summary>
    private void UpdateTrayTooltip()
    {
        var text = $"VR Session Monitor — headset {(_headset.IsOnline ? "online" : "offline")}";

        // Snapshot: this runs on HeadsetMonitor's polling thread, while the UI thread can be
        // adding/removing entries. Enumerating the live List<ManagedApp> here can throw
        // InvalidOperationException on a background thread — process-fatal. Same reason
        // ManagedAppWindowService.CheckOnceAsync enumerates a copy.
        var snapshot = _config.ManagedApps.ToArray();
        var manual = _managedApps.CurrentOrigins
            .Where(kv => kv.Value == AppStartOrigin.Manual)
            .Select(kv => snapshot.FirstOrDefault(a => a.Id == kv.Key)?.DisplayName ?? kv.Key)
            .ToList();
        if (manual.Count > 0) text += $" — manual: {string.Join(", ", manual)}";

        // NotifyIcon.Text silently fails (or throws, depending on Windows version) above 63 chars.
        _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
    }

    /// <summary>Notify-only, matching UpdateChecker's own contract — never installs anything, just
    /// gives a heads-up (e.g. VRCOSC's own startup update dialog otherwise blocks an unattended
    /// automated launch entirely) so there's a chance to update manually before that happens.</summary>
    private void OnUpdateFindingsAvailable(object? sender, List<UpdateFinding> findings)
    {
        var outdated = findings.Where(f => f.PossiblyOutdated).ToList();
        if (outdated.Count == 0) return;

        var summary = string.Join(", ", outdated.Select(f => $"{f.Component} ({f.LocalVersion} -> {f.LatestKnownVersion})"));
        Log.Info("Tray", $"Possible updates available: {summary}");
        _notifyIcon.ShowBalloonTip(6000, "VR Session Monitor — updates available",
            $"Possibly outdated: {summary}. Check before it blocks an automated launch.", ToolTipIcon.Warning);
    }

    /// <summary>Ping-sweeps the LAN for a Meta/Oculus MAC (see HeadsetDiscovery), queries
    /// SlimeVR's own SolarXR API for its current device list (see SlimeVrDiscovery's doc for the
    /// full protocol story), and reads Baballonia's camera address fields via UI Automation (see
    /// BaballoniaAutomation.TryReadCameraAddress), merging anything found into config. Trackers
    /// are matched by MAC so re-running this doesn't create duplicates — updates the IP if it
    /// changed, adds a new entry for MACs not already configured. The headset IP is only ever
    /// auto-filled when exactly one Meta/Oculus device is found; with more than one, this
    /// doesn't guess which is "the" headset, it just logs every candidate for you to pick
    /// manually. Eye camera IPs are simply overwritten per configured camera, since there's no
    /// MAC to match on for those. None of the three sources are required to be running/present —
    /// each degrades to "found nothing" on its own, already logged by the underlying call, so
    /// this only needs to report the combined tally.</summary>
    internal async Task AutoDetectAsync()
    {
        Log.Info("Tray", "Auto-detect (headset/SlimeVR/Baballonia) requested.");

        var headsetCandidates = await HeadsetDiscovery.DiscoverAsync().ConfigureAwait(false);
        var headsetUpdated = false;
        if (headsetCandidates.Count == 1)
        {
            var found = headsetCandidates[0];
            if (_config.Network.HeadsetIp != found.Ip)
            {
                _config.Network.HeadsetIp = found.Ip;
                headsetUpdated = true;
                Log.Info("Tray", $"Auto-detected headset at {found.Ip} (MAC {found.Mac}).");
            }
        }
        else if (headsetCandidates.Count > 1)
        {
            Log.Warn("Tray", $"Found {headsetCandidates.Count} Meta/Oculus devices on the network — not guessing which is the headset, pick one manually: " +
                              string.Join(", ", headsetCandidates.Select(c => $"{c.Ip} ({c.Mac})")));
        }

        var discovered = await new SlimeVrDiscovery().DiscoverTrackersAsync().ConfigureAwait(false);

        var trackersAdded = 0;
        var trackersUpdated = 0;
        foreach (var d in discovered)
        {
            if (string.IsNullOrEmpty(d.Mac)) continue;

            var existing = _config.Trackers.FirstOrDefault(t => string.Equals(t.Mac, d.Mac, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (!string.IsNullOrEmpty(d.Ip) && existing.Ip != d.Ip)
                {
                    existing.Ip = d.Ip;
                    trackersUpdated++;
                }
            }
            else
            {
                _config.Trackers.Add(new TrackerConfig { Name = d.Name, Ip = d.Ip, Mac = d.Mac, HasExtension = false });
                trackersAdded++;
            }
        }

        var camsUpdated = 0;
        foreach (var cam in _config.EyeCameras)
        {
            if (string.IsNullOrEmpty(cam.BaballoniaSectionLabel)) continue;

            var address = _eyeTracking.TryReadCameraAddress(cam.BaballoniaSectionLabel);
            if (!string.IsNullOrEmpty(address) && address != cam.Ip)
            {
                cam.Ip = address;
                camsUpdated++;
            }
        }

        _config.Save(_configPath);

        var headsetNote = headsetUpdated ? "headset IP set, "
            : headsetCandidates.Count > 1 ? $"{headsetCandidates.Count} possible headsets (see log), "
            : "";
        var summary = $"Auto-detect: {headsetNote}{trackersAdded} tracker(s) added, {trackersUpdated} updated, {camsUpdated} camera(s) updated.";
        Log.Info("Tray", summary);
        _notifyIcon.ShowBalloonTip(4000, "VR Session Monitor", summary, ToolTipIcon.Info);
    }

#if INCLUDE_HOME_ASSISTANT
    /// <summary>Completes the "get token -> connect -> pick area" flow without touching
    /// appsettings.json by hand or restarting the app: saves Base URL/Access Token from a small
    /// dialog, restarts the (already-constructed) HomeAssistantClient's connection loop in place
    /// so it picks up the new values immediately, then auto-runs the area/light discovery once
    /// connected — the same "Refresh areas/lights" click, just chained automatically.</summary>
    internal async Task SetUpHomeAssistantConnectionAsync()
    {
        using var dialog = new HomeAssistantSetupDialog(_config.HomeAssistant.BaseUrl, _config.HomeAssistant.AccessToken);
        if (dialog.ShowDialog() != DialogResult.OK) return;

        if (string.IsNullOrWhiteSpace(dialog.BaseUrl) || string.IsNullOrWhiteSpace(dialog.AccessToken))
        {
            Log.Warn("Tray", "Home Assistant setup cancelled — Base URL and Access Token both need a value.");
            _notifyIcon.ShowBalloonTip(4000, "VR Session Monitor", "Base URL and Access Token both need a value.", ToolTipIcon.Warning);
            return;
        }

        _config.HomeAssistant.BaseUrl = dialog.BaseUrl;
        _config.HomeAssistant.AccessToken = dialog.AccessToken;
        _config.HomeAssistant.Enabled = true;
        _config.Save(_configPath);
        Log.Info("Tray", $"Home Assistant connection configured (BaseUrl: {dialog.BaseUrl}).");

        _homeAssistantClient.Stop();
        _homeAssistantClient.Start();
        _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Connecting to Home Assistant...", ToolTipIcon.Info);

        await WaitForHomeAssistantConnectionThenRefreshAsync(notifyOnFailure: true).ConfigureAwait(false);
    }

    /// <summary>Shared by the setup dialog and by startup — added 2026-07-29 after the Area/light
    /// pickers were found still showing empty on a plain restart despite SelectedAreaId being
    /// saved correctly: RefreshHomeAssistantAreasAsync (which actually populates them) was
    /// previously only ever called right after the setup dialog connects, or by manually clicking
    /// "Refresh areas/lights" — never automatically when reconnecting to an already-configured
    /// instance at startup.</summary>
    private async Task WaitForHomeAssistantConnectionThenRefreshAsync(bool notifyOnFailure)
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            if (_homeAssistantClient.IsConnected) break;
        }

        if (_homeAssistantClient.IsConnected)
        {
            await RefreshHomeAssistantAreasAsync();
        }
        else
        {
            Log.Warn("Tray", "Home Assistant didn't connect within 5s — check Base URL/Access Token and the log for the actual connection error.");
            if (notifyOnFailure)
                _notifyIcon.ShowBalloonTip(4000, "VR Session Monitor", "Couldn't connect to Home Assistant — check Base URL/Access Token and the log.", ToolTipIcon.Warning);
        }
    }

    internal async Task RefreshHomeAssistantAreasAsync()
    {
        Log.Info("Tray", "Home Assistant area/light refresh requested.");
        List<AreaInfo> areas;
        try
        {
            areas = await _homeAssistantDiscovery.DiscoverAreasAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"Home Assistant area discovery failed: {ex.Message}");
            return;
        }

        _settingsForm.SetHomeAssistantAreas(areas);

        if (!string.IsNullOrEmpty(_config.HomeAssistant.SelectedAreaId))
        {
            Log.Info("Tray", $"Refreshing lights for selected area '{_config.HomeAssistant.SelectedAreaId}'.");
            await RefreshHomeAssistantLightsAsync(_config.HomeAssistant.SelectedAreaId);
        }
        else
        {
            Log.Info("Tray", "No Home Assistant area selected — skipping light refresh.");
        }
    }

    internal async Task RefreshHomeAssistantLightsAsync(string areaId)
    {
        try
        {
            _homeAssistantLightsInSelectedArea = await _homeAssistantDiscovery.DiscoverLightsInAreaAsync(areaId);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"Home Assistant light discovery for area '{areaId}' failed: {ex.Message}");
            return;
        }

        _settingsForm.SetHomeAssistantLights(_homeAssistantLightsInSelectedArea);
        Log.Info("Tray", $"Refreshed light pickers for {_homeAssistantLightsInSelectedArea.Count} light(s).");
    }
#endif

    /// <summary>Manual recovery path added 2026-07-28 after a live incident where face tracking
    /// silently stopped delivering real data while every automated health signal (module process
    /// count, ModuleConnectedToSRanipal) kept reading healthy — the automated stalled-connection
    /// watchdog (FaceTrackingMonitor.HandleStalledConnectionAsync) never fired because it depends
    /// entirely on that same TCP-established check, and restarting VRCFaceTracking.exe alone did
    /// nothing (matching the well-documented 2026-07-16 finding that VRCFaceTracking only attempts
    /// its SRanipal connection once, at its own startup). The legacy ft.cmd script's kill-both-and-
    /// cold-restart was the only thing that actually fixed it that night. This reproduces the
    /// relevant half of that recipe on demand: sr_runtime.exe is explicitly relaunched and confirmed
    /// running BEFORE VRCFaceTracking.exe, rather than killing both and hoping FaceTrackingMonitor's
    /// and VrcFaceTrackingLifecycleManager's two independent ~5s polling loops happen to relaunch
    /// them in a safe order.</summary>
    internal async Task RestartFaceTrackingPipelineAsync()
    {
        Log.Info("Tray", "Face-tracking pipeline restart requested (sr_runtime.exe + VRCFaceTracking.exe).");

        foreach (var name in new[] { "VRCFaceTracking", "sr_runtime" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Warn("Tray", $"Killing {name}.exe for pipeline restart threw: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        var launcher = new ProcessLauncher();

        // suppressUacPrompt: sr_runtime.exe's manifest requests highestAvailable elevation, which
        // pops a UAC consent prompt on every launch with nothing there to click "Yes" — same fix
        // FaceTrackingMonitor.RelaunchAsync already uses.
        var srResult = await launcher.EnsureRunningAsync(
            "sr_runtime", _config.LaunchTargetFor("sranipal", _config.Paths.SRanipalExe), null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs,
            suppressUacPrompt: true).ConfigureAwait(false);
        if (!srResult.Success)
            Log.Warn("Tray", $"sr_runtime.exe restart did not confirm success: {srResult.Error}");

        var vrcftResult = await launcher.EnsureRunningAsync(
            "VRCFaceTracking", _config.LaunchTargetFor("vrcfacetracking", _config.Paths.VrcFaceTrackingExe), null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
        if (!vrcftResult.Success)
            Log.Warn("Tray", $"VRCFaceTracking.exe restart did not confirm success: {vrcftResult.Error}");

        Log.Info("Tray", "Face-tracking pipeline restart complete.");
    }

    internal void OpenLogsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Log.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"Failed to open logs folder: {ex.Message}");
        }
    }

    internal void ExitApp()
    {
        Log.Info("Tray", "Exit requested.");
        _notifyIcon.Visible = false;
        _settingsForm.Dispose();
        _headset.Dispose();
        _trackers.Dispose();
        _steamVr.Dispose();
        _vrChat.Dispose();
        _faceTracking.Dispose();
        _eyeTracking.Dispose();
        _vrcFtLifecycle.Dispose();
        _vrcOscLifecycle.Dispose();
        _vhSranipalLifecycle.Dispose();
        _slimeLifecycle.Dispose();
        _slimeMotion.Dispose();
        _firmwareNotify.Dispose();
        _managedApps?.Dispose();
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantManager?.Dispose();
        _hmdActivity?.Dispose();
        _vrChatOscAfk?.Dispose();
        _groupAutomation.Dispose();
        _homeAssistantClient.Dispose();
#endif
        Log.Shutdown();
        Application.Exit();
    }
}
