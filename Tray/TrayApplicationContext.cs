using System.Diagnostics;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using VrSessionMonitor.Modules;
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
    private readonly FirmwareNotificationListener _firmwareNotify;
    private readonly UpdateChecker _updateChecker;
    private readonly AdbController _adb;
    private readonly SessionOrchestrator _orchestrator;

    private readonly ToolStripMenuItem _headsetItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _trackerItem;
    private readonly ToolStripMenuItem _peripheralItem;
    private readonly ToolStripMenuItem _steamVrItem;
    private readonly ToolStripMenuItem _vrChatItem;
    private readonly ToolStripMenuItem _firmwareItem;
    private readonly ContextMenuStrip _menu;
#if INCLUDE_HOME_ASSISTANT
    private readonly HomeAssistantClient _homeAssistantClient;
    private readonly HomeAssistantAreaDiscovery _homeAssistantDiscovery;
    private readonly ToolStripMenuItem _homeAssistantMenu;
    private readonly ToolStripMenuItem _homeAssistantStatusItem;
    private readonly ToolStripMenuItem _homeAssistantAreaMenu;
    private List<string> _homeAssistantLightsInSelectedArea = new();
    private HomeAssistantLightsManager? _homeAssistantManager;
    private HmdActivityMonitor? _hmdActivity;
    private VrChatOscAfkListener? _vrChatOscAfk;
    private ToolStripMenuItem? _homeAssistantOnLightsMenu;
    private ToolStripMenuItem? _homeAssistantOffLightsMenu;
    private ToolStripMenuItem? _homeAssistantAfkLightsMenu;
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
        _vrcFtLifecycle = new VrcFaceTrackingLifecycleManager(_config, _eyeTracking, _faceTracking);
        _vrcOscLifecycle = new VrcOscLifecycleManager(_config, _vrChat);
        _firmwareNotify = new FirmwareNotificationListener(_config);
        _updateChecker = new UpdateChecker(_config);
        _adb = new AdbController(_config);
        _orchestrator = new SessionOrchestrator(_config, _trackers, _updateChecker, _adb);
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantClient = new HomeAssistantClient(_config);
        _homeAssistantDiscovery = new HomeAssistantAreaDiscovery(_homeAssistantClient);
        _homeAssistantManager = new HomeAssistantLightsManager(_config, _homeAssistantClient, _headset);
        _hmdActivity = new HmdActivityMonitor(_config);
        _vrChatOscAfk = new VrChatOscAfkListener(_config);
