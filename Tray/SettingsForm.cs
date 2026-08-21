using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Optimizations;
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
    private readonly SlimeVrLifecycleManager _slimeLifecycle;
    private readonly FirmwareNotificationListener _firmwareNotify;
    private readonly SessionOrchestrator _orchestrator;
    private readonly OptimizationsManager _optimizations;

    private TabControl _tabs = null!;
    private TabPage _optimizationsTabPage = null!;
    private bool _refreshingOptimizationsTab;

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

#endif

    private readonly Dictionary<string, Label> _optimizationStatusLabels = new();
    private readonly Dictionary<string, Button> _optimizationFixButtons = new();

    private ListBox _appsList = null!;
    private Panel _appDetailPanel = null!;
    private ManagedApp? _selectedApp;

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
        SlimeVrLifecycleManager slimeLifecycle,
        FirmwareNotificationListener firmwareNotify,
        SessionOrchestrator orchestrator,
        OptimizationsManager optimizations)
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
        _slimeLifecycle = slimeLifecycle;
        _firmwareNotify = firmwareNotify;
        _orchestrator = orchestrator;
        _optimizations = optimizations;

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
        AppsTabIndex = _tabs.TabPages.Count;
        _tabs.TabPages.Add(BuildAppsTab());
        _tabs.TabPages.Add(BuildAdvancedTab());
        _optimizationsTabPage = BuildOptimizationsTab();
        _tabs.TabPages.Add(_optimizationsTabPage);

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

    /// <summary>Index of the Apps tab — same reasoning as HomeAssistantTabIndex/SettingsTabIndex,
    /// since tab order shifts as tabs are added.</summary>
    public int AppsTabIndex { get; private set; }

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
            var setting = LightSetting.Parse(actions.GetValueOrDefault(entityId));

            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var dot = new PictureBox { Image = CreateStatusDot(DotColorFor(setting)), Width = 16, Height = 16, Margin = new Padding(3, 6, 3, 3) };
            var nameLabel = new Label { Text = light.DisplayName, AutoSize = true, Margin = new Padding(3, 6, 8, 3), MinimumSize = new Size(160, 0) };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            foreach (var option in new[] { LightAction.On, LightAction.Off, LightAction.NoChange })
                combo.Items.Add(option.ToConfigString());
            combo.SelectedIndex = setting.Action switch { LightAction.On => 0, LightAction.Off => 1, _ => 2 };

            var colourButton = new Button { Text = "Colour...", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
            var brightness = new NumericUpDown { Minimum = 1, Maximum = 100, Value = setting.BrightnessPct, Width = 60, Margin = new Padding(3, 4, 3, 3) };
            var pctLabel = new Label { Text = "%", AutoSize = true, Margin = new Padding(0, 7, 6, 3) };

            // Colour/brightness only mean anything when the light is being turned ON — an Off or
            // NoChange row shouldn't imply they'll be applied.
            void SyncEnabled()
            {
                var isOn = combo.SelectedIndex == 0;
                colourButton.Enabled = isOn;
                brightness.Enabled = isOn;
                pctLabel.Enabled = isOn;
            }

            void Persist(LightSetting s)
            {
                setting = s;
                actions[entityId] = s.ToConfigString();
                _config.Save(_configPath);
                dot.Image = CreateStatusDot(DotColorFor(s));
                colourButton.BackColor = s.Rgb is int c ? Color.FromArgb(c | unchecked((int)0xFF000000)) : SystemColors.Control;
                colourButton.ForeColor = s.Rgb is int c2 && IsDark(c2) ? Color.White : SystemColors.ControlText;
                SyncEnabled();
            }

            combo.SelectedIndexChanged += (_, _) =>
            {
                var chosen = combo.SelectedIndex switch { 0 => LightAction.On, 1 => LightAction.Off, _ => LightAction.NoChange };
                Persist(setting with { Action = chosen });
                Log.Info("SettingsForm", $"Home Assistant: {light.DisplayName} ({entityId}) set to {chosen} for this trigger.");
            };

            colourButton.Click += (_, _) =>
            {
                using var dialog = new ColorDialog
                {
                    FullOpen = true,
                    Color = setting.Rgb is int c ? Color.FromArgb(c | unchecked((int)0xFF000000)) : Color.White,
                };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var rgb = (dialog.Color.R << 16) | (dialog.Color.G << 8) | dialog.Color.B;
                Persist(setting with { Rgb = rgb });
                Log.Info("SettingsForm", $"Home Assistant: {light.DisplayName} ({entityId}) colour set to #{rgb:X6}.");
            };

            brightness.ValueChanged += (_, _) => Persist(setting with { BrightnessPct = (int)brightness.Value });

            row.Controls.Add(dot);
            row.Controls.Add(nameLabel);
            row.Controls.Add(combo);
            row.Controls.Add(colourButton);
            row.Controls.Add(brightness);
            row.Controls.Add(pctLabel);
            panel.Controls.Add(row);

            // Paints the initial swatch/enabled state without re-saving config on load.
            colourButton.BackColor = setting.Rgb is int initial ? Color.FromArgb(initial | unchecked((int)0xFF000000)) : SystemColors.Control;
            colourButton.ForeColor = setting.Rgb is int initial2 && IsDark(initial2) ? Color.White : SystemColors.ControlText;
            SyncEnabled();
        }

        panel.ResumeLayout();
    }

    /// <summary>
    /// The dot previews what the light will actually do, so a tab reads as a scene at a glance:
    /// the configured colour scaled by the configured brightness. With no colour set that scaling
    /// falls out as plain greyscale — white at 100%, mid-grey at 50%, near-black at 1% — so
    /// intensity is still visible. Off is black, and NoChange draws nothing at all (null).
    /// </summary>
    private static Color? DotColorFor(LightSetting setting)
    {
        if (setting.Action == LightAction.NoChange) return null;
        if (setting.Action == LightAction.Off) return Color.Black;

        // On: base colour (or white when unset) dimmed by brightness.
        var (r, g, b) = setting.Rgb is int rgb
            ? ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF)
            : (255, 255, 255);

        var scale = setting.BrightnessPct / 100.0;
        return Color.FromArgb((int)(r * scale), (int)(g * scale), (int)(b * scale));
    }

    /// <summary>Perceived-luminance test (Rec. 601 weights) so the swatch button's label stays
    /// readable against whatever colour was picked.</summary>
    private static bool IsDark(int rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return (0.299 * r + 0.587 * g + 0.114 * b) < 140;
    }

    /// <summary>Draws the row's status dot. A null colour means "nothing to show" (NoChange) and
    /// yields a fully transparent bitmap rather than a hidden control — the space is kept so the
    /// light names below it stay aligned in the column instead of jumping left.
    /// The black outline keeps a black (Off) or dimmed dot visible against the window background.</summary>
    private static Bitmap CreateStatusDot(Color? color)
    {
        var bmp = new Bitmap(16, 16);
        if (color is not Color fill) return bmp; // transparent — NoChange

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, 3, 3, 10, 10);
        using var pen = new Pen(Color.Black, 1f);
        g.DrawEllipse(pen, 3, 3, 10, 10);
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
        if (_slimeLifecycle.DescribePendingAction() is string slimePending) pending.Add(slimePending);
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
        var overlayRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        overlayRow.Controls.Add(new Label { Text = "VR overlay to auto-start:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        var overlayCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, FormattingEnabled = true };
        overlayCombo.Format += (_, e) =>
        {
            if (e.ListItem is VrOverlayChoice c)
                e.Value = c switch
                {
                    VrOverlayChoice.None => "None",
                    VrOverlayChoice.OvrToolkit => "OVR Toolkit",
                    VrOverlayChoice.XSOverlay => "XSOverlay",
                    _ => c.ToString(),
                };
        };
        overlayCombo.Items.AddRange(new object[] { VrOverlayChoice.None, VrOverlayChoice.OvrToolkit, VrOverlayChoice.XSOverlay });
        overlayCombo.SelectedItem = _config.SessionFlow.VrOverlay;
        overlayCombo.SelectedIndexChanged += (_, _) =>
        {
            if (overlayCombo.SelectedItem is VrOverlayChoice choice)
            {
                _config.SessionFlow.VrOverlay = choice;
                _config.Save(_configPath);
            }
        };
        overlayRow.Controls.Add(overlayCombo);
        layout.Controls.Add(overlayRow);
        layout.Controls.Add(BuildCheckbox("Auto-start/stop Baballonia",
            _config.GetApp("baballonia")?.Enabled ?? _config.BaballoniaLifecycle.Enabled,
            v =>
            {
                // Write both: ManagedApp is what EyeTracking's lifecycle logic reads, the legacy
                // property is kept in sync so a hand-edited or pre-migration config still behaves
                // predictably.
                if (_config.GetApp("baballonia") is { } app) app.Enabled = v;
                _config.BaballoniaLifecycle.Enabled = v;
                _config.Save(_configPath);
            }));
        layout.Controls.Add(BuildCheckbox("Auto-start/stop VRCFaceTracking",
            _config.GetApp("vrcfacetracking")?.Enabled ?? _config.VrcFaceTrackingLifecycle.Enabled,
            v =>
            {
                if (_config.GetApp("vrcfacetracking") is { } app) app.Enabled = v;
                _config.VrcFaceTrackingLifecycle.Enabled = v;
                _config.Save(_configPath);
            }));
        layout.Controls.Add(BuildCheckbox("Auto-heal face tracking (restart SRanipal + VRCFaceTracking when it stalls/freezes)", _config.FaceTrackingAutoFix.Enabled,
            v => { _config.FaceTrackingAutoFix.Enabled = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Restart face tracking when the Vive camera attaches late", _config.FaceTrackingAutoFix.RestartOnCameraReappear,
            v => { _config.FaceTrackingAutoFix.RestartOnCameraReappear = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Auto-start/stop VRCOSC",
            _config.GetApp("vrcosc")?.Enabled ?? _config.VrcOscLifecycle.Enabled,
            v =>
            {
                if (_config.GetApp("vrcosc") is { } app) app.Enabled = v;
                _config.VrcOscLifecycle.Enabled = v;
                _config.Save(_configPath);
            }));
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
                   "group's instance, false otherwise. Type a group by name, short code, or grp_ ID\n" +
                   "in the Group column — names/codes autocomplete and resolve to the ID for you\n" +
                   "(that needs VRCX running + logged in; grp_ IDs also come from VRChat's logs).\n" +
                   "Restart the app after changing this list for it to take effect. Tick Represent to\n" +
                   "also set that group as your VRChat represented group while you're in it.",
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
        var groupCol = new DataGridViewTextBoxColumn { HeaderText = "Group (name, code, or grp_ ID)", DataPropertyName = nameof(GroupAutomationEntry.GroupId), FillWeight = 40 };
        grid.Columns.Add(groupCol);
        var nameCol = new DataGridViewTextBoxColumn { HeaderText = "Display name", DataPropertyName = nameof(GroupAutomationEntry.DisplayName), FillWeight = 30 };
        grid.Columns.Add(nameCol);
        var paramCol = new DataGridViewTextBoxColumn { HeaderText = "OSC parameter name", DataPropertyName = nameof(GroupAutomationEntry.ParamName), FillWeight = 30 };
        grid.Columns.Add(paramCol);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Represent", DataPropertyName = nameof(GroupAutomationEntry.Represent), FillWeight = 15 });

        var binding = new BindingList<GroupAutomationEntry>(_config.VrChatGroupAutomation.Groups);
        grid.DataSource = binding;

        void SaveGroups() => _config.Save(_configPath);

        // The Group column accepts a name, short code, or grp_ id. When a committed value matches one
        // of your groups by name/code (populated below from the VRChat API via VRCX), resolve it to
        // the grp_ id in place and fill Display name if it's blank — so you never have to paste a raw
        // grp_ id. A value already starting with grp_, or one we don't recognize, is left untouched.
        var groupsByKey = new Dictionary<string, VrcGroupInfo>(StringComparer.OrdinalIgnoreCase);
        grid.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == groupCol.Index)
            {
                var cell = grid.Rows[e.RowIndex].Cells[groupCol.Index];
                var typed = (cell.Value as string)?.Trim();
                if (!string.IsNullOrEmpty(typed) && !typed.StartsWith("grp_", StringComparison.Ordinal)
                    && groupsByKey.TryGetValue(typed, out var g))
                {
                    cell.Value = g.Id;
                    var nameCell = grid.Rows[e.RowIndex].Cells[nameCol.Index];
                    if (string.IsNullOrWhiteSpace(nameCell.Value as string))
                        nameCell.Value = g.Name;
                }
            }
            SaveGroups();
        };
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
        _ = Task.Run(async () =>
        {
            try
            {
                var groupIds = AutomationSuggestionSources.ScanGroupIds(vrchatLow).ToArray();
                var oscParams = AutomationSuggestionSources.ScanOscBoolParams(Path.Combine(vrchatLow, "OSC")).ToArray();

                // Also fetch the groups you actually belong to (name + short code + grp_ id) so the
                // Group column can autocomplete by name/code and resolve to the id. Needs VRCX running
                // and logged in; best-effort — no session just means those name entries are absent and
                // the log-mined grp_ ids still work.
                IReadOnlyList<VrcGroupInfo> groups = Array.Empty<VrcGroupInfo>();
                try
                {
                    using var represent = new VrcRepresentClient(new VrcxSessionProvider());
                    if (represent.HasSession)
                        groups = await represent.GetMyGroupsAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Debug("SettingsForm", $"Group name fetch failed: {ex.Message}"); }

                var nameAndCodeSuggestions = groups
                    .SelectMany(g => new[] { g.Name, g.ShortCode })
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .ToArray();

                BeginInvoke((Action)(() =>
                {
                    groupIdSuggestions.AddRange(groupIds);
                    groupIdSuggestions.AddRange(nameAndCodeSuggestions);
                    oscParamSuggestions.AddRange(oscParams);
                    foreach (var g in groups)
                    {
                        if (!string.IsNullOrWhiteSpace(g.Name)) groupsByKey[g.Name] = g;
                        if (!string.IsNullOrWhiteSpace(g.ShortCode)) groupsByKey[g.ShortCode!] = g;
                        groupsByKey[g.Id] = g;
                    }
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

    // ───────────────────────────── Apps tab ─────────────────────────────

    /// <summary>Master–detail editor for ManagedApps: the list on the left, the selected app's
    /// launch and window settings on the right. Replaces the per-app checkbox pile that used to
    /// live in the Settings tab, which grew a row per app and didn't scale — see
    /// docs/superpowers/specs/2026-08-21-managed-apps-window-control-design.md.</summary>
    private TabPage BuildAppsTab()
    {
        var tab = new TabPage("Apps");

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

        // ── left: the list plus its reorder/add/remove controls
        var leftPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _appsList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        _appsList.SelectedIndexChanged += (_, _) =>
        {
            _selectedApp = _appsList.SelectedIndex >= 0 && _appsList.SelectedIndex < SortedApps().Count
                ? SortedApps()[_appsList.SelectedIndex]
                : null;
            RebuildAppDetailPanel();
        };
        leftPanel.Controls.Add(_appsList, 0, 0);

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        listButtons.Controls.Add(BuildActionButton("Add", AddManagedApp));
        listButtons.Controls.Add(BuildActionButton("Remove", RemoveSelectedManagedApp));
        listButtons.Controls.Add(BuildActionButton("Up", () => MoveSelectedManagedApp(-1)));
        listButtons.Controls.Add(BuildActionButton("Down", () => MoveSelectedManagedApp(1)));
        leftPanel.Controls.Add(listButtons, 0, 1);

        // ── right: the detail panel, rebuilt on each selection change
        _appDetailPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 0, 0, 0) };

        split.Controls.Add(leftPanel, 0, 0);
        split.Controls.Add(_appDetailPanel, 1, 0);
        tab.Controls.Add(split);

        RefreshAppsList();
        return tab;
    }

    /// <summary>Apps in display order. Order is a user-editable int, so sort rather than trusting
    /// the raw list order — a hand-edited config can easily have gaps or duplicates.</summary>
    private List<ManagedApp> SortedApps() =>
        _config.ManagedApps.OrderBy(a => a.Order).ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Repopulates the list, preserving the selected app by Id where possible — index
    /// alone is wrong after a reorder or removal.</summary>
    private void RefreshAppsList()
    {
        var previouslySelectedId = _selectedApp?.Id;

        _appsList.BeginUpdate();
        _appsList.Items.Clear();
        var apps = SortedApps();
        foreach (var app in apps)
        {
            var enabled = app.Enabled ? "" : "  (disabled)";
            var rules = DescribeWindowRules(app);
            _appsList.Items.Add($"{app.DisplayName}{enabled}{rules}");
        }
        _appsList.EndUpdate();

        var index = previouslySelectedId is null ? -1 : apps.FindIndex(a => a.Id == previouslySelectedId);
        if (index < 0 && apps.Count > 0) index = 0;
        if (index >= 0)
        {
            _appsList.SelectedIndex = index;
            _selectedApp = apps[index];
        }
        else
        {
            _selectedApp = null;
        }
        RebuildAppDetailPanel();
    }

    /// <summary>Short suffix so the list conveys each app's window rules without needing to click
    /// through every entry.</summary>
    private static string DescribeWindowRules(ManagedApp app)
    {
        var parts = new List<string>();
        if (app.WindowState != AppWindowState.Unchanged) parts.Add(app.WindowState.ToString().ToLowerInvariant());
        if (app.BringToFront) parts.Add("front");
        if (app.KeepInBackground) parts.Add("background");
        if (app.TargetMonitor is int m) parts.Add($"mon{m}");
        return parts.Count == 0 ? "" : $"  [{string.Join(", ", parts)}]";
    }

    private void RebuildAppDetailPanel()
    {
        // Filled in by Task 2.
    }

    private void AddManagedApp()
    {
        // Filled in by Task 3.
    }

    private void RemoveSelectedManagedApp()
    {
        // Filled in by Task 3.
    }

    private void MoveSelectedManagedApp(int delta)
    {
        // Filled in by Task 3.
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

    // ───────────────────────────── Optimizations tab ─────────────────────────────

    private TabPage BuildOptimizationsTab()
    {
        var tab = new TabPage("Optimizations");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoScroll = true,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));

        OptimizationCategory? lastCategory = null;
        foreach (var opt in _optimizations.Optimizations)
        {
            if (opt.Category != lastCategory)
            {
                var header = new Label
                {
                    Text = opt.Category.ToString(),
                    AutoSize = true,
                    Font = new Font(Font, FontStyle.Bold),
                    Margin = new Padding(3, 12, 3, 3),
                };
                layout.Controls.Add(header);
                layout.SetColumnSpan(header, 4);
                lastCategory = opt.Category;
            }

            var nameLabel = new Label { Text = opt.DisplayName, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            var statusLabel = new Label { Text = "Checking...", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _optimizationStatusLabels[opt.Id] = statusLabel;

            var modeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            modeCombo.Items.AddRange(new object[] { OptimizationMode.Off, OptimizationMode.Manual, OptimizationMode.Auto });
            modeCombo.SelectedItem = _optimizations.GetEntry(opt.Id).Mode;

            var fixButton = new Button { Text = "Fix", AutoSize = true, Enabled = modeCombo.SelectedItem is OptimizationMode.Manual };
            _optimizationFixButtons[opt.Id] = fixButton;

            modeCombo.SelectedIndexChanged += async (_, _) =>
            {
                if (modeCombo.SelectedItem is not OptimizationMode mode) return;
                try
                {
                    await _optimizations.SetModeAsync(opt, mode);
                }
                catch (Exception ex)
                {
                    Log.Warn("SettingsForm", $"Setting mode for '{opt.Id}' failed: {ex.Message}");
                    MessageBox.Show(this, ex.Message, "VR Session Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    modeCombo.SelectedItem = _optimizations.GetEntry(opt.Id).Mode; // reflect the actual (possibly rolled-back) mode
                }
                fixButton.Enabled = modeCombo.SelectedItem is OptimizationMode.Manual;
                await RefreshOptimizationRowAsync(opt);
            };

            fixButton.Click += async (_, _) =>
            {
                fixButton.Enabled = false;
                try
                {
                    await _optimizations.ApplyManualAsync(opt);
                }
                catch (Exception ex)
                {
                    Log.Warn("SettingsForm", $"Applying fix for '{opt.Id}' failed: {ex.Message}");
                    MessageBox.Show(this, ex.Message, "VR Session Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    fixButton.Enabled = modeCombo.SelectedItem is OptimizationMode.Manual;
                    await RefreshOptimizationRowAsync(opt);
                }
            };

            layout.Controls.Add(nameLabel);
            layout.Controls.Add(statusLabel);
            layout.Controls.Add(modeCombo);
            layout.Controls.Add(fixButton);
        }

        tab.Controls.Add(layout);
        return tab;
    }

    private async Task RefreshOptimizationRowAsync(IOptimization opt)
    {
        var status = await _optimizations.CheckAsync(opt);
        if (!_optimizationStatusLabels.TryGetValue(opt.Id, out var label)) return;

        var text = status switch
        {
            OptimizationStatus.Applied => opt is ServiceStateOptimization && _optimizations.GetEntry(opt.Id).Mode == OptimizationMode.Auto
                ? "Applied (stopped — not auto-restarted)"
                : "Applied",
            OptimizationStatus.NotApplied => "Not applied",
            _ => "Unknown",
        };

        if (InvokeRequired) Invoke(() => label.Text = text);
        else label.Text = text;
    }

    /// <summary>Called from TrayApplicationContext's existing 5s status timer, alongside
    /// RefreshStatus — refreshes every row's live Applied/Not applied/Unknown label. Each check
    /// spawns real processes (powercfg, SCM queries, adapter enumeration), so this is a no-op
    /// unless this window is actually visible and showing the Optimizations tab, and
    /// re-entrancy-guarded in case one pass takes longer than the 5s timer interval.</summary>
    public async Task RefreshOptimizationsTabAsync()
    {
        if (!Visible || _tabs.SelectedTab != _optimizationsTabPage) return;
        if (_refreshingOptimizationsTab) return;

        _refreshingOptimizationsTab = true;
        try
        {
            foreach (var opt in _optimizations.Optimizations)
                await RefreshOptimizationRowAsync(opt);
        }
        finally
        {
            _refreshingOptimizationsTab = false;
        }
    }
}
