using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using VrSessionMonitor.Modules;
#if INCLUDE_HOME_ASSISTANT
using VrSessionMonitor.Modules.HomeAssistant;
#endif

namespace VrSessionMonitor.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
    private ContextMenuStrip _menu;
    private readonly Form _menuOwnerWindow;
#if INCLUDE_HOME_ASSISTANT
    private readonly HomeAssistantClient _homeAssistantClient;
    private readonly HomeAssistantAreaDiscovery _homeAssistantDiscovery;
    private readonly ToolStripMenuItem _homeAssistantMenu;
    private readonly ToolStripMenuItem _homeAssistantStatusItem;
    private readonly ToolStripMenuItem _homeAssistantAreaMenu;
    private List<LightInfo> _homeAssistantLightsInSelectedArea = new();
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
        _homeAssistantMenu.DropDownItems.Add("Set up connection...", null, (_, _) => _ = SetUpHomeAssistantConnectionAsync());
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

        // Settings and Actions grouped into submenus (added 2026-08-02) rather than sitting flat
        // at the top level — confirmed live that a 25-item top-level menu needs vertical scrolling
        // on this display, and WinForms' scroll-arrow control for a ToolStripDropDownMenu can fail
        // outright ("Error creating window handle", Win32 1400) rather than just looking ugly.
        // Keeping only the always-glanceable status lines + Home Assistant + these two submenus +
        // Exit at the top level sidesteps needing to scroll at all.
        var settingsMenu = new ToolStripMenuItem("Settings");
        settingsMenu.DropDownItems.Add(autoLaunchVrChatItem);
        settingsMenu.DropDownItems.Add(autoLaunchOvrToolkitItem);
        settingsMenu.DropDownItems.Add(lowPowerVrChatItem);
        settingsMenu.DropDownItems.Add(autoManageBaballoniaItem);
        settingsMenu.DropDownItems.Add(autoManageVrcFtItem);
        settingsMenu.DropDownItems.Add(autoManageVrcOscItem);
        settingsMenu.DropDownItems.Add(startWithWindowsItem);
        _menu.Items.Add(settingsMenu);

        var actionsMenu = new ToolStripMenuItem("Actions");
        actionsMenu.DropDownItems.Add("Force run now", null, (_, _) => _ = _orchestrator.RunSessionStartAsync());
        actionsMenu.DropDownItems.Add("Restart VRChat now", null, (_, _) => _ = _orchestrator.RestartVrChatAsync());
        actionsMenu.DropDownItems.Add("Restart face-tracking pipeline", null, (_, _) => _ = RestartFaceTrackingPipelineAsync());
        actionsMenu.DropDownItems.Add("Recheck trackers", null, (_, _) => _ = _trackers.CheckAllAsync());
        actionsMenu.DropDownItems.Add("Auto-detect headset/trackers/cameras", null, (_, _) => _ = AutoDetectAsync());
        actionsMenu.DropDownItems.Add("Open logs folder", null, (_, _) => OpenLogsFolder());
        _menu.Items.Add(actionsMenu);

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
            Visible = true,
        };

        // A real, hidden Form to own the SetForegroundWindow call ShowTrayMenuWithRetry needs.
        // NotifyIcon.ShowContextMenu() does this internally against its own hidden window before
        // showing the ContextMenuStrip — without it, the menu opens and is treated as if it
        // immediately lost focus, closing itself within the same frame (confirmed live 2026-08-03:
        // MouseUp fired, Show() returned with no exception, but no menu window ever existed a
        // moment later). NotifyIcon doesn't expose its own window handle publicly, hence a
        // dedicated Form instead of reusing it.
        _menuOwnerWindow = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Size = new System.Drawing.Size(0, 0),
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
            Opacity = 0,
        };
        _menuOwnerWindow.Show();
        _menuOwnerWindow.Hide();

        // Logs the exception text that would otherwise only ever appear in a JIT-debugging popup
        // dialog (or nowhere at all, for a non-UI-thread exception) — added after several rounds of
        // asking the user to hand-transcribe a crash dialog during the 2026-08-02/03 tray-menu
        // investigation. Application.ThreadException only fires for the WinForms UI thread; the
        // AppDomain handler covers everything else, though the process still terminates after
        // either fires (this is purely so the crash gets into the log before that happens).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Tray", $"UnhandledException: {e.ExceptionObject}");
        System.Windows.Forms.Application.ThreadException += (_, e) => Log.Error("Tray", $"ThreadException: {e.Exception}");

        // Deliberately NOT using NotifyIcon.ContextMenuStrip's automatic show-on-right-click
        // mechanism — confirmed 2026-08-02/03/2026-08-03 (extensive investigation, see git log
        // around commit b96de71) that ToolStripDropDownMenu's internal scroll-arrow control
        // intermittently fails Win32 CreateWindowEx ("Error creating window handle") when the menu
        // becomes visible, a genuine timing-sensitive WinForms bug that reproduces even in a bare
        // minimal repro once enough background Invoke() traffic competes for the UI thread's
        // message queue at the same moment — not tied to any specific dependency, DPI mode, or
        // menu content in the end. Showing the menu manually here means a failed attempt can just
        // be retried immediately instead of silently doing nothing (the automatic mechanism has no
        // such recovery — one failed CreateWindowEx and the click is simply swallowed).
        _notifyIcon.MouseUp += (_, e) =>
        {
            Log.Trace("Tray", $"NotifyIcon.MouseUp button={e.Button}");
            if (e.Button != MouseButtons.Right) return;
            ShowTrayMenuWithRetry();
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
        // _hmdActivity intentionally NOT started: confirmed live 2026-07-29 (Windows Application
        // Event Log, .NET Runtime event 1026) that its raw OpenVR IVRSystem function-table
        // marshaling crashed the entire process with an unhandled access violation (0xc0000005) in
        // coreclr.dll -- a native AV can't be caught by try/catch and takes down every other
        // monitor with it. AFK detection still works via VrChatOscAfkListener below (VRChat's own
        // /avatar/parameters/AFK over OSC, no native interop) -- only the "took the headset off
        // without toggling AFK" half of detection is lost until this interop is fixed or replaced.
        _vrChatOscAfk!.AfkChanged += (_, afk) => _homeAssistantManager!.OnOscAfkChanged(afk);
        _vrChatOscAfk.Start();
        if (_config.HomeAssistant.Enabled)
            _ = WaitForHomeAssistantConnectionThenRefreshAsync(notifyOnFailure: false);
#endif

        var statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        statusTimer.Tick += (_, _) => UpdateStatusItems();
        statusTimer.Start();

        Log.Info("Tray", "VR Session Monitor started and all background monitors running.");
        _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Started. Waiting for headset...", ToolTipIcon.Info);
    }

    /// <summary>Shows the tray context menu manually (rather than via NotifyIcon.ContextMenuStrip's
    /// automatic right-click handling) so a failed attempt can be retried instead of just doing
    /// nothing. See the MouseUp wiring's doc in the constructor for why this exists: a genuine
    /// WinForms bug in ToolStripDropDownMenu's internal scroll-arrow control can throw
    /// Win32Exception 1400 ("Error creating window handle") when the menu becomes visible.
    /// Confirmed live 2026-08-03 that once this fires for a given ContextMenuStrip instance, every
    /// immediate retry against the SAME instance fails identically within milliseconds — the
    /// scroll-arrow control's handle-creation failure leaves it permanently broken, not just
    /// unlucky-timing broken. So each retry here rebuilds an entirely fresh ContextMenuStrip
    /// (reusing the existing ToolStripItem objects — moving a ToolStripItem to a new owner is
    /// fine) instead of reusing the same, now-poisoned instance.</summary>
    private void ShowTrayMenuWithRetry()
    {
        const int maxAttempts = 5;
        var position = Cursor.Position;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // SetForegroundWindow first: without it, confirmed live 2026-08-03 that Show()
                // returns with no exception, yet the menu window never actually stays open — it's
                // treated as if it lost focus the instant it appeared and closes itself within the
                // same frame. See _menuOwnerWindow's doc for why a dedicated hidden Form owns this.
                SetForegroundWindow(_menuOwnerWindow.Handle);

                // Explicit direction (rather than plain Show(Point), which defaults to opening
                // downward) since the tray icon sits at the screen edge next to the taskbar —
                // opening upward is both the conventional tray-menu direction and keeps it from
                // extending off-screen below the taskbar. ToolStripDropDown still auto-corrects if
                // this doesn't fully fit (e.g. taskbar pinned to the top instead of the bottom).
                _menu.Show(position, ToolStripDropDownDirection.AboveLeft);
                return;
            }
            catch (Win32Exception ex)
            {
                Log.Warn("Tray", $"Showing tray menu failed on attempt {attempt}/{maxAttempts}: {ex.Message}");
                RebuildMenuInstance();
            }
        }
        Log.Error("Tray", $"Showing tray menu failed after {maxAttempts} attempts — giving up for this click.");
    }

    /// <summary>Moves every existing top-level ToolStripItem into a brand-new ContextMenuStrip and
    /// disposes the old one — see ShowTrayMenuWithRetry's doc for why a failed Show() means the old
    /// instance's internal scroll-arrow control is permanently broken, not just unlucky timing.</summary>
    private void RebuildMenuInstance()
    {
        var oldMenu = _menu;
        if (oldMenu.Visible) oldMenu.Close();

        var items = new ToolStripItem[oldMenu.Items.Count];
        oldMenu.Items.CopyTo(items, 0);
        oldMenu.Items.Clear(); // detach before disposing the old strip, so items survive it

        var newMenu = new ContextMenuStrip();
        newMenu.Items.AddRange(items);
        _menu = newMenu;

        oldMenu.Dispose();
    }

    private void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        _notifyIcon.Text = $"VR Session Monitor — headset {(e.IsOnline ? "online" : "offline")}";
        if (e.IsOnline)
            _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Headset detected — starting session flow.", ToolTipIcon.Info);

        // Fires from HeadsetMonitor's own background polling loop, not the UI thread — same
        // InvokeRequired guard as UpdateStatusItem, needed because ToolStripMenuItem (unlike
        // NotifyIcon above) isn't safe to touch off the UI thread. Also skips while the menu is
        // open — see UpdateStatusItems' doc for why setting .Text on a visible menu item can crash.
        var text = $"Headset: {(e.IsOnline ? "online" : "offline")}";
        if (_menu.InvokeRequired)
            _menu.Invoke(() => { if (!_menu.Visible) _headsetItem.Text = text; });
        else if (!_menu.Visible)
            _headsetItem.Text = text;
    }

    private void UpdateStatusItem(string state)
    {
        if (_menu.InvokeRequired)
            _menu.Invoke(() => { if (!_menu.Visible) _statusItem.Text = $"Status: {state}"; });
        else if (!_menu.Visible)
            _statusItem.Text = $"Status: {state}";
    }

    /// <summary>Refreshes every polling-based tray status line. Always called from the UI-thread
    /// Timer.Tick, so unlike OnHeadsetStateChanged/UpdateFirmwareItem (fired from other monitors'
    /// own background loops) this needs no InvokeRequired guard.</summary>
    private void UpdateStatusItems()
    {
        // Skip entirely while the user has the menu open — confirmed live 2026-08-02 as the real
        // cause of a reliably reproducible crash ("Error creating window handle", Win32 1400):
        // setting .Text on a currently-visible ToolStripItem triggers a live layout recalculation,
        // which tries to (re)create the scroll-arrow control's window handle once the menu is long
        // enough to need scrolling (this one has grown a lot — SRanipalService state, all the
        // "waiting for..." pending-action strings, etc.) and that handle creation can fail outright.
        // This fired on every single 5s tick the menu was left open, independent of Explorer,
        // process restarts, or anything else — skipping here just means the refresh catches up on
        // the next tick after the user closes the menu, which costs a few seconds of stale text
        // against crashing the whole app mid-interaction.
        if (_menu.Visible) return;

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
            // Skips while the menu is open — see UpdateStatusItems' doc for why setting .Text on
            // a visible menu item can crash.
            if (_menu.Visible) return;

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
    /// <summary>Completes the "get token -> connect -> pick area" flow without touching
    /// appsettings.json by hand or restarting the app: saves Base URL/Access Token from a small
    /// dialog, restarts the (already-constructed) HomeAssistantClient's connection loop in place
    /// so it picks up the new values immediately, then auto-runs the area/light discovery once
    /// connected — the same "Refresh areas/lights" click, just chained automatically.</summary>
    private async Task SetUpHomeAssistantConnectionAsync()
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
        Log.Info("Tray", $"Home Assistant connection configured via tray dialog (BaseUrl: {dialog.BaseUrl}).");

        _homeAssistantClient.Stop();
        _homeAssistantClient.Start();
        _notifyIcon.ShowBalloonTip(3000, "VR Session Monitor", "Connecting to Home Assistant...", ToolTipIcon.Info);

        await WaitForHomeAssistantConnectionThenRefreshAsync(notifyOnFailure: true).ConfigureAwait(false);
    }

    /// <summary>Shared by the setup dialog and by startup — added 2026-07-29 after the Area/light
    /// tray menus were found still showing "(none selected)"/empty on a plain restart despite
    /// SelectedAreaId being saved correctly: RefreshHomeAssistantAreasAsync (which actually builds
    /// those menu items) was previously only ever called right after the setup dialog connects, or
    /// by manually clicking "Refresh areas/lights" — never automatically when reconnecting to an
    /// already-configured instance at startup.</summary>
    private async Task WaitForHomeAssistantConnectionThenRefreshAsync(bool notifyOnFailure)
    {
        // No ConfigureAwait(false) anywhere in this method, including this delay loop: confirmed
        // live 2026-08-02/03/04 that running RefreshHomeAssistantAreasAsync's menu-item rebuild on
        // a thread-pool thread — reached via this method being fired fire-and-forget from the
        // constructor — silently corrupts the tray ContextMenuStrip well before any crash: right/
        // left-click stop opening the menu at all, with no exception anywhere. An EARLIER fix here
        // only removed ConfigureAwait(false) from the await of RefreshHomeAssistantAreasAsync()
        // itself, but this delay loop's own ConfigureAwait(false) was still discarding the
        // WindowsFormsSynchronizationContext before ever reaching that later await — there was
        // nothing left to restore by that point, so the "fix" didn't actually change which thread
        // the Home Assistant refresh ran on. Removing it here too (not just downstream) is what
        // actually keeps the whole chain on the UI thread.
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

    private async Task RefreshHomeAssistantAreasAsync()
    {
        Log.Info("Tray", "Home Assistant area/light refresh requested from tray menu.");
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

        void Rebuild()
        {
            // Closing first avoids a live crash confirmed 2026-08-02 ("Error creating window
            // handle", Win32 1400): rebuilding a ContextMenuStrip's items while the user has it
            // open lets ShowContextMenu()'s own nested message pump dispatch this rebuild
            // mid-display, mutating items the menu is actively trying to create window handles
            // for. Closing first means there's nothing live left to race against.
            if (_menu.Visible) _menu.Close();

            ClearAndDisposeItems(_homeAssistantAreaMenu.DropDownItems);
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

        try
        {
            // Marshaling via _menuOwnerWindow (a stable Form that lives for the app's whole
            // lifetime) rather than _menu itself — _menu can be swapped out mid-session by
            // RebuildMenuInstance() (see ShowTrayMenuWithRetry's doc), which makes it an unreliable
            // target for a callback that was captured before a swap happened.
            if (_menuOwnerWindow.InvokeRequired) _menuOwnerWindow.Invoke(Rebuild); else Rebuild();
        }
        catch (Exception ex)
        {
            // Guards against a silent failure mode confirmed live 2026-08-04: this call previously
            // wasn't wrapped, and since RefreshHomeAssistantAreasAsync runs fire-and-forget from the
            // "Refresh areas/lights" click handler, an exception here would vanish with no log
            // anywhere — and everything after this point (including the light refresh below) would
            // just silently never run.
            Log.Error("Tray", $"Rebuilding area menu items threw ({ex.GetType().Name}): {ex.Message}");
            return;
        }

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

    private async Task RefreshHomeAssistantLightsAsync(string areaId)
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

        void Rebuild()
        {
            // See RefreshHomeAssistantAreasAsync's Rebuild() for why this closes the menu first.
            if (_menu.Visible) _menu.Close();

            RebuildLightActionMenu(_homeAssistantOnLightsMenu!, _config.HomeAssistant.HeadsetOnActions);
            RebuildLightActionMenu(_homeAssistantOffLightsMenu!, _config.HomeAssistant.HeadsetOffActions);
            RebuildLightActionMenu(_homeAssistantAfkLightsMenu!, _config.HomeAssistant.AfkActions);
        }

        try
        {
            // See RefreshHomeAssistantAreasAsync's Rebuild() invocation for why _menuOwnerWindow
            // is used instead of _menu here.
            if (_menuOwnerWindow.InvokeRequired) _menuOwnerWindow.Invoke(Rebuild); else Rebuild();
            Log.Info("Tray", $"Rebuilt light action menus for {_homeAssistantLightsInSelectedArea.Count} light(s).");
        }
        catch (Exception ex)
        {
            // Guards against the same silent-failure mode as RefreshHomeAssistantAreasAsync's
            // Rebuild() call above: this method can be called fire-and-forget (from the area
            // picker's Click handler) or awaited from RefreshHomeAssistantAreasAsync (itself also
            // fire-and-forget from a menu click), so an unwrapped exception here would vanish with
            // no log anywhere and the light submenus would just silently stay empty.
            Log.Error("Tray", $"Rebuilding light action menus threw ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Colored status icons shown before each light's name so its current setting for this
    /// trigger is visible without expanding the submenu. Drawn as small bitmaps rather than colored
    /// emoji (🟢/🔴/⚪) in the item text — confirmed live 2026-08-04 that WinForms' native menu text
    /// rendering (GDI/GDI+, not DirectWrite) doesn't render color emoji at all, falling back to a
    /// monochrome placeholder glyph instead. A real Image on ToolStripItem.Image always renders in
    /// full color regardless of font/rendering-path support. Built once and reused (never disposed)
    /// since they're tiny and live for the whole process.</summary>
    private static readonly Dictionary<LightAction, Image> LightActionIcons = new()
    {
        [LightAction.On] = CreateStatusDot(Color.LimeGreen),
        [LightAction.Off] = CreateStatusDot(Color.Firebrick),
        [LightAction.NoChange] = CreateStatusDot(Color.Gainsboro),
    };

    private static Bitmap CreateStatusDot(Color color)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 3, 3, 10, 10);
        return bmp;
    }

    /// <summary>Builds one "light name -> On/Off/No change" radio submenu per light in the
    /// currently-selected area, persisting the choice into the given tri-state map. Shared by all
    /// three trigger light lists (Headset On, Headset Off, AFK) — each map is independent, so the
    /// same light can have a different action per trigger.</summary>
    private void RebuildLightActionMenu(ToolStripMenuItem menu, Dictionary<string, string> actions)
    {
        ClearAndDisposeItems(menu.DropDownItems);
        foreach (var light in _homeAssistantLightsInSelectedArea)
        {
            var entityId = light.EntityId;
            var current = actions.GetValueOrDefault(entityId, LightAction.NoChange.ToConfigString()).ParseOrDefault();
            var lightMenu = new ToolStripMenuItem(light.DisplayName) { Image = LightActionIcons[current] };

            foreach (var option in new[] { LightAction.On, LightAction.Off, LightAction.NoChange })
            {
                var optionItem = new ToolStripMenuItem(option.ToConfigString()) { Checked = option == current };
                optionItem.Click += (_, _) =>
                {
                    actions[entityId] = option.ToConfigString();
                    _config.Save(_configPath);
                    lightMenu.Image = LightActionIcons[option];
                    foreach (ToolStripMenuItem sibling in lightMenu.DropDownItems)
                        sibling.Checked = sibling.Text == option.ToConfigString();
                    Log.Info("Tray", $"Home Assistant: {light.DisplayName} ({entityId}) set to {option} for this trigger.");
                };
                lightMenu.DropDownItems.Add(optionItem);
            }

            menu.DropDownItems.Add(lightMenu);
        }
    }

    /// <summary>Explicitly disposes every item in a ToolStrip collection (and any nested
    /// DropDownItems, recursively) before Clear() — added 2026-08-02 after a live crash
    /// ("Error creating window handle", Win32 1400) while showing the tray context menu. Clear()
    /// alone only removes items from the collection, it does not dispose them, so every one of
    /// these menus' periodic rebuilds (area list, per-light action submenus) was leaking native
    /// window handles for the life of the process.</summary>
    private static void ClearAndDisposeItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripDropDownItem dropDownItem)
                ClearAndDisposeItems(dropDownItem.DropDownItems);
            item.Dispose();
        }
        items.Clear();
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
