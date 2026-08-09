using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using VrSessionMonitor.Modules;
#if INCLUDE_HOME_ASSISTANT
using VrSessionMonitor.Modules.HomeAssistant;
#endif

namespace VrSessionMonitor.Tray;

/// <summary>
/// Replaces the old tray ContextMenuStrip entirely (see git history around 2026-08-09) — that
/// menu had a well-documented recurring crash (Win32 1400, "Error creating window handle") in
/// ToolStripDropDownMenu's internal scroll-arrow control, needing an explicit "skip status
/// updates while the menu is open" workaround plus a retry-with-fresh-instance recovery path.
/// A plain Form's controls don't share that bug, so none of that machinery is needed here —
/// status labels can update live even while this window is open and focused.
///
/// Left-click on the tray icon opens this on the Status tab; right-click opens it on the
/// Settings tab (see TrayApplicationContext's NotifyIcon.MouseUp wiring). A single instance is
/// created once and shown/hidden, never recreated, so it also serves as the stable long-lived
/// window other code can safely Invoke() against (replacing the old _menuOwnerWindow's role).
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly TrayApplicationContext _owner;
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
    private readonly FirmwareNotificationListener _firmwareNotify;
    private readonly SessionOrchestrator _orchestrator;

    private TabControl _tabs = null!;

    private Label _sessionStatusLabel = null!;
    private Label _headsetLabel = null!;
    private Label _trackerLabel = null!;
    private Label _peripheralLabel = null!;
    private Label _peripheralNextLabel = null!;
    private Label _steamVrLabel = null!;
    private Label _vrChatLabel = null!;
    private Button _restartVrChatButton = null!;
    private Label _firmwareLabel = null!;

#if INCLUDE_HOME_ASSISTANT
    private Label _homeAssistantStatusLabel = null!;
    private ComboBox _homeAssistantAreaCombo = null!;
    private List<AreaInfo> _homeAssistantAreas = new();
    private FlowLayoutPanel _homeAssistantOnLightsPanel = null!;
    private FlowLayoutPanel _homeAssistantOffLightsPanel = null!;
    private FlowLayoutPanel _homeAssistantAfkLightsPanel = null!;
    private List<LightInfo> _homeAssistantLightsInSelectedArea = new();

    private static readonly Dictionary<LightAction, Color> LightActionColors = new()
    {
        [LightAction.On] = Color.LimeGreen,
        [LightAction.Off] = Color.Firebrick,
        [LightAction.NoChange] = Color.Gainsboro,
    };
#endif

    public SettingsForm(
        TrayApplicationContext owner,
        MonitorConfig config,
        string configPath,
        HeadsetMonitor headset,
        SlimeVrTrackerMonitor trackers,
        SteamVrMonitor steamVr,
        VrChatMonitor vrChat,
        FaceTrackingMonitor faceTracking,
        EyeTrackingMonitor eyeTracking,
        VrcFaceTrackingLifecycleManager vrcFtLifecycle,
        VrcOscLifecycleManager vrcOscLifecycle,
        VirtualHereSRanipalLifecycleManager vhSranipalLifecycle,
        FirmwareNotificationListener firmwareNotify,
        SessionOrchestrator orchestrator)
    {
        _owner = owner;
        _config = config;
        _configPath = configPath;
        _headset = headset;
        _trackers = trackers;
        _steamVr = steamVr;
        _vrChat = vrChat;
        _faceTracking = faceTracking;
        _eyeTracking = eyeTracking;
        _vrcFtLifecycle = vrcFtLifecycle;
        _vrcOscLifecycle = vrcOscLifecycle;
        _vhSranipalLifecycle = vhSranipalLifecycle;
        _firmwareNotify = firmwareNotify;
        _orchestrator = orchestrator;

        Text = "VR Session Monitor";
        Width = 680;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 480);
        ShowInTaskbar = true;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildStatusTab());