#endif

        _headsetItem = new ToolStripMenuItem($"Headset: {(_headset.IsOnline ? "online" : "offline")}") { Enabled = false };
        _statusItem = new ToolStripMenuItem("Status: idle") { Enabled = false };
        _trackerItem = new ToolStripMenuItem("Trackers: --") { Enabled = false };
        _peripheralItem = new ToolStripMenuItem("Eye/Face tracking: --") { Enabled = false };
        _steamVrItem = new ToolStripMenuItem("SteamVR: --") { Enabled = false };
        _vrChatItem = new ToolStripMenuItem("VRChat: --") { Enabled = false };
        _firmwareItem = new ToolStripMenuItem("Firmware self-heal: none yet") { Enabled = false };

        var autoLaunchVrChatItem = new ToolStripMenuItem("Auto-start VRChat")
        {
            CheckOnClick = true,
            Checked = _config.SessionFlow.AutoLaunchVrChat,
        };
        autoLaunchVrChatItem.Click += (_, _) =>
        {
            _config.SessionFlow.AutoLaunchVrChat = autoLaunchVrChatItem.Checked;
            _config.Save(_configPath);
            Log.Info("Tray", $"Auto-start VRChat toggled {(_config.SessionFlow.AutoLaunchVrChat ? "ON" : "OFF")} via tray menu.");
        };

        var autoLaunchOvrToolkitItem = new ToolStripMenuItem("Auto-start OVR Toolkit")
        {
            CheckOnClick = true,
            Checked = _config.SessionFlow.AutoLaunchOvrToolkit,
        };
        autoLaunchOvrToolkitItem.Click += (_, _) =>
        {
            _config.SessionFlow.AutoLaunchOvrToolkit = autoLaunchOvrToolkitItem.Checked;
            _config.Save(_configPath);
            Log.Info("Tray", $"Auto-start OVR Toolkit toggled {(_config.SessionFlow.AutoLaunchOvrToolkit ? "ON" : "OFF")} via tray menu.");
        };

        var lowPowerVrChatItem = new ToolStripMenuItem("VRChat: low-power window")
        {
            CheckOnClick = true,
            Checked = _config.SessionFlow.VrChatLowPowerMode,
        };
        lowPowerVrChatItem.Click += (_, _) =>
        {
            _config.SessionFlow.VrChatLowPowerMode = lowPowerVrChatItem.Checked;
            _config.Save(_configPath);
            Log.Info("Tray", $"VRChat low-power window mode toggled {(_config.SessionFlow.VrChatLowPowerMode ? "ON" : "OFF")} via tray menu.");
        };

        var autoManageBaballoniaItem = new ToolStripMenuItem("Auto-start/stop Baballonia")
        {
            CheckOnClick = true,
            Checked = _config.BaballoniaLifecycle.Enabled,
        };
        autoManageBaballoniaItem.Click += (_, _) =>
        {
            _config.BaballoniaLifecycle.Enabled = autoManageBaballoniaItem.Checked;
            _config.Save(_configPath);
            Log.Info("Tray", $"Auto-start/stop Baballonia toggled {(_config.BaballoniaLifecycle.Enabled ? "ON" : "OFF")} via tray menu.");
        };

        var autoManageVrcFtItem = new ToolStripMenuItem("Auto-start/stop VRCFaceTracking")
        {
            CheckOnClick = true,
            Checked = _config.VrcFaceTrackingLifecycle.Enabled,
        };
        autoManageVrcFtItem.Click += (_, _) =>
        {
            _config.VrcFaceTrackingLifecycle.Enabled = autoManageVrcFtItem.Checked;
            _config.Save(_configPath);
            Log.Info("Tray", $"Auto-start/stop VRCFaceTracking toggled {(_config.VrcFaceTrackingLifecycle.Enabled ? "ON" : "OFF")} via tray menu.");
        };

        var autoManageVrcOscItem = new ToolStripMenuItem("Auto-start/stop VRCOSC")
        {
            CheckOnClick = true,
            Checked = _config.VrcOscLifecycle.Enabled,
        };
        autoManageVrcOscItem.Click += (_, _) =>
        {
            _config.VrcOscLifecycle.Enabled = autoManageVrcOscItem.Checked;
            _config.Save(_configPath);
            Log.Info("Tray", $"Auto-start/stop VRCOSC toggled {(_config.VrcOscLifecycle.Enabled ? "ON" : "OFF")} via tray menu.");
        };

        // Checked state reads the actual registry Run key, not a config flag, so this can never
        // drift from what Windows will really do — the registry key IS the source of truth.
        var startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = WindowsStartup.IsEnabled(),
        };
        startWithWindowsItem.Click += (_, _) =>
        {
            WindowsStartup.SetEnabled(startWithWindowsItem.Checked);
            Log.Info("Tray", $"Start with Windows toggled {(startWithWindowsItem.Checked ? "ON" : "OFF")} via tray menu.");
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_headsetItem);
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_trackerItem);
        _menu.Items.Add(_peripheralItem);
        _menu.Items.Add(_steamVrItem);
        _menu.Items.Add(_vrChatItem);
        _menu.Items.Add(_firmwareItem);
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantStatusItem = new ToolStripMenuItem("Home Assistant: disconnected") { Enabled = false };
        _homeAssistantAreaMenu = new ToolStripMenuItem("Area: (none selected)");
        _homeAssistantMenu = new ToolStripMenuItem("Home Assistant");
        _homeAssistantMenu.DropDownItems.Add(_homeAssistantStatusItem);
        _homeAssistantMenu.DropDownItems.Add("Get access token...", null, (_, _) => OpenHomeAssistantTokenPage());
        _homeAssistantMenu.DropDownItems.Add(_homeAssistantAreaMenu);
        _homeAssistantMenu.DropDownItems.Add("Refresh areas/lights", null, (_, _) => _ = RefreshHomeAssistantAreasAsync());
        _homeAssistantOnLightsMenu = new ToolStripMenuItem("Headset On lights");
        _homeAssistantOffLightsMenu = new ToolStripMenuItem("Headset Off lights");
        _homeAssistantAfkLightsMenu = new ToolStripMenuItem("AFK lights");
        _homeAssistantMenu.DropDownItems.Add(_homeAssistantOnLightsMenu);
        _homeAssistantMenu.DropDownItems.Add(_homeAssistantOffLightsMenu);
        _homeAssistantMenu.DropDownItems.Add(_homeAssistantAfkLightsMenu);
        _menu.Items.Add(_homeAssistantMenu);