#if INCLUDE_HOME_ASSISTANT
        HomeAssistantTabIndex = _tabs.TabPages.Count;
        _tabs.TabPages.Add(BuildHomeAssistantTab());
#endif
        SettingsTabIndex = _tabs.TabPages.Count;
        _tabs.TabPages.Add(BuildSettingsTab());
#if INCLUDE_HOME_ASSISTANT
        _tabs.TabPages.Add(BuildAutomationTab());
#endif
        _tabs.TabPages.Add(BuildAdvancedTab());

        var bottomBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8),
        };
        var exitButton = new Button { Text = "Exit", AutoSize = true };
        exitButton.Click += (_, _) => _owner.ExitApp();
        bottomBar.Controls.Add(exitButton);

        Controls.Add(_tabs);
        Controls.Add(bottomBar);

        // Closing the window (Alt+F4, the X button) just hides it — this is a tray-companion
        // window, not the app itself. Exit is the only thing that actually terminates the app.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>Index of the Home Assistant tab (only set/meaningful when INCLUDE_HOME_ASSISTANT
    /// is defined) — exposed so the tray icon's middle-click handler doesn't need to hardcode a
    /// tab position that shifts whenever tabs get reordered.</summary>
    public int HomeAssistantTabIndex { get; private set; } = -1;

    /// <summary>Index of the Settings tab — same reasoning as HomeAssistantTabIndex, since tab
    /// order has already shifted once (Home Assistant moved in front of it).</summary>
    public int SettingsTabIndex { get; private set; }

    /// <summary>Shows (or focuses, if already open) the window on the given tab index. Left-click
    /// opens Status (0), right-click opens SettingsTabIndex, middle-click opens
    /// HomeAssistantTabIndex (see TrayApplicationContext's NotifyIcon.MouseUp wiring).</summary>
    public void ShowOnTab(int tabIndex)
    {
        _tabs.SelectedIndex = tabIndex;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>Same as ShowOnTab, except clicking the same tray-icon button again while the
    /// window is already open on that exact tab closes it instead of just re-focusing it — lets
    /// the tray icon act as a toggle rather than only ever opening.</summary>
    public void ToggleOnTab(int tabIndex)
    {
        if (Visible && _tabs.SelectedIndex == tabIndex)
            Hide();
        else
            ShowOnTab(tabIndex);
    }

    // ───────────────────────────── Status tab ─────────────────────────────

    private TabPage BuildStatusTab()
    {
        var tab = new TabPage("Status");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

        _sessionStatusLabel = AddStatusRow(layout, "Status: idle");
        _headsetLabel = AddStatusRow(layout, "Headset: --");
        _trackerLabel = AddStatusRow(layout, "Trackers: --");
        _peripheralLabel = AddStatusRow(layout, "Eye/Face tracking: --");
        _peripheralNextLabel = AddStatusRow(layout, "");
        _peripheralNextLabel.Visible = false;
        _peripheralNextLabel.Margin = new Padding(20, 0, 3, 3);

        _steamVrLabel = AddStatusRow(layout, "SteamVR: --");

        _vrChatLabel = new Label { Text = "VRChat: --", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        _restartVrChatButton = new Button { Text = "Restart", AutoSize = true };
        _restartVrChatButton.Click += (_, _) => _ = _orchestrator.RestartVrChatAsync();
        layout.Controls.Add(_vrChatLabel);
        layout.Controls.Add(_restartVrChatButton);

        _firmwareLabel = AddStatusRow(layout, "Firmware self-heal: none yet");

        tab.Controls.Add(layout);
        return tab;
    }

#if INCLUDE_HOME_ASSISTANT
    // ───────────────────────────── Home Assistant tab ─────────────────────────────

    private TabPage BuildHomeAssistantTab()
    {
        var tab = new TabPage("Home Assistant");
        var haLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };

        _homeAssistantStatusLabel = new Label { Text = "Home Assistant: disconnected", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        var setupButton = new Button { Text = "Set up connection...", AutoSize = true };
        setupButton.Click += (_, _) => _ = _owner.SetUpHomeAssistantConnectionAsync();
        haLayout.Controls.Add(_homeAssistantStatusLabel);
        haLayout.Controls.Add(setupButton);

        var areaLabel = new Label { Text = "Area:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        _homeAssistantAreaCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        _homeAssistantAreaCombo.SelectedIndexChanged += OnHomeAssistantAreaSelected;
        var refreshButton = new Button { Text = "Refresh areas/lights", AutoSize = true };
        refreshButton.Click += (_, _) => _ = _owner.RefreshHomeAssistantAreasAsync();
        haLayout.Controls.Add(areaLabel);
        var areaRow = new FlowLayoutPanel { AutoSize = true };
        areaRow.Controls.Add(_homeAssistantAreaCombo);
        areaRow.Controls.Add(refreshButton);
        haLayout.Controls.Add(areaRow);

        var lightsTabs = new TabControl { Dock = DockStyle.Fill };
        _homeAssistantOnLightsPanel = BuildLightsSubTab(lightsTabs, "Headset On lights", _config.HomeAssistant.HeadsetOnActions);
        _homeAssistantAfkLightsPanel = BuildLightsSubTab(lightsTabs, "AFK lights", _config.HomeAssistant.AfkActions);
        _homeAssistantOffLightsPanel = BuildLightsSubTab(lightsTabs, "Headset Off lights", _config.HomeAssistant.HeadsetOffActions);

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.Controls.Add(haLayout, 0, 0);
        outer.Controls.Add(lightsTabs, 0, 1);

        tab.Controls.Add(outer);
        return tab;
    }
#endif

    private static Label AddStatusRow(TableLayoutPanel layout, string text)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        layout.Controls.Add(label);
        layout.SetColumnSpan(label, 2);
        return label;
    }

#if INCLUDE_HOME_ASSISTANT
    private FlowLayoutPanel BuildLightsSubTab(TabControl parent, string title, Dictionary<string, string> actions)
    {
        var page = new TabPage(title);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false };
        page.Controls.Add(panel);
        parent.TabPages.Add(page);
        panel.Tag = actions; // stashes which action map this panel writes to, for RebuildLightActionRows
        return panel;
    }

    private void OnHomeAssistantAreaSelected(object? sender, EventArgs e)
    {
        if (_homeAssistantAreaCombo.SelectedIndex < 0) return;
        var area = _homeAssistantAreas[_homeAssistantAreaCombo.SelectedIndex];
        if (area.AreaId == _config.HomeAssistant.SelectedAreaId) return;

        _config.HomeAssistant.SelectedAreaId = area.AreaId;
        _config.Save(_configPath);
        Log.Info("SettingsForm", $"Home Assistant area selected: {area.Name} ({area.AreaId})");
        _ = _owner.RefreshHomeAssistantLightsAsync(area.AreaId);
    }

    /// <summary>Called by TrayApplicationContext.RefreshHomeAssistantAreasAsync once the area list
    /// is fetched. Always call via Invoke if not already on the UI thread — see this form's own
    /// InvokeRequired-guarded public Update* methods for the pattern.</summary>
    public void SetHomeAssistantAreas(List<AreaInfo> areas)
    {
        void Apply()
        {
            _homeAssistantAreas = areas;
            _homeAssistantAreaCombo.Items.Clear();
            foreach (var area in areas) _homeAssistantAreaCombo.Items.Add(area.Name);
            var selectedIndex = areas.FindIndex(a => a.AreaId == _config.HomeAssistant.SelectedAreaId);
            if (selectedIndex >= 0) _homeAssistantAreaCombo.SelectedIndex = selectedIndex;
        }

        if (InvokeRequired) Invoke(Apply); else Apply();
    }

    /// <summary>Called by TrayApplicationContext.RefreshHomeAssistantLightsAsync once the light
    /// list for the selected area is fetched.</summary>
    public void SetHomeAssistantLights(List<LightInfo> lights)
    {
        void Apply()
        {
            _homeAssistantLightsInSelectedArea = lights;
            RebuildLightActionRows(_homeAssistantOnLightsPanel, _config.HomeAssistant.HeadsetOnActions);
            RebuildLightActionRows(_homeAssistantOffLightsPanel, _config.HomeAssistant.HeadsetOffActions);
            RebuildLightActionRows(_homeAssistantAfkLightsPanel, _config.HomeAssistant.AfkActions);
        }

        if (InvokeRequired) Invoke(Apply); else Apply();
    }

    private void RebuildLightActionRows(FlowLayoutPanel panel, Dictionary<string, string> actions)
    {
        panel.SuspendLayout();
        foreach (Control c in panel.Controls) c.Dispose();
        panel.Controls.Clear();

        foreach (var light in _homeAssistantLightsInSelectedArea)
        {
            var entityId = light.EntityId;
            var current = actions.GetValueOrDefault(entityId, LightAction.NoChange.ToConfigString()).ParseOrDefault();

            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var dot = new PictureBox { Image = CreateStatusDot(LightActionColors[current]), Width = 16, Height = 16, Margin = new Padding(3, 6, 3, 3) };
            var nameLabel = new Label { Text = light.DisplayName, AutoSize = true, Margin = new Padding(3, 6, 8, 3), MinimumSize = new Size(160, 0) };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            foreach (var option in new[] { LightAction.On, LightAction.Off, LightAction.NoChange })
                combo.Items.Add(option.ToConfigString());
            combo.SelectedIndex = current switch { LightAction.On => 0, LightAction.Off => 1, _ => 2 };
            combo.SelectedIndexChanged += (_, _) =>
            {
                var chosen = combo.SelectedIndex switch { 0 => LightAction.On, 1 => LightAction.Off, _ => LightAction.NoChange };
                actions[entityId] = chosen.ToConfigString();
                _config.Save(_configPath);
                dot.Image = CreateStatusDot(LightActionColors[chosen]);
                Log.Info("SettingsForm", $"Home Assistant: {light.DisplayName} ({entityId}) set to {chosen} for this trigger.");
            };

            row.Controls.Add(dot);
            row.Controls.Add(nameLabel);
            row.Controls.Add(combo);
            panel.Controls.Add(row);
        }

        panel.ResumeLayout();
    }

    private static Bitmap CreateStatusDot(Color color)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 3, 3, 10, 10);
        return bmp;
    }
#endif

    /// <summary>Refreshes every polling-based status label. Called from TrayApplicationContext's
    /// existing 5s Timer.Tick (UI thread) — unlike the event-driven Update* methods below, this
    /// needs no InvokeRequired guard for that reason, matching the old UpdateStatusItems'
    /// contract. Unlike the old menu version, there's no need to skip while the window is visible
    /// — Label/Button controls don't share ToolStripDropDownMenu's handle-creation bug.</summary>
    public void RefreshStatus()
    {
        _headsetLabel.Text = $"Headset: {(_headset.IsOnline ? "online" : "offline")}";
        _trackerLabel.Text = $"Trackers: {_trackers.Summarize()}";

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
        foreach (var cam in cams.Where(c => !c.Online || !c.Streaming))
            parts.Add(cam.Online ? $"{cam.Camera.Name} not streaming" : $"{cam.Camera.Name} offline");
        _peripheralLabel.Text = parts.Count == 0
            ? $"Eye/Face tracking: all running ({p.ModuleProcessCount} module(s), {cams.Count(c => c.Streaming)}/{cams.Count} cameras streaming)"
            : $"Eye/Face tracking: {string.Join(", ", parts)}";

        var pending = new List<string>();
        pending.AddRange(_eyeTracking.DescribePendingActions());
        if (_faceTracking.DescribePendingAction() is string faceTrackingPending) pending.Add(faceTrackingPending);
        if (_vrcFtLifecycle.DescribePendingAction() is string vrcFtPending) pending.Add(vrcFtPending);
        if (_vhSranipalLifecycle.DescribePendingAction() is string vhSranipalPending) pending.Add(vhSranipalPending);
        _peripheralNextLabel.Visible = pending.Count > 0;
        if (pending.Count > 0)
            _peripheralNextLabel.Text = $"Next: {string.Join(", ", pending)}";

        var sv = _steamVr.Current;
        if (sv.VrServerRunning && sv.VrMonitorRunning && sv.VrCompositorRunning)
            _steamVrLabel.Text = "SteamVR: running";
        else if (!sv.VrServerRunning && !sv.VrMonitorRunning && !sv.VrCompositorRunning)
            _steamVrLabel.Text = "SteamVR: not running";
        else
        {
            var down = new List<string>();
            if (!sv.VrServerRunning) down.Add("vrserver");
            if (!sv.VrMonitorRunning) down.Add("vrmonitor");
            if (!sv.VrCompositorRunning) down.Add("vrcompositor");
            _steamVrLabel.Text = $"SteamVR: partial (down: {string.Join(", ", down)})";
        }
        if (_steamVr.DescribePendingAction() is string steamVrPending)
            _steamVrLabel.Text += $" — next: {steamVrPending}";

        _vrChatLabel.Text = $"VRChat: {(_vrChat.Current.Running ? "running" : "not running")}";
        if (_vrcOscLifecycle.DescribePendingAction() is string vrcOscPending)
            _vrChatLabel.Text += $" — next: {vrcOscPending}";

        RefreshFirmwareLabel();
#if INCLUDE_HOME_ASSISTANT
        _homeAssistantStatusLabel.Text = $"Home Assistant: {(_owner.HomeAssistantIsConnected ? "connected" : "disconnected")}";
#endif
    }

    /// <summary>Fires from SessionOrchestrator's own StateChanged event, not necessarily the UI
    /// thread.</summary>
    public void UpdateSessionStatus(string state)
    {
        var text = $"Status: {state}";
        if (InvokeRequired) Invoke(() => _sessionStatusLabel.Text = text);
        else _sessionStatusLabel.Text = text;
    }

    /// <summary>Fires from HeadsetMonitor's own background polling loop, not the UI thread.</summary>
    public void UpdateHeadsetStatus(bool isOnline)
    {
        var text = $"Headset: {(isOnline ? "online" : "offline")}";
        if (InvokeRequired) Invoke(() => _headsetLabel.Text = text);
        else _headsetLabel.Text = text;
    }

    /// <summary>Called both from RefreshStatus (UI thread) and directly off
    /// FirmwareNotificationListener.NotificationReceived (its own background receive loop) for an
    /// immediate refresh rather than waiting up to 5s for the next timer tick.</summary>
    public void RefreshFirmwareLabel()
    {
        void Apply()
        {
            _firmwareLabel.Text = _firmwareNotify.LastEventAtUtc is DateTime at
                ? $"Firmware self-heal: {_firmwareNotify.LastEventSummary} ({FormatAgo(DateTime.UtcNow - at)} ago)"
                : "Firmware self-heal: none yet";
        }

        if (InvokeRequired) Invoke(Apply); else Apply();
    }

    private static string FormatAgo(TimeSpan span) =>
        span.TotalMinutes < 1 ? "just now" :
        span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" :
        $"{(int)span.TotalHours}h{span.Minutes}m";

    // ───────────────────────────── Settings tab ─────────────────────────────

    private TabPage BuildSettingsTab()
    {
        var tab = new TabPage("Settings");

        // Actions pinned in their own right-hand column (not part of the scrollable area below)
        // so they're always visible without scrolling past the checkboxes/groupboxes to reach them.
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoScroll = true,
            WrapContents = false,
            Padding = new Padding(10),
        };

        layout.Controls.Add(BuildCheckbox("Auto-start VRChat", _config.SessionFlow.AutoLaunchVrChat,
            v => { _config.SessionFlow.AutoLaunchVrChat = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Auto-start OVR Toolkit", _config.SessionFlow.AutoLaunchOvrToolkit,
            v => { _config.SessionFlow.AutoLaunchOvrToolkit = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Auto-start/stop Baballonia", _config.BaballoniaLifecycle.Enabled,
            v => { _config.BaballoniaLifecycle.Enabled = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Auto-start/stop VRCFaceTracking", _config.VrcFaceTrackingLifecycle.Enabled,
            v => { _config.VrcFaceTrackingLifecycle.Enabled = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Auto-start/stop VRCOSC", _config.VrcOscLifecycle.Enabled,
            v => { _config.VrcOscLifecycle.Enabled = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Start with Windows", WindowsStartup.IsEnabled(),
            v => WindowsStartup.SetEnabled(v)));

        var bgGroup = new GroupBox { Text = "VRChat background mode", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(380, 0) };
        var bgLayout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        bgLayout.Controls.Add(BuildCheckbox("Run VRChat in the background (small, minimized)", _config.SessionFlow.VrChatBackgroundMode,
            v => { _config.SessionFlow.VrChatBackgroundMode = v; _config.Save(_configPath); }));
        bgLayout.Controls.Add(BuildNumericRow("Width", _config.SessionFlow.VrChatBackgroundWidth,
            v => { _config.SessionFlow.VrChatBackgroundWidth = v; _config.Save(_configPath); }));
        bgLayout.Controls.Add(BuildNumericRow("Height", _config.SessionFlow.VrChatBackgroundHeight,
            v => { _config.SessionFlow.VrChatBackgroundHeight = v; _config.Save(_configPath); }));
        bgLayout.Controls.Add(BuildNumericRow("Monitor", _config.SessionFlow.VrChatBackgroundMonitor,
            v => { _config.SessionFlow.VrChatBackgroundMonitor = v; _config.Save(_configPath); }));
        bgGroup.Controls.Add(bgLayout);
        layout.Controls.Add(bgGroup);

        var netGroup = new GroupBox { Text = "Network", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(380, 0) };
        var netLayout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        netLayout.Controls.Add(BuildTextRow("Headset IP", _config.Network.HeadsetIp,
            v => { _config.Network.HeadsetIp = v; _config.Save(_configPath); }));
        netLayout.Controls.Add(BuildTextRow("Headset IP (secondary, optional)", _config.Network.HeadsetIpSecondary,
            v => { _config.Network.HeadsetIpSecondary = v; _config.Save(_configPath); }));
        foreach (var cam in _config.EyeCameras)
        {
            var camRef = cam;
            netLayout.Controls.Add(BuildTextRow($"{camRef.Name} camera IP", camRef.Ip,
                v => { camRef.Ip = v; _config.Save(_configPath); }));
        }
        netGroup.Controls.Add(netLayout);
        layout.Controls.Add(netGroup);

        var actionsGroup = new GroupBox { Text = "Actions", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var actionsLayout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        actionsLayout.Controls.Add(BuildActionButton("Force run now", () => _ = _orchestrator.RunSessionStartAsync()));
        actionsLayout.Controls.Add(BuildActionButton("Restart face-tracking pipeline", () => _ = _owner.RestartFaceTrackingPipelineAsync()));
        actionsLayout.Controls.Add(BuildActionButton("Recheck trackers", () => _ = _trackers.CheckAllAsync()));
        actionsLayout.Controls.Add(BuildActionButton("Auto-detect headset/trackers/cameras", () => _ = _owner.AutoDetectAsync()));
        actionsLayout.Controls.Add(BuildActionButton("Open logs folder", () => _owner.OpenLogsFolder()));
        actionsGroup.Controls.Add(actionsLayout);

        outer.Controls.Add(layout, 0, 0);
        outer.Controls.Add(actionsGroup, 1, 0);
        tab.Controls.Add(outer);
        return tab;
    }

#if INCLUDE_HOME_ASSISTANT
    // ───────────────────────────── Automation tab ─────────────────────────────

    /// <summary>Editable list backing VrChatGroupAutomationMonitor's watch list - see that
    /// class's doc for the actual detection/OSC-send mechanism. Changes here are saved to config
    /// immediately but only take effect on restart, matching this app's general "no config
    /// hot-reload" convention (the monitor reads Groups/Enabled once, at Start()).</summary>
    private TabPage BuildAutomationTab()
    {
        var tab = new TabPage("Automation");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel { AutoSize = true };
        header.Controls.Add(BuildCheckbox("Enabled", _config.VrChatGroupAutomation.Enabled,
            v => { _config.VrChatGroupAutomation.Enabled = v; _config.Save(_configPath); }));
        header.Controls.Add(new Label
        {
            Text = "Toggles an avatar OSC bool parameter true while you're in a matching VRChat\n" +
                   "group's instance, false otherwise. Detected from VRChat's own log file - no\n" +
                   "login needed. Restart the app after changing this list for it to take effect.\n" +
                   "Tick Represent to also set that group as your VRChat represented group while\n" +
                   "you're in it (needs VRCX running and logged in).",
            AutoSize = true,
            Margin = new Padding(12, 3, 3, 3),
        });
        layout.Controls.Add(header, 0, 0);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false,
        };
        var groupCol = new DataGridViewTextBoxColumn { HeaderText = "Group ID (grp_...)", DataPropertyName = nameof(GroupAutomationEntry.GroupId), FillWeight = 40 };
        grid.Columns.Add(groupCol);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Display name", DataPropertyName = nameof(GroupAutomationEntry.DisplayName), FillWeight = 30 });
        var paramCol = new DataGridViewTextBoxColumn { HeaderText = "OSC parameter name", DataPropertyName = nameof(GroupAutomationEntry.ParamName), FillWeight = 30 };
        grid.Columns.Add(paramCol);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Represent", DataPropertyName = nameof(GroupAutomationEntry.Represent), FillWeight = 15 });

        var binding = new BindingList<GroupAutomationEntry>(_config.VrChatGroupAutomation.Groups);
        grid.DataSource = binding;

        void SaveGroups() => _config.Save(_configPath);
        grid.CellEndEdit += (_, _) => SaveGroups();
        grid.UserDeletedRow += (_, _) => SaveGroups();
        grid.RowValidated += (_, _) => SaveGroups();

        var vrchatLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"Low\VRChat\VRChat";
        // ScanGroupIds/ScanOscBoolParams read every VRChat output_log_*.txt and every avatar OSC
        // config JSON — tens–hundreds of MB for a heavy user, which would freeze the Settings window
        // if done synchronously here. Build the two collections EMPTY and wire them to the grid's
        // EditingControlShowing handler and the fallback textbox now (they're referenced by object,
        // so late population is fine — autocomplete is pure convenience). Then run both scans on a
        // background thread and marshal back to populate them: AutoCompleteStringCollection.AddRange
        // must run on the UI/STA thread, hence the BeginInvoke. Never block the constructor here.
        var groupIdSuggestions = new AutoCompleteStringCollection();
        var oscParamSuggestions = new AutoCompleteStringCollection();
        _ = Task.Run(() =>
        {
            try
            {
                var groupIds = AutomationSuggestionSources.ScanGroupIds(vrchatLow).ToArray();
                var oscParams = AutomationSuggestionSources.ScanOscBoolParams(Path.Combine(vrchatLow, "OSC")).ToArray();
                BeginInvoke((Action)(() =>
                {
                    groupIdSuggestions.AddRange(groupIds);
                    oscParamSuggestions.AddRange(oscParams);
                }));
            }
            catch (Exception ex)
            {
                Log.Debug("SettingsForm", $"Automation autocomplete scan failed: {ex.Message}");
            }
        });

        var fallbackRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        fallbackRow.Controls.Add(new Label { Text = "Fallback represented group (blank = clear):", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        var fallbackBox = new TextBox
        {
            Text = _config.VrChatGroupAutomation.FallbackRepresentGroupId,
            Width = 260,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.CustomSource,
            AutoCompleteCustomSource = groupIdSuggestions,
        };
        fallbackBox.Leave += (_, _) =>
        {
            _config.VrChatGroupAutomation.FallbackRepresentGroupId = fallbackBox.Text.Trim();
            _config.Save(_configPath);
        };
        fallbackRow.Controls.Add(fallbackBox);
        header.Controls.Add(fallbackRow);

        grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is not TextBox tb) return;
            var col = grid.CurrentCell?.OwningColumn;
            if (col == groupCol)
            {
                tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteCustomSource = groupIdSuggestions;
            }
            else if (col == paramCol)
            {
                tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteCustomSource = oscParamSuggestions;
            }
            else
            {
                // Display name column: the editing TextBox is reused across columns, so explicitly
                // clear autocomplete or it carries the previous column's source.
                tb.AutoCompleteMode = AutoCompleteMode.None;
                tb.AutoCompleteCustomSource = null;
            }
        };

        layout.Controls.Add(grid, 0, 1);

        var removeSelectedButton = new Button { Text = "Remove selected row(s)", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        removeSelectedButton.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.SelectedRows)
            {
                if (!row.IsNewRow) binding.RemoveAt(row.Index);
            }
            SaveGroups();
        };
        layout.Controls.Add(removeSelectedButton, 0, 2);

        tab.Controls.Add(layout);
        return tab;
    }
#endif

    private static CheckBox BuildCheckbox(string text, bool initial, Action<bool> onChange)
    {
        var box = new CheckBox { Text = text, Checked = initial, AutoSize = true, Margin = new Padding(3, 6, 3, 6) };
        box.CheckedChanged += (_, _) => onChange(box.Checked);
        return box;
    }

    private static FlowLayoutPanel BuildNumericRow(string label, int initial, Action<int> onChange)
    {
        var row = new FlowLayoutPanel { AutoSize = true };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 140, Margin = new Padding(3, 6, 3, 3) });
        var numeric = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = initial, Width = 100 };
        numeric.ValueChanged += (_, _) => onChange((int)numeric.Value);
        row.Controls.Add(numeric);
        return row;
    }

    private static FlowLayoutPanel BuildTextRow(string label, string initial, Action<string> onChange)
    {
        var row = new FlowLayoutPanel { AutoSize = true };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 200, Margin = new Padding(3, 6, 3, 3) });
        var text = new TextBox { Text = initial, Width = 200 };
        text.Leave += (_, _) => onChange(text.Text);
        row.Controls.Add(text);
        return row;
    }

    private static Button BuildActionButton(string text, Action onClick)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ───────────────────────────── Advanced tab ─────────────────────────────

    private TabPage BuildAdvancedTab()
    {
        var tab = new TabPage("Advanced");
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), WrapContents = false };

        layout.Controls.Add(new Label
        {
            Text = "Everything not covered by the Settings tab lives in appsettings.json directly.\n" +
                   "This opens it in your default editor. VrSessionMonitor doesn't hot-reload config —\n" +
                   "restart the app after making changes here for them to take effect.",
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 12),
        });

        var openButton = new Button { Text = "Open appsettings.json", AutoSize = true };
        openButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = _configPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("SettingsForm", $"Failed to open {_configPath}: {ex.Message}");
                MessageBox.Show(this, $"Couldn't open the config file: {ex.Message}", "VR Session Monitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        layout.Controls.Add(openButton);

        tab.Controls.Add(layout);
        return tab;
    }
}