#endif
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(autoLaunchVrChatItem);
        _menu.Items.Add(autoLaunchOvrToolkitItem);
        _menu.Items.Add(lowPowerVrChatItem);
        _menu.Items.Add(autoManageBaballoniaItem);
        _menu.Items.Add(autoManageVrcFtItem);
        _menu.Items.Add(autoManageVrcOscItem);
        _menu.Items.Add(startWithWindowsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Force run now", null, (_, _) => _ = _orchestrator.RunSessionStartAsync());
        _menu.Items.Add("Restart VRChat now", null, (_, _) => _ = _orchestrator.RestartVrChatAsync());
        _menu.Items.Add("Restart face-tracking pipeline", null, (_, _) => _ = RestartFaceTrackingPipelineAsync());
        _menu.Items.Add("Recheck trackers", null, (_, _) => _ = _trackers.CheckAllAsync());
        _menu.Items.Add("Auto-detect headset/trackers/cameras", null, (_, _) => _ = AutoDetectAsync());
        _menu.Items.Add("Open logs folder", null, (_, _) => OpenLogsFolder());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            // Pulls the icon straight off this exe's own embedded resource (set via
            // <ApplicationIcon>icon.ico</ApplicationIcon> in the csproj) rather than shipping a
            // second copy of the file — one source of truth for both the taskbar/Explorer icon
            // and the tray icon. Falls back to the generic system icon on the off chance
            // extraction fails for some reason (e.g. running from a path Windows can't resolve).
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application,
            Text = "VR Session Monitor",
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _headset.StateChanged += OnHeadsetStateChanged;
        _headset.StateChanged += _orchestrator.OnHeadsetStateChanged;
        _orchestrator.StateChanged += (_, state) => UpdateStatusItem(state.ToString());
        _firmwareNotify.NotificationReceived += (_, _) => UpdateFirmwareItem();

        _headset.Start();
        _trackers.Start();
        _steamVr.Start();
        _vrChat.Start();
        _faceTracking.Start();
        _eyeTracking.Start();
        _vrcFtLifecycle.Start();
        _vrcOscLifecycle.Start();
        _firmwareNotify.Start();
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantClient.Start();
        _homeAssistantManager!.Start();
        _hmdActivity!.PresenceChanged += (_, present) => _homeAssistantManager!.OnHmdPresenceChanged(present);
        _hmdActivity.Start();
        _vrChatOscAfk!.AfkChanged += (_, afk) => _homeAssistantManager!.OnOscAfkChanged(afk);
        _vrChatOscAfk.Start();
#endif

        var statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        statusTimer.Tick += (_, _) => UpdateStatusItems();
        statusTimer.Start();

        Log.Info("Tray", "VR Session Monitor started and all background monitors running.");
        _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Started. Waiting for headset...", ToolTipIcon.Info);
    }

    private void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        _notifyIcon.Text = $"VR Session Monitor — headset {(e.IsOnline ? "online" : "offline")}";
        if (e.IsOnline)
            _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Headset detected — starting session flow.", ToolTipIcon.Info);

        // Fires from HeadsetMonitor's own background polling loop, not the UI thread — same
        // InvokeRequired guard as UpdateStatusItem, needed because ToolStripMenuItem (unlike
        // NotifyIcon above) isn't safe to touch off the UI thread.
        var text = $"Headset: {(e.IsOnline ? "online" : "offline")}";
        if (_menu.InvokeRequired)
            _menu.Invoke(() => _headsetItem.Text = text);
        else
            _headsetItem.Text = text;
    }

    private void UpdateStatusItem(string state)
    {
        if (_menu.InvokeRequired)
            _menu.Invoke(() => _statusItem.Text = $"Status: {state}");
        else
            _statusItem.Text = $"Status: {state}";
    }

    /// <summary>Refreshes every polling-based tray status line. Always called from the UI-thread
    /// Timer.Tick, so unlike OnHeadsetStateChanged/UpdateFirmwareItem (fired from other monitors'
    /// own background loops) this needs no InvokeRequired guard.</summary>
    private void UpdateStatusItems()
    {
        _headsetItem.Text = $"Headset: {(_headset.IsOnline ? "online" : "offline")}";

        _trackerItem.Text = $"Trackers: {_trackers.Summarize()}";

        var p = _faceTracking.Current;
        var cams = _eyeTracking.Current;
        var parts = new List<string>();
        if (!p.VirtualHereRunning) parts.Add("VirtualHere down");
        if (!p.SRanipalRunning) parts.Add("SRanipal down");
        else if (!p.SRanipalServiceRunning) parts.Add("SRanipalService down (zombie sr_runtime?)");
        if (p.VirtualHereRunning && !p.ViveCameraDevicePresent) parts.Add("Vive tracker not attached");
        if (!p.VrcFaceTrackingRunning) parts.Add("VRCFaceTracking down");
        else if (p.ModuleProcessCount == 0) parts.Add("no tracking modules loaded");
        else if (p.SRanipalRunning && p.ViveCameraDevicePresent && !p.ModuleConnectedToSRanipal) parts.Add("face module not connected to SRanipal");
        // Online (ping) is authoritative and fast; Streaming (TCP ESTABLISHED state) can lag
        // stale-true for a long time after an ungraceful disconnect (e.g. power loss) since TCP
        // has no way to notice a dead peer without traffic/keepalives. Must check Online first.
        foreach (var cam in cams.Where(c => !c.Online || !c.Streaming))
            parts.Add(cam.Online ? $"{cam.Camera.Name} not streaming" : $"{cam.Camera.Name} offline");
        _peripheralItem.Text = parts.Count == 0
            ? $"Eye/Face tracking: all running ({p.ModuleProcessCount} module(s), {cams.Count(c => c.Streaming)}/{cams.Count} cameras streaming)"
            : $"Eye/Face tracking: {string.Join(", ", parts)}";

        // Surfaces scheduled auto-start/stop timers (restart cooldowns, shutdown/close countdowns,
        // escalation backoffs) that were previously only visible by watching the log at the right
        // moment — added 2026-07-24 after an unprompted-looking VRCFaceTracking launch turned out
        // to have a perfectly logged reason once checked after the fact.
        var pending = new List<string>();
        pending.AddRange(_eyeTracking.DescribePendingActions());
        if (_faceTracking.DescribePendingAction() is string faceTrackingPending) pending.Add(faceTrackingPending);
        if (_vrcFtLifecycle.DescribePendingAction() is string vrcFtPending) pending.Add(vrcFtPending);
        if (pending.Count > 0)
            _peripheralItem.Text += $" — next: {string.Join(", ", pending)}";

        var sv = _steamVr.Current;
        if (sv.VrServerRunning && sv.VrMonitorRunning && sv.VrCompositorRunning)
            _steamVrItem.Text = "SteamVR: running";
        else if (!sv.VrServerRunning && !sv.VrMonitorRunning && !sv.VrCompositorRunning)
            _steamVrItem.Text = "SteamVR: not running";
        else
        {
            var down = new List<string>();
            if (!sv.VrServerRunning) down.Add("vrserver");
            if (!sv.VrMonitorRunning) down.Add("vrmonitor");
            if (!sv.VrCompositorRunning) down.Add("vrcompositor");
            _steamVrItem.Text = $"SteamVR: partial (down: {string.Join(", ", down)})";
        }
        if (_steamVr.DescribePendingAction() is string steamVrPending)
            _steamVrItem.Text += $" — next: {steamVrPending}";

        _vrChatItem.Text = $"VRChat: {(_vrChat.Current.Running ? "running" : "not running")}";
        if (_vrcOscLifecycle.DescribePendingAction() is string vrcOscPending)
            _vrChatItem.Text += $" — next: {vrcOscPending}";

        UpdateFirmwareItem();
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantStatusItem.Text = $"Home Assistant: {(_homeAssistantClient.IsConnected ? "connected" : "disconnected")}";
#endif

        Log.Trace("Tray", $"{_headsetItem.Text} | {_trackerItem.Text} | {_peripheralItem.Text} | {_steamVrItem.Text} | {_vrChatItem.Text}");
    }

    /// <summary>Called both from UpdateStatusItems (UI thread, no marshaling needed) and directly
    /// off FirmwareNotificationListener.NotificationReceived (that listener's own background
    /// receive loop) for an immediate refresh the moment a self-heal event arrives, rather than
    /// waiting up to 5s for the next timer tick — so it guards for both cases itself.</summary>
    private void UpdateFirmwareItem()
    {
        void Apply()
        {
            _firmwareItem.Text = _firmwareNotify.LastEventAtUtc is DateTime at
                ? $"Firmware self-heal: {_firmwareNotify.LastEventSummary} ({FormatAgo(DateTime.UtcNow - at)} ago)"
                : "Firmware self-heal: none yet";
        }

        if (_menu.InvokeRequired)
            _menu.Invoke(Apply);
        else
            Apply();
    }

    private static string FormatAgo(TimeSpan span) =>
        span.TotalMinutes < 1 ? "just now" :
        span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" :
        $"{(int)span.TotalHours}h{span.Minutes}m";

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
    private async Task AutoDetectAsync()
    {
        Log.Info("Tray", "Auto-detect (headset/SlimeVR/Baballonia) requested from tray menu.");

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
    /// <summary>Jumps straight to HA's Long-Lived Access Tokens section instead of making the user
    /// find profile -> Security -> scroll to it by hand. Only needs BaseUrl set (not a working
    /// token yet — getting the token is the whole point), so this works even before HomeAssistant
    /// is fully configured.</summary>
    private void OpenHomeAssistantTokenPage()
    {
        var baseUrl = _config.HomeAssistant.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Log.Warn("Tray", "Home Assistant BaseUrl isn't set yet — add it to appsettings.json first (e.g. http://homeassistant.local:8123), then use this again.");
            _notifyIcon.ShowBalloonTip(4000, "VR Session Monitor", "Set HomeAssistant.BaseUrl in appsettings.json first, then try this again.", ToolTipIcon.Warning);
            return;
        }

        var url = $"{baseUrl.TrimEnd('/')}/profile/security";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log.Info("Tray", $"Opened Home Assistant token page: {url}");
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"Failed to open Home Assistant token page: {ex.Message}");
        }
    }

    private async Task RefreshHomeAssistantAreasAsync()
    {
        Log.Info("Tray", "Home Assistant area/light refresh requested from tray menu.");
        List<AreaInfo> areas;
        try
        {
            areas = await _homeAssistantDiscovery.DiscoverAreasAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"Home Assistant area discovery failed: {ex.Message}");
            return;
        }

        void Rebuild()
        {
            _homeAssistantAreaMenu.DropDownItems.Clear();
            foreach (var area in areas)
            {
                var item = new ToolStripMenuItem(area.Name) { Checked = area.AreaId == _config.HomeAssistant.SelectedAreaId };
                item.Click += (_, _) =>
                {
                    _config.HomeAssistant.SelectedAreaId = area.AreaId;
                    _config.Save(_configPath);
                    Log.Info("Tray", $"Home Assistant area selected: {area.Name} ({area.AreaId})");
                    _homeAssistantAreaMenu.Text = $"Area: {area.Name}";
                    foreach (ToolStripMenuItem sibling in _homeAssistantAreaMenu.DropDownItems)
                        sibling.Checked = sibling == item;
                    _ = RefreshHomeAssistantLightsAsync(area.AreaId);
                };
                _homeAssistantAreaMenu.DropDownItems.Add(item);
            }

            var selected = areas.FirstOrDefault(a => a.AreaId == _config.HomeAssistant.SelectedAreaId);
            _homeAssistantAreaMenu.Text = selected is not null ? $"Area: {selected.Name}" : "Area: (none selected)";
        }

        if (_menu.InvokeRequired) _menu.Invoke(Rebuild); else Rebuild();

        if (!string.IsNullOrEmpty(_config.HomeAssistant.SelectedAreaId))
            await RefreshHomeAssistantLightsAsync(_config.HomeAssistant.SelectedAreaId).ConfigureAwait(false);
    }

    private async Task RefreshHomeAssistantLightsAsync(string areaId)
    {
        try
        {
            _homeAssistantLightsInSelectedArea = await _homeAssistantDiscovery.DiscoverLightsInAreaAsync(areaId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"Home Assistant light discovery for area '{areaId}' failed: {ex.Message}");
            return;
        }

        void Rebuild()
        {
            RebuildLightActionMenu(_homeAssistantOnLightsMenu!, _config.HomeAssistant.HeadsetOnActions);
            RebuildLightActionMenu(_homeAssistantOffLightsMenu!, _config.HomeAssistant.HeadsetOffActions);
            RebuildLightActionMenu(_homeAssistantAfkLightsMenu!, _config.HomeAssistant.AfkActions);
        }

        if (_menu.InvokeRequired) _menu.Invoke(Rebuild); else Rebuild();
    }

    /// <summary>Builds one "light name -> On/Off/No change" radio submenu per light in the
    /// currently-selected area, persisting the choice into the given tri-state map. Shared by all
    /// three trigger light lists (Headset On, Headset Off, AFK) — each map is independent, so the
    /// same light can have a different action per trigger.</summary>
    private void RebuildLightActionMenu(ToolStripMenuItem menu, Dictionary<string, string> actions)
    {
        menu.DropDownItems.Clear();
        foreach (var entityId in _homeAssistantLightsInSelectedArea)
        {
            var lightMenu = new ToolStripMenuItem(entityId);
            var current = actions.GetValueOrDefault(entityId, LightAction.NoChange.ToConfigString()).ParseOrDefault();

            foreach (var option in new[] { LightAction.On, LightAction.Off, LightAction.NoChange })
            {
                var optionItem = new ToolStripMenuItem(option.ToConfigString()) { Checked = option == current };
                optionItem.Click += (_, _) =>
                {
                    actions[entityId] = option.ToConfigString();
                    _config.Save(_configPath);
                    foreach (ToolStripMenuItem sibling in lightMenu.DropDownItems)
                        sibling.Checked = sibling.Text == option.ToConfigString();
                    Log.Info("Tray", $"Home Assistant: {entityId} set to {option} for this trigger.");
                };
                lightMenu.DropDownItems.Add(optionItem);
            }

            menu.DropDownItems.Add(lightMenu);
        }
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
    private async Task RestartFaceTrackingPipelineAsync()
    {
        Log.Info("Tray", "Face-tracking pipeline restart requested via tray menu (sr_runtime.exe + VRCFaceTracking.exe).");

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
            "sr_runtime", _config.Paths.SRanipalExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs,
            suppressUacPrompt: true).ConfigureAwait(false);
        if (!srResult.Success)
            Log.Warn("Tray", $"sr_runtime.exe restart did not confirm success: {srResult.Error}");

        var vrcftResult = await launcher.EnsureRunningAsync(
            "VRCFaceTracking", _config.Paths.VrcFaceTrackingExe, null,
            _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);
        if (!vrcftResult.Success)
            Log.Warn("Tray", $"VRCFaceTracking.exe restart did not confirm success: {vrcftResult.Error}");

        Log.Info("Tray", "Face-tracking pipeline restart complete.");
    }

    private static void OpenLogsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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

    private void ExitApp()
    {
        Log.Info("Tray", "Exit requested from tray menu.");
        _notifyIcon.Visible = false;
        _headset.Dispose();
        _trackers.Dispose();
        _steamVr.Dispose();
        _vrChat.Dispose();
        _faceTracking.Dispose();
        _eyeTracking.Dispose();
        _vrcFtLifecycle.Dispose();
        _vrcOscLifecycle.Dispose();
        _firmwareNotify.Dispose();
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantManager?.Dispose();
        _hmdActivity?.Dispose();
        _vrChatOscAfk?.Dispose();
        _homeAssistantClient.Dispose();
#endif
        Log.Shutdown();
        Application.Exit();
    }
}
