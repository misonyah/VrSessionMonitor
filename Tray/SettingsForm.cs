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
    private readonly ManagedAppWindowService _managedAppService;

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
    private Label _heartrateLabel = null!;
    private Label _warningLabel = null!;
    private readonly ToolTip _statusToolTip = new();

#if INCLUDE_HOME_ASSISTANT
    private Label _homeAssistantStatusLabel = null!;
    private ComboBox _homeAssistantAreaCombo = null!;
    private List<AreaInfo> _homeAssistantAreas = new();
    private FlowLayoutPanel _homeAssistantOnLightsPanel = null!;
    private FlowLayoutPanel _homeAssistantOffLightsPanel = null!;
    private FlowLayoutPanel _homeAssistantAfkLightsPanel = null!;
    private List<LightInfo> _homeAssistantLightsInSelectedArea = new();

#endif

    // Outside the Home Assistant guard: Bluetooth presence has nothing to do with HA, and the
    // HA-off build has to compile too.
    private ListView? _bluetoothDeviceList;
    private TabPage? _audioTab;
    private ComboBox? _audioVrCombo;
    private ComboBox? _audioAwayCombo;
    private CheckedListBox? _audioBlockList;
    private bool _populatingAudioBlockList;
    private int _statusRowCount;
    private readonly Dictionary<Label, PictureBox> _rowDots = new();
    private FlowLayoutPanel? _bluetoothStatusPanel;
    private readonly Dictionary<string, BluetoothStatusRow> _bluetoothRows = new(StringComparer.OrdinalIgnoreCase);
    private string _bluetoothRowSignature = "";

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
        OptimizationsManager optimizations,
        ManagedAppWindowService managedAppService)
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
        _managedAppService = managedAppService;

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
        _tabs.TabPages.Add(BuildBluetoothTab());
        _tabs.TabPages.Add(BuildAudioTab());
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

        // A real table rather than a stack of "Name: value" strings, so the values line up in one
        // column and the tab can be read down rather than sentence by sentence. Four columns:
        // state dot, name, value, and an optional action button.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoScroll = true,
            Padding = new Padding(10),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // dot
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // name
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));        // value
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // action

        _sessionStatusLabel = AddStatusRow(layout, "Status");
        _headsetLabel = AddStatusRow(layout, "Headset");
        _trackerLabel = AddStatusRow(layout, "Trackers");
        _peripheralLabel = AddStatusRow(layout, "Eye/Face tracking");

        // A continuation of the row above rather than a row of its own, so it gets no dot or name
        // and simply sits under the value it elaborates.
        _peripheralNextLabel = AddContinuationRow(layout);

        _steamVrLabel = AddStatusRow(layout, "SteamVR");

        _restartVrChatButton = new Button { Text = "Restart", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
        _restartVrChatButton.Click += (_, _) => _ = _orchestrator.RestartVrChatAsync();
        _vrChatLabel = AddStatusRow(layout, "VRChat", _restartVrChatButton);

        _firmwareLabel = AddStatusRow(layout, "Firmware self-heal");
        _heartrateLabel = AddStatusRow(layout, "Heart rate");
        _warningLabel = AddStatusRow(layout, "Warnings");

        // One row per tracked Bluetooth device, each showing the icons of the apps it starts, so
        // "this device brings up these programs" is visible rather than something to remember.
        _bluetoothStatusPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(3, 2, 3, 3),
        };
        layout.Controls.Add(_bluetoothStatusPanel);
        layout.SetColumnSpan(_bluetoothStatusPanel, 2);

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

        // VRChat raises its AFK parameter whenever it loses focus, opening the SteamVR dashboard
        // included, so without a hold the lights change on every trip to the Steam menu.
        var afkHoldRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        afkHoldRow.Controls.Add(new Label
        {
            Text = "Wait before AFK counts:",
            AutoSize = true,
            Margin = new Padding(3, 6, 3, 3),
        });
        var afkHoldSpinner = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 600,
            Width = 70,
            Value = Math.Clamp(_config.HomeAssistant.AfkOscHoldSeconds, 0, 600),
        };
        afkHoldSpinner.ValueChanged += (_, _) =>
        {
            var seconds = (int)afkHoldSpinner.Value;
            if (seconds == _config.HomeAssistant.AfkOscHoldSeconds) return;

            _config.HomeAssistant.AfkOscHoldSeconds = seconds;
            _config.Save(_configPath);
            Log.Info("SettingsForm", $"AFK hold set to {seconds}s — takes effect next time the lights manager starts.");
        };
        afkHoldRow.Controls.Add(afkHoldSpinner);
        afkHoldRow.Controls.Add(new Label
        {
            Text = "seconds (0 = immediately)",
            AutoSize = true,
            Margin = new Padding(3, 6, 3, 3),
            ForeColor = SystemColors.GrayText,
        });
        haLayout.Controls.Add(afkHoldRow);
        haLayout.Controls.Add(new Label
        {
            Text = "VRChat reports AFK whenever it loses focus, including when you open the SteamVR "
                   + "dashboard. Waiting a while before believing it stops the Steam menu from changing your lights.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 6),
        });

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

    /// <summary>
    /// One table row: state dot, name, value, and an optional action control.
    ///
    /// The dot is its own PictureBox in its own cell rather than the Label's Image, because Label
    /// has no TextImageRelation — that lives on ButtonBase — so an image and text sharing an
    /// alignment just draw on top of each other, which is exactly how the dots ended up sitting
    /// under the text. A cell of its own also aligns every dot with every other.
    ///
    /// Returns the VALUE label; callers set only the value, never the name.
    /// </summary>
    private Label AddStatusRow(TableLayoutPanel layout, string name, Control? action = null)
    {
        var row = _statusRowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var dot = new PictureBox
        {
            Image = StatusIcons.Dot(StatusLevel.Unknown),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 3, 6, 3),
        };
        layout.Controls.Add(dot, 0, row);

        layout.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 12, 3),
        }, 1, row);

        var value = new Label
        {
            Text = "--",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 3, 3),
        };
        layout.Controls.Add(value, 2, row);

        if (action is not null) layout.Controls.Add(action, 3, row);

        _rowDots[value] = dot;
        return value;
    }

    /// <summary>A value-only row that continues the one above it — no dot, no name, indented so it
    /// reads as detail rather than a separate thing being reported.</summary>
    private Label AddContinuationRow(TableLayoutPanel layout)
    {
        var row = _statusRowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 0, 3, 3),
            Visible = false,
        };
        layout.Controls.Add(label, 2, row);
        return label;
    }

    /// <summary>Sets a row's state dot, skipping the assignment when it has not changed — these
    /// run on a 5s tick and reassigning an identical Image forces a needless repaint.</summary>
    private void SetRowStatus(Label label, StatusLevel level)
    {
        if (!_rowDots.TryGetValue(label, out var dot)) return;

        var image = StatusIcons.Dot(level);
        if (!ReferenceEquals(dot.Image, image)) dot.Image = image;
    }

    /// <summary>
    /// Keeps the Bluetooth rows on the Status tab in step with the tracked devices.
    ///
    /// Rebuilds only when the tracked SET changes, never on a plain presence change: this runs
    /// every 5 seconds, and tearing down and recreating controls that often flickers visibly and
    /// throws away anything the user was interacting with. Presence itself is a label and a dot
    /// updated in place.
    /// </summary>
    /// <summary>
    /// Shows the conditions that have actually cost frames on this machine — tight memory and
    /// wide-open avatar limits — so they are visible before a session rather than discovered
    /// mid-session at 20 FPS. Advisory only; nothing here changes a setting.
    /// </summary>
    private void RefreshWarnings()
    {
        var warnings = _owner.CurrentSessionWarnings();

        if (warnings.Count == 0)
        {
            _warningLabel.Text = "none";
            SetRowStatus(_warningLabel, StatusLevel.Good);
            return;
        }

        _warningLabel.Text = string.Join("  |  ", warnings.Select(w => w.Summary));
        // Tooltip carries the explanation, so the row stays one line but the reasoning is reachable.
        _statusToolTip.SetToolTip(_warningLabel,
            string.Join(Environment.NewLine + Environment.NewLine,
                warnings.Select(w => w.Summary + Environment.NewLine + w.Detail)));
        SetRowStatus(_warningLabel, StatusLevel.Warning);
    }

    private void RefreshBluetoothStatusRows()
    {
        if (_bluetoothStatusPanel is null) return;

        var devices = _config.Bluetooth.Devices;
        var signature = string.Join("|", devices.Select(d => $"{d.Address}:{string.Join(",", d.StartAppIds)}"));
        if (signature != _bluetoothRowSignature)
        {
            _bluetoothRowSignature = signature;
            RebuildBluetoothStatusRows(devices);
        }

        foreach (var (address, row) in _bluetoothRows)
        {
            var present = _owner.IsBluetoothDevicePresent(address);
            var text = present ? "on" : "not detected";
            if (row.State.Text != text) row.State.Text = text;
            SetRowStatus(row.State, present ? StatusLevel.Good : StatusLevel.Unknown);
        }
    }

    private void RebuildBluetoothStatusRows(List<BluetoothDeviceConfig> devices)
    {
        _bluetoothStatusPanel!.SuspendLayout();
        _bluetoothStatusPanel.Controls.Clear();
        _bluetoothRows.Clear();

        foreach (var device in devices)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };

            row.Controls.Add(new PictureBox
            {
                Image = StatusIcons.Bluetooth(),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = new Padding(3, 6, 2, 3),
            });

            var name = device.DisplayName is { Length: > 0 } ? device.DisplayName : device.Address;
            row.Controls.Add(new Label { Text = $"{name}:", AutoSize = true, Margin = new Padding(0, 6, 3, 3) });

            var state = new Label
            {
                Text = "--",
                AutoSize = true,
                Margin = new Padding(0, 6, 6, 3),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
            };
            row.Controls.Add(state);

            // The apps this device starts, shown as their own icons — the same ones you'd
            // recognise in the taskbar. Falls back to a text label when an exe has no extractable
            // icon, or when the target is a Steam app id rather than a path.
            foreach (var appId in device.StartAppIds)
            {
                var app = _config.GetApp(appId);
                var icon = StatusIcons.ForExecutable(app is null ? null : _config.LaunchTargetFor(appId, ""));

                if (icon is not null)
                    row.Controls.Add(new PictureBox
                    {
                        Image = icon,
                        SizeMode = PictureBoxSizeMode.AutoSize,
                        Margin = new Padding(0, 4, 3, 3),
                        Tag = app?.DisplayName ?? appId,
                    });
                else
                    row.Controls.Add(new Label
                    {
                        Text = app?.DisplayName ?? appId,
                        AutoSize = true,
                        ForeColor = SystemColors.GrayText,
                        Margin = new Padding(0, 6, 3, 3),
                    });
            }

            _bluetoothStatusPanel.Controls.Add(row);
            _bluetoothRows[device.Address] = new BluetoothStatusRow(state);
        }

        _bluetoothStatusPanel.ResumeLayout();
    }

    private sealed record BluetoothStatusRow(Label State);

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
        // Controls is index-based and re-indexes on every removal, so a foreach silently skips
        // every other control (Dispose() removes the item from the collection as a side effect).
        while (panel.Controls.Count > 0) panel.Controls[0].Dispose();
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
        _headsetLabel.Text = _headset.IsOnline ? "online" : "offline";
        SetRowStatus(_headsetLabel, _headset.IsOnline ? StatusLevel.Good : StatusLevel.Unknown);

        _trackerLabel.Text = _trackers.Summarize();
        SetRowStatus(_trackerLabel, _trackers.OnlineCount switch
        {
            0 => StatusLevel.Unknown,                                    // none up: usually just "not in a session"
            var n when n == _trackers.TotalCount => StatusLevel.Good,
            _ => StatusLevel.Warning,                                     // some up, some not
        });

        // Two independent facts, so both are shown: whether the strap is switched on (our own BLE
        // scan) and whether a reading is arriving (VRCOSC over OSC). They can disagree — most
        // usefully when the strap is on but nothing has connected to it, which is the state where
        // VRCOSC has been started but has not picked it up.
        _heartrateLabel.Text = _owner.SummarizeHeartrate();
        SetRowStatus(_heartrateLabel, _owner.HeartrateStatusLevel());

        RefreshWarnings();
        RefreshBluetoothStatusRows();

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
            ? $"all running ({p.ModuleProcessCount} module(s), {cams.Count(c => c.Streaming)}/{cams.Count} cameras streaming)"
            : string.Join(", ", parts);
        SetRowStatus(_peripheralLabel, parts.Count == 0 ? StatusLevel.Good : StatusLevel.Warning);

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
        {
            _steamVrLabel.Text = "running";
            SetRowStatus(_steamVrLabel, StatusLevel.Good);
        }
        else if (!sv.VrServerRunning && !sv.VrMonitorRunning && !sv.VrCompositorRunning)
        {
            _steamVrLabel.Text = "not running";
            SetRowStatus(_steamVrLabel, StatusLevel.Unknown);
        }
        else
        {
            var down = new List<string>();
            if (!sv.VrServerRunning) down.Add("vrserver");
            if (!sv.VrMonitorRunning) down.Add("vrmonitor");
            if (!sv.VrCompositorRunning) down.Add("vrcompositor");
            _steamVrLabel.Text = $"partial (down: {string.Join(", ", down)})";
            SetRowStatus(_steamVrLabel, StatusLevel.Warning);
        }
        if (_steamVr.DescribePendingAction() is string steamVrPending)
            _steamVrLabel.Text += $" — next: {steamVrPending}";

        _vrChatLabel.Text = _vrChat.Current.Running ? "running" : "not running";
        SetRowStatus(_vrChatLabel, _vrChat.Current.Running ? StatusLevel.Good : StatusLevel.Unknown);
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
        var text = state;
        if (InvokeRequired) Invoke(() => _sessionStatusLabel.Text = text);
        else _sessionStatusLabel.Text = text;
    }

    /// <summary>Fires from HeadsetMonitor's own background polling loop, not the UI thread.</summary>
    public void UpdateHeadsetStatus(bool isOnline)
    {
        var text = isOnline ? "online" : "offline";
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
                ? $"{_firmwareNotify.LastEventSummary} ({FormatAgo(DateTime.UtcNow - at)} ago)"
                : "none yet";
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

        layout.Controls.Add(new Label
        {
            Text = "Per-app start-up and window settings now live in the Apps tab.",
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 10),
        });
        layout.Controls.Add(BuildCheckbox("Auto-heal face tracking (restart SRanipal + VRCFaceTracking when it stalls/freezes)", _config.FaceTrackingAutoFix.Enabled,
            v => { _config.FaceTrackingAutoFix.Enabled = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Restart face tracking when the Vive camera attaches late", _config.FaceTrackingAutoFix.RestartOnCameraReappear,
            v => { _config.FaceTrackingAutoFix.RestartOnCameraReappear = v; _config.Save(_configPath); }));
        layout.Controls.Add(BuildCheckbox("Start with Windows", WindowsStartup.IsEnabled(),
            v => WindowsStartup.SetEnabled(v)));

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
        _appsList.SelectedIndexChanged += OnAppsListSelectedIndexChanged;
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

    /// <summary>Handles _appsList's SelectedIndexChanged. Extracted to a named method (rather than
    /// an inline lambda) so RefreshAppsTab can detach/reattach it around its in-place label-only
    /// update loop — see the comment there for why.</summary>
    private void OnAppsListSelectedIndexChanged(object? sender, EventArgs e)
    {
        var apps = SortedApps();
        _selectedApp = _appsList.SelectedIndex >= 0 && _appsList.SelectedIndex < apps.Count
            ? apps[_appsList.SelectedIndex]
            : null;
        RebuildAppDetailPanel();
    }

    /// <summary>Apps in display order. Order is a user-editable int, so sort rather than trusting
    /// the raw list order — a hand-edited config can easily have gaps or duplicates.</summary>
    private List<ManagedApp> SortedApps() =>
        _config.ManagedApps.OrderBy(a => a.Order).ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The list row text for one app: name, disabled marker, window-rule summary, and
    /// current run state. Extracted so the periodic refresh can update labels in place without
    /// rebuilding the list (which would tear down the detail panel mid-edit).</summary>
    private string FormatAppListItem(ManagedApp app)
    {
        var enabled = app.Enabled ? "" : "  (disabled)";
        var rules = DescribeWindowRules(app);
        var origin = _managedAppService.CurrentOrigins.TryGetValue(app.Id, out var o) ? o : AppStartOrigin.NotRunning;
        // "manual" is worth surfacing: it explains why an app's window rules were applied (or,
        // if ApplyWindowRulesWhenStartedManually is off, why they weren't).
        var originText = origin switch
        {
            AppStartOrigin.Managed => "  • running (managed)",
            AppStartOrigin.Manual => "  • running (manual)",
            _ => "",
        };
        return $"{app.DisplayName}{enabled}{rules}{originText}";
    }

    /// <summary>Repopulates the list, preserving the selected app by Id where possible — index
    /// alone is wrong after a reorder or removal.</summary>
    private void RefreshAppsList()
    {
        var previouslySelectedId = _selectedApp?.Id;

        _appsList.BeginUpdate();
        _appsList.Items.Clear();
        var apps = SortedApps();
        foreach (var app in apps)
            _appsList.Items.Add(FormatAppListItem(app));
        _appsList.EndUpdate();

        var index = previouslySelectedId is null ? -1 : apps.FindIndex(a => a.Id == previouslySelectedId);
        if (index < 0 && apps.Count > 0) index = 0;

        // Items.Clear() above already reset SelectedIndex to -1, so assigning any index >= 0
        // here is always a real change: SelectedIndexChanged fires and OnAppsListSelectedIndexChanged
        // rebuilds the detail panel itself. Only the empty-list case (index stays -1, so the
        // assignment is a no-op and nothing fires) needs an explicit rebuild here, to show
        // "No app selected."
        if (index >= 0) _appsList.SelectedIndex = index;
        _selectedApp = index >= 0 ? apps[index] : null;
        if (index < 0) RebuildAppDetailPanel();
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

    /// <summary>Rebuilds the right-hand editor for the selected app. Rebuilt wholesale on each
    /// selection change rather than rebound, matching how this form handles the Home Assistant
    /// light rows — simpler than tracking per-control bindings, and these panels are small.</summary>
    private void RebuildAppDetailPanel()
    {
        _appDetailPanel.SuspendLayout();
        // Controls is index-based and re-indexes on every removal, so a foreach silently skips
        // every other control (Dispose() removes the item from the collection as a side effect).
        while (_appDetailPanel.Controls.Count > 0) _appDetailPanel.Controls[0].Dispose();
        _appDetailPanel.Controls.Clear();

        if (_selectedApp is not ManagedApp app)
        {
            _appDetailPanel.Controls.Add(new Label { Text = "No app selected.", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            _appDetailPanel.ResumeLayout();
            return;
        }

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false };

        // Persist + refresh the list so its label/suffix reflects the change immediately.
        // Uses UpdateAppListLabels(), not RefreshAppsList(): this runs from inside a control
        // event still on the stack (e.g. a TextBox's Leave handler firing mid focus-transition
        // to another control in this same panel), and RefreshAppsList() ends in
        // RebuildAppDetailPanel(), which would dispose that control out from under its own event.
        // Every Save() caller here only changes label text (name/enabled/rules), never
        // membership or order, so the in-place label update is always valid.
        void Save()
        {
            _config.Save(_configPath);
            UpdateAppListLabels();
        }

        layout.Controls.Add(new Label
        {
            Text = app.DisplayName,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 3, 3, 8),
        });

        layout.Controls.Add(BuildCheckbox("Enabled (participates in auto-start/stop)", app.Enabled, v =>
        {
            app.Enabled = v;
            SyncLegacyEnabledFlag(app, v);
            Save();
        }));

        layout.Controls.Add(BuildTextRow("Display name", app.DisplayName, v =>
        {
            if (string.IsNullOrWhiteSpace(v)) return; // an unnamed row is unusable in the list
            app.DisplayName = v.Trim();
            Save();
        }));

        // ── launch
        var launchGroup = new GroupBox { Text = "Launch", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(360, 0) };
        var launchLayout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

        // Target now drives launching (MonitorConfig.LaunchTargetFor). Clearing it falls back to
        // the matching Paths entry rather than leaving the app unlaunchable, so say that plainly —
        // an empty box that silently reverts to another value is worth explaining.
        launchLayout.Controls.Add(new Label
        {
            Text = "Target is what gets launched — an .exe path, or a Steam app ID for Steam apps. "
                   + "Leave it empty to fall back to the matching entry in appsettings.json.",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 3, 3, 6),
        });

        // Pickers rather than free text: ProcessName must match exactly (no .exe) and is what the
        // window rules key on, so a typo fails silently. Suggested by tomaae (AppSupervisor), whose
        // editor picks from running processes/executables instead of asking you to type them.
        var pickerRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(3, 0, 3, 6) };

        var pickRunning = new Button { Text = "Pick running app...", AutoSize = true };
        pickRunning.Click += (_, _) =>
        {
            using var dialog = new ProcessPickerDialog();
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            app.ProcessName = dialog.SelectedProcessName;
            if (!string.IsNullOrWhiteSpace(dialog.SelectedExePath))
            {
                app.Target = dialog.SelectedExePath;
                app.LaunchMethod = AppLaunchMethod.Executable;
            }
            // Name a still-unnamed entry after what was picked, so "New app" doesn't linger.
            if (string.IsNullOrWhiteSpace(app.DisplayName) || app.DisplayName == "New app")
                app.DisplayName = dialog.SelectedProcessName;

            Save();
            RebuildAppDetailPanel(); // reflect every field the pick just changed
            Log.Info("SettingsForm", $"Managed app '{app.Id}' set from running process '{dialog.SelectedProcessName}'.");
        };

        var browseExe = new Button { Text = "Browse for .exe...", AutoSize = true };
        browseExe.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select the application executable",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            app.Target = dialog.FileName;
            app.LaunchMethod = AppLaunchMethod.Executable;
            // Process name is the filename without extension — the same convention
            // Process.GetProcessesByName expects, which is what the window rules use.
            var derived = Path.GetFileNameWithoutExtension(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(derived)) app.ProcessName = derived;
            if (string.IsNullOrWhiteSpace(app.DisplayName) || app.DisplayName == "New app")
                app.DisplayName = derived;

            Save();
            RebuildAppDetailPanel();
            Log.Info("SettingsForm", $"Managed app '{app.Id}' set from executable '{dialog.FileName}'.");
        };

        pickerRow.Controls.Add(pickRunning);
        pickerRow.Controls.Add(browseExe);
        launchLayout.Controls.Add(pickerRow);

        var methodRow = new FlowLayoutPanel { AutoSize = true };
        methodRow.Controls.Add(new Label { Text = "Method", AutoSize = true, Width = 140, Margin = new Padding(3, 6, 3, 3) });
        var methodCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        methodCombo.Items.AddRange(new object[] { AppLaunchMethod.Executable, AppLaunchMethod.SteamAppId });
        methodCombo.SelectedItem = app.LaunchMethod;
        methodCombo.SelectedIndexChanged += (_, _) =>
        {
            if (methodCombo.SelectedItem is AppLaunchMethod m) { app.LaunchMethod = m; Save(); }
        };
        methodRow.Controls.Add(methodCombo);
        launchLayout.Controls.Add(methodRow);

        launchLayout.Controls.Add(BuildTextRow("Target (exe path or Steam app id)", app.Target, v => { app.Target = v.Trim(); Save(); }));
        launchLayout.Controls.Add(BuildTextRow("Process name (no .exe)", app.ProcessName, v => { app.ProcessName = v.Trim(); Save(); }));
        launchGroup.Controls.Add(launchLayout);
        layout.Controls.Add(launchGroup);

        // ── window
        var windowGroup = new GroupBox { Text = "Window", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(360, 0) };
        var windowLayout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

        var stateRow = new FlowLayoutPanel { AutoSize = true };
        stateRow.Controls.Add(new Label { Text = "State on launch", AutoSize = true, Width = 140, Margin = new Padding(3, 6, 3, 3) });
        var stateCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        stateCombo.Items.AddRange(new object[]
        {
            AppWindowState.Unchanged, AppWindowState.Normal, AppWindowState.Minimized,
            AppWindowState.Maximized, AppWindowState.Fullscreen,
        });
        stateCombo.SelectedItem = app.WindowState;
        stateCombo.SelectedIndexChanged += (_, _) =>
        {
            if (stateCombo.SelectedItem is AppWindowState s) { app.WindowState = s; Save(); }
        };
        stateRow.Controls.Add(stateCombo);
        windowLayout.Controls.Add(stateRow);

        windowLayout.Controls.Add(BuildCheckbox("Bring to front after launch", app.BringToFront, v =>
        {
            app.BringToFront = v;
            // Both at once is contradictory; WindowController resolves it in favour of
            // BringToFront, so make the UI reflect that rather than letting the config lie.
            if (v) app.KeepInBackground = false;
            Save();
            RebuildAppDetailPanel();
        }));

        windowLayout.Controls.Add(BuildCheckbox("Keep in background (never steal focus)", app.KeepInBackground, v =>
        {
            app.KeepInBackground = v;
            if (v) app.BringToFront = false;
            Save();
            RebuildAppDetailPanel();
        }));

        // 0 means "any monitor" — NumericUpDown has no null, and a nullable spinner would be more
        // UI than this is worth.
        windowLayout.Controls.Add(BuildNumericRow("Monitor (0 = any)", app.TargetMonitor ?? 0, v =>
        {
            app.TargetMonitor = v <= 0 ? null : v;
            Save();
        }));

        windowLayout.Controls.Add(BuildCheckbox("Apply window rules even when started manually", app.ApplyWindowRulesWhenStartedManually, v =>
        {
            app.ApplyWindowRulesWhenStartedManually = v;
            Save();
        }));

        // Defaults to whatever the launch sites did before this was a setting, so a config written
        // before the property existed keeps working (see MonitorConfig.SuppressUacFor).
        windowLayout.Controls.Add(BuildCheckbox("Launch without a UAC prompt (RunAsInvoker)",
            _config.SuppressUacFor(app.Id, builtInDefault: false), v =>
        {
            app.SuppressUacPrompt = v;
            Save();
        }));
        windowLayout.Controls.Add(new Label
        {
            Text = "Only affects apps whose manifest asks for elevation. Works when the app merely "
                   + "prefers admin and still runs without it (sr_runtime does). If an app truly "
                   + "requires admin, this makes it start without the rights it needs and fail — "
                   + "leave it off unless you know the app tolerates running unelevated.",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 6),
        });

        // CPU priority held only for the length of a VR session, then put back.
        var priorityRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        priorityRow.Controls.Add(new Label { Text = "Priority during a VR session:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });

        var priorityCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        priorityCombo.Items.Add(LeaveAlone);
        foreach (var p in Enum.GetValues<AppProcessPriority>()) priorityCombo.Items.Add(p.ToString());
        priorityCombo.SelectedItem = app.SessionPriority?.ToString() ?? LeaveAlone;
        priorityCombo.SelectedIndexChanged += (_, _) =>
        {
            var picked = priorityCombo.SelectedItem as string;
            app.SessionPriority = picked is null or LeaveAlone
                ? null
                : Enum.Parse<AppProcessPriority>(picked);
            Save();
        };
        priorityRow.Controls.Add(priorityCombo);
        windowLayout.Controls.Add(priorityRow);

        windowLayout.Controls.Add(new Label
        {
            Text = "Applied when SteamVR starts and undone when it stops, so a background app can be "
                   + "pushed out of the way without closing it — BelowNormal on a Unity editor, say. "
                   + "Whatever the process was at beforehand is what it goes back to. Realtime isn't "
                   + "offered: it outranks input and audio handling and can lock the machine up. "
                   + "Note this only helps if the CPU is what's actually short.",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 6),
        });

        windowLayout.Controls.Add(BuildCheckbox("Freeze this app during a VR session", app.SuspendDuringSession, v =>
        {
            app.SuspendDuringSession = v;
            Save();
        }));
        windowLayout.Controls.Add(new Label
        {
            Text = "Pauses every thread when SteamVR starts and releases the app's memory to the "
                   + "system, then resumes it untouched afterwards — the same RAM you'd get by "
                   + "closing it, without losing what's open. Editors and browsers are good "
                   + "candidates. Not safe for anything mid-download or mid-write: a frozen app "
                   + "keeps its locks, and network connections can time out while it's stopped. "
                   + "The VR apps themselves and system processes are always refused.",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 6),
        });

        windowGroup.Controls.Add(windowLayout);
        layout.Controls.Add(windowGroup);

        _appDetailPanel.Controls.Add(layout);
        _appDetailPanel.ResumeLayout();
    }

    /// <summary>Dropdown entry meaning "no session priority for this app" — the default.</summary>
    private const string LeaveAlone = "Leave alone";

    /// <summary>Keeps the legacy per-app config properties in sync for apps whose old flag is still
    /// read elsewhere. The foundation plan's final review found that writing only one side turns
    /// controls into silent no-ops, so both are written until those legacy readers are retired.</summary>
    private void SyncLegacyEnabledFlag(ManagedApp app, bool enabled)
    {
        switch (app.Id)
        {
            case "vrchat": _config.SessionFlow.AutoLaunchVrChat = enabled; break;
            case "slimevr": _config.SlimeVrLifecycle.Enabled = enabled; break;
            case "vrcosc": _config.VrcOscLifecycle.Enabled = enabled; break;
            case "vrcfacetracking": _config.VrcFaceTrackingLifecycle.Enabled = enabled; break;
            case "baballonia": _config.BaballoniaLifecycle.Enabled = enabled; break;
            case "sranipal":
            case "virtualhere":
                _config.VirtualHereSRanipalLifecycle.Enabled = enabled;
                // One lifecycle manager owns both processes and gates them on "sranipal", so these
                // two entries can't diverge without the list lying about one of them.
                if (_config.GetApp("sranipal") is { } sr) sr.Enabled = enabled;
                if (_config.GetApp("virtualhere") is { } vh) vh.Enabled = enabled;
                break;
            // The overlay picker is an enum, not a bool: enabling one overlay must disable the
            // other, and disabling either means None.
            case "ovrtoolkit":
                _config.SessionFlow.VrOverlay = enabled ? VrOverlayChoice.OvrToolkit
                    : _config.SessionFlow.VrOverlay == VrOverlayChoice.OvrToolkit ? VrOverlayChoice.None : _config.SessionFlow.VrOverlay;
                if (enabled && _config.GetApp("xsoverlay") is { } xs) xs.Enabled = false;
                break;
            case "xsoverlay":
                _config.SessionFlow.VrOverlay = enabled ? VrOverlayChoice.XSOverlay
                    : _config.SessionFlow.VrOverlay == VrOverlayChoice.XSOverlay ? VrOverlayChoice.None : _config.SessionFlow.VrOverlay;
                if (enabled && _config.GetApp("ovrtoolkit") is { } ovr) ovr.Enabled = false;
                break;
        }
    }

    /// <summary>Adds a blank entry and selects it — the detail panel is where it gets filled in,
    /// which avoids a separate new-app dialog.</summary>
    private void AddManagedApp()
    {
        string id;
        do
        {
            id = $"custom-{Guid.NewGuid():N}"[..16];
        } while (_config.GetApp(id) is not null);

        var app = new ManagedApp
        {
            Id = id,
            DisplayName = "New app",
            Enabled = false, // don't auto-start something that isn't configured yet
            Order = _config.ManagedApps.Count == 0 ? 0 : _config.ManagedApps.Max(a => a.Order) + 1,
        };
        _config.ManagedApps.Add(app);
        _config.Save(_configPath);
        _selectedApp = app;
        RefreshAppsList();
        Log.Info("SettingsForm", $"Added managed app '{app.Id}'.");
    }

    /// <summary>Removes the selected app. Seeded apps are removable — migration only re-seeds when
    /// the whole list is empty, so removing one entry sticks.</summary>
    private void RemoveSelectedManagedApp()
    {
        if (_selectedApp is not ManagedApp app) return;

        if (_config.ManagedApps.Count == 1)
        {
            MessageBox.Show(this,
                "This is the last managed app. Removing it would empty the list, and " +
                "VrSessionMonitor treats an empty list as unconfigured — all 9 default apps would " +
                "be restored automatically the next time it starts.\n\nDisable the app instead if " +
                "you don't want it managed.",
                "VR Session Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Remove '{app.DisplayName}' from the managed apps list?\n\nThis only stops VrSessionMonitor managing it — the app itself isn't touched.",
            "VR Session Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        _config.ManagedApps.Remove(app);
        _config.Save(_configPath);
        _selectedApp = null;
        RefreshAppsList();
        Log.Info("SettingsForm", $"Removed managed app '{app.Id}'.");
    }

    /// <summary>Moves the selected app up (-1) or down (+1) in launch order. Rewrites every app's
    /// Order to its new index afterwards, so a hand-edited config with duplicate or gappy Order
    /// values gets normalised rather than reordering unpredictably.</summary>
    private void MoveSelectedManagedApp(int delta)
    {
        if (_selectedApp is not ManagedApp app) return;

        var apps = SortedApps();
        var index = apps.FindIndex(a => a.Id == app.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= apps.Count) return;

        (apps[index], apps[target]) = (apps[target], apps[index]);
        for (var i = 0; i < apps.Count; i++) apps[i].Order = i;

        _config.Save(_configPath);
        RefreshAppsList();
    }

    // ───────────────────────────── Advanced tab ─────────────────────────────

    /// <summary>
    /// Bluetooth presence. The scan is passive — it never connects — so it cannot take the heart
    /// rate strap away from VRCOSC or a toy away from Intiface, and the copy here says so, because
    /// "will this fight my other software" is the first thing anyone will wonder.
    /// </summary>
    private TabPage BuildBluetoothTab()
    {
        var tab = new TabPage("Bluetooth");
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), WrapContents = false, AutoScroll = true };

        layout.Controls.Add(new Label
        {
            Text = "Detects which Bluetooth devices are switched on, by listening for the "
                   + "advertisements they broadcast. It never connects to anything, so it cannot "
                   + "interfere with VRCOSC's heart rate connection or with Intiface.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 8),
        });

        var enabled = new CheckBox
        {
            Text = "Scan for Bluetooth devices",
            Checked = _config.Bluetooth.Enabled,
            AutoSize = true,
        };
        enabled.CheckedChanged += (_, _) =>
        {
            _config.Bluetooth.Enabled = enabled.Checked;
            _config.Save(_configPath);
            Log.Info("SettingsForm", $"Bluetooth scanning set to {enabled.Checked} — takes effect on restart.");
        };
        layout.Controls.Add(enabled);
        layout.Controls.Add(new Label
        {
            Text = "Takes effect when VrSessionMonitor restarts.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 8),
        });

        var activeScan = new CheckBox
        {
            Text = "Ask devices for their name (active scanning)",
            Checked = _config.Bluetooth.ActiveScanning,
            AutoSize = true,
        };
        activeScan.CheckedChanged += (_, _) =>
        {
            _config.Bluetooth.ActiveScanning = activeScan.Checked;
            _config.Save(_configPath);
        };
        layout.Controls.Add(activeScan);
        layout.Controls.Add(new Label
        {
            Text = "Without this most devices show up as a bare address and nothing else — a real "
                   + "scan here found 23 devices and not one name. It still never connects to "
                   + "anything, so it cannot interfere with VRCOSC or Intiface; it just transmits a "
                   + "short request instead of only listening.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 8),
        });

        var timeoutRow = new FlowLayoutPanel { AutoSize = true };
        timeoutRow.Controls.Add(new Label { Text = "Treat a device as gone after:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        var timeout = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 300,
            Width = 70,
            Value = Math.Clamp(_config.Bluetooth.PresenceTimeoutSeconds, 5, 300),
        };
        timeout.ValueChanged += (_, _) =>
        {
            _config.Bluetooth.PresenceTimeoutSeconds = (int)timeout.Value;
            _config.Save(_configPath);
        };
        timeoutRow.Controls.Add(timeout);
        timeoutRow.Controls.Add(new Label { Text = "seconds without being seen", AutoSize = true, Margin = new Padding(3, 6, 3, 3), ForeColor = SystemColors.GrayText });
        layout.Controls.Add(timeoutRow);
        layout.Controls.Add(new Label
        {
            Text = "Devices advertise every second or two and dropped packets are normal, so a short "
                   + "value here makes presence flicker on and off — and since apps start when a "
                   + "device appears, flickering means starting them again. 60 seconds matches "
                   + "Windows' own out-of-range timeout; going higher only slows detection.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 8),
        });

        _bluetoothDeviceList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Width = 560,
            Height = 200,
            Margin = new Padding(3, 6, 3, 6),
        };
        _bluetoothDeviceList.Columns.Add("Device", 190);
        _bluetoothDeviceList.Columns.Add("Address", 140);
        _bluetoothDeviceList.Columns.Add("Present", 70);
        _bluetoothDeviceList.Columns.Add("Starts", 150);
        layout.Controls.Add(_bluetoothDeviceList);

        // No Refresh button: the scan runs continuously, so the Present column keeps itself up to
        // date from the same 5s tick that drives the rest of this window. A button to re-read
        // something the app is already watching is just a manual workaround for a stale view.
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var addSeen = new Button { Text = "Track a discovered device...", AutoSize = true };
        addSeen.Click += (_, _) => TrackDiscoveredDevice();
        var remove = new Button { Text = "Stop tracking", AutoSize = true };
        remove.Click += (_, _) => RemoveTrackedDevice();
        buttons.Controls.Add(addSeen);
        buttons.Controls.Add(remove);
        layout.Controls.Add(buttons);

        layout.Controls.Add(new Label
        {
            Text = "A tracked device can start apps when it appears — switch a toy on and Intiface "
                   + "and OSCGoesBrrr start by themselves. Apps are only ever started, never "
                   + "stopped, so a device dropping out for a moment won't close something you're using.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 3, 4),
        });

        tab.Controls.Add(layout);
        RefreshBluetoothDevices();
        return tab;
    }

    /// <summary>
    /// Updates just the Present column, in place, from the 5s status tick.
    ///
    /// Deliberately does NOT rebuild the list. Clearing and re-adding items would drop the user's
    /// selection every five seconds, which is the same class of bug that twice had this window's
    /// timer wiping out in-progress edits. Only the cell text and colour change, so a row stays
    /// selected while its presence updates underneath.
    /// </summary>
    public void RefreshBluetoothPresence()
    {
        if (_bluetoothDeviceList is null || _bluetoothDeviceList.Items.Count == 0) return;

        foreach (ListViewItem item in _bluetoothDeviceList.Items)
        {
            if (item.Tag is not BluetoothDeviceConfig device) continue;

            var present = _owner.IsBluetoothDevicePresent(device.Address);
            var text = present ? "yes" : "no";
            if (item.SubItems[2].Text != text) item.SubItems[2].Text = text;

            var colour = present ? SystemColors.WindowText : SystemColors.GrayText;
            if (item.ForeColor != colour) item.ForeColor = colour;
        }
    }

    /// <summary>Rebuilds the whole list. Only for structural changes — adding or removing a
    /// tracked device — since it resets selection.</summary>
    private void RefreshBluetoothDevices()
    {
        if (_bluetoothDeviceList is null) return;

        _bluetoothDeviceList.BeginUpdate();
        _bluetoothDeviceList.Items.Clear();

        foreach (var device in _config.Bluetooth.Devices)
        {
            var present = _owner.IsBluetoothDevicePresent(device.Address);
            var starts = device.StartAppIds.Count == 0
                ? ""
                : string.Join(", ", device.StartAppIds.Select(id => _config.GetApp(id)?.DisplayName ?? id));

            var item = new ListViewItem(device.DisplayName is { Length: > 0 } ? device.DisplayName : "(unnamed)");
            item.SubItems.Add(device.Address);
            item.SubItems.Add(present ? "yes" : "no");
            item.SubItems.Add(starts);
            item.Tag = device;
            if (!present) item.ForeColor = SystemColors.GrayText;
            _bluetoothDeviceList.Items.Add(item);
        }

        _bluetoothDeviceList.EndUpdate();
    }

    private void TrackDiscoveredDevice()
    {
        if (!_config.Bluetooth.Enabled)
        {
            MessageBox.Show(this,
                "Turn on \"Scan for Bluetooth devices\" and restart VrSessionMonitor first — there's nothing to pick from until it has scanned.",
                "VR Session Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var seen = _owner.DiscoveredBluetoothDevices()
            .Where(s => _config.Bluetooth.GetDevice(s.Address) is null)
            .ToList();

        if (seen.Count == 0)
        {
            MessageBox.Show(this,
                "No new devices have been seen yet. Make sure the device is switched on and give the scan a few seconds.",
                "VR Session Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new BluetoothDevicePickerDialog(seen, _config);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedDevice is null) return;

        _config.Bluetooth.Devices.Add(dialog.SelectedDevice);
        _config.Save(_configPath);
        Log.Info("SettingsForm", $"Now tracking Bluetooth device {dialog.SelectedDevice.Address} ({dialog.SelectedDevice.DisplayName}).");
        RefreshBluetoothDevices();
    }

    private void RemoveTrackedDevice()
    {
        if (_bluetoothDeviceList?.SelectedItems.Count is not > 0) return;
        if (_bluetoothDeviceList.SelectedItems[0].Tag is not BluetoothDeviceConfig device) return;

        _config.Bluetooth.Devices.Remove(device);
        _config.Save(_configPath);
        RefreshBluetoothDevices();
    }

    /// <summary>
    /// Audio device rules. Devices are picked from a live list rather than typed, because endpoint
    /// names are long and near-identical between similar hardware, and their ids are what actually
    /// gets matched.
    ///
    /// The list is re-read whenever this tab is shown, which matters more here than elsewhere:
    /// Virtual Desktop's endpoint only exists while it is streaming, so it is simply absent unless
    /// the headset is connected. A configured device that is missing is still listed, marked as not
    /// connected, so selecting it once does not get silently lost later.
    /// </summary>
    private TabPage BuildAudioTab()
    {
        _audioTab = new TabPage("Audio");
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), WrapContents = false, AutoScroll = true };

        layout.Controls.Add(new Label
        {
            Text = "Moves Windows' default playback device to follow where you are: the headset while "
                   + "you're wearing it, your normal headphones once you take it off.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 8),
        });

        var enabled = new CheckBox { Text = "Switch audio automatically", Checked = _config.Audio.Enabled, AutoSize = true };
        enabled.CheckedChanged += (_, _) =>
        {
            _config.Audio.Enabled = enabled.Checked;
            _config.Save(_configPath);
            Log.Info("SettingsForm", $"Automatic audio switching set to {enabled.Checked} — takes effect on restart.");
        };
        layout.Controls.Add(enabled);
        layout.Controls.Add(new Label
        {
            Text = "Takes effect when VrSessionMonitor restarts.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 8),
        });

        _audioVrCombo = AddAudioDeviceRow(layout, "While wearing the headset:",
            v => _config.Audio.VrDeviceId = v);
        _audioAwayCombo = AddAudioDeviceRow(layout, "When the headset is off:",
            v => _config.Audio.AwayDeviceId = v);

        var delayRow = new FlowLayoutPanel { AutoSize = true };
        delayRow.Controls.Add(new Label { Text = "Wait before switching:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        var delay = new NumericUpDown { Minimum = 0, Maximum = 60, Width = 60, Value = Math.Clamp(_config.Audio.SwitchDelaySeconds, 0, 60) };
        delay.ValueChanged += (_, _) => { _config.Audio.SwitchDelaySeconds = (int)delay.Value; _config.Save(_configPath); };
        delayRow.Controls.Add(delay);
        delayRow.Controls.Add(new Label { Text = "seconds (0 = immediately)", AutoSize = true, Margin = new Padding(3, 6, 3, 3), ForeColor = SystemColors.GrayText });
        layout.Controls.Add(delayRow);
        layout.Controls.Add(new Label
        {
            Text = "Stops audio flapping if you lift the headset for a moment. Kept short on purpose — "
                   + "the Home Assistant lights use a much longer 30s hold, which would be a long "
                   + "silence to sit through here.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 10),
        });

        layout.Controls.Add(new Label { Text = "Never use these as the default:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _audioBlockList = new CheckedListBox { Width = 520, Height = 130, CheckOnClick = true };
        _audioBlockList.ItemCheck += (_, _) =>
        {
            // Ignore our own population. SetItemChecked raises ItemCheck exactly as a user click
            // does, so without this, filling the list from config immediately writes it back —
            // and, during construction, does so before there is a window to marshal onto.
            if (_populatingAudioBlockList) return;

            // ItemCheck fires BEFORE the item's checked state updates, so the control has to be
            // read after the event has been processed rather than during it. BeginInvoke needs a
            // created handle: called from the constructor it throws InvalidOperationException and
            // takes the whole process down, which is exactly what happened on 2026-09-04 as soon
            // as the config held a blocked device.
            if (!IsHandleCreated) return;

            BeginInvoke(() =>
            {
                if (_audioBlockList is null) return;
                _config.Audio.NeverDefaultDeviceIds = _audioBlockList.CheckedItems
                    .OfType<AudioDeviceEntry>().Select(x => x.Id).ToList();
                _config.Save(_configPath);
            });
        };
        layout.Controls.Add(_audioBlockList);
        layout.Controls.Add(new Label
        {
            Text = "Enforced even when Windows picks one itself, which it does whenever hardware is "
                   + "plugged in. Useful for anything you never want sound coming out of by surprise.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 2, 3, 6),
        });

        _audioTab.Controls.Add(layout);
        RefreshAudioDevices();
        return _audioTab;
    }

    /// <summary>A device holder whose ToString drives what the pickers display.</summary>
    private sealed record AudioDeviceEntry(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private ComboBox AddAudioDeviceRow(FlowLayoutPanel parent, string caption, Action<string> set)
    {
        var row = new FlowLayoutPanel { AutoSize = true };
        row.Controls.Add(new Label { Text = caption, AutoSize = true, Width = 170, Margin = new Padding(3, 6, 3, 3) });

        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
        combo.SelectedIndexChanged += (_, _) =>
        {
            set(combo.SelectedItem is AudioDeviceEntry e ? e.Id : "");
            _config.Save(_configPath);
        };
        row.Controls.Add(combo);
        parent.Controls.Add(row);
        return combo;
    }

    /// <summary>Re-reads the endpoint list and rebuilds every picker, preserving selections.</summary>
    public void RefreshAudioDevices()
    {
        if (_audioVrCombo is null || _audioAwayCombo is null || _audioBlockList is null) return;

        var entries = _owner.GetAudioPlaybackDevices()
            .Select(d => new AudioDeviceEntry(d.Id, d.FriendlyName)).ToList();

        // Keep a configured device that is not currently present, so choosing Virtual Desktop's
        // endpoint while streaming does not silently clear itself the moment streaming stops.
        void KeepMissing(string id)
        {
            if (id is { Length: > 0 } && entries.All(e => !string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)))
                entries.Add(new AudioDeviceEntry(id, "(not connected) " + ShortenDeviceId(id)));
        }

        KeepMissing(_config.Audio.VrDeviceId);
        KeepMissing(_config.Audio.AwayDeviceId);
        foreach (var id in _config.Audio.NeverDefaultDeviceIds) KeepMissing(id);

        FillCombo(_audioVrCombo, entries, _config.Audio.VrDeviceId);
        FillCombo(_audioAwayCombo, entries, _config.Audio.AwayDeviceId);

        // Guarded so the ItemCheck handler treats these as population rather than user intent.
        _populatingAudioBlockList = true;
        try
        {
            _audioBlockList.Items.Clear();
            foreach (var entry in entries)
            {
                var index = _audioBlockList.Items.Add(entry);
                if (_config.Audio.NeverDefaultDeviceIds.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
                    _audioBlockList.SetItemChecked(index, true);
            }
        }
        finally
        {
            _populatingAudioBlockList = false;
        }
    }

    private static void FillCombo(ComboBox combo, List<AudioDeviceEntry> entries, string selectedId)
    {
        combo.Items.Clear();
        combo.Items.Add(new AudioDeviceEntry("", "(leave alone)"));
        foreach (var entry in entries) combo.Items.Add(entry);

        combo.SelectedItem = combo.Items.OfType<AudioDeviceEntry>()
            .FirstOrDefault(e => string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<AudioDeviceEntry>().First();
    }

    /// <summary>Endpoint ids are long and unreadable; show just the tail so one missing device is at
    /// least distinguishable from another.</summary>
    private static string ShortenDeviceId(string id) =>
        id.Length <= 12 ? id : "..." + id[^12..];

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

    /// <summary>Keeps the Apps list's run-state suffixes current without rebuilding anything.
    /// Deliberately does NOT call RefreshAppsList(): that clears the list and reselects, which
    /// rebuilds the detail panel — destroying whatever control the user is currently editing
    /// (text rows only commit on Leave, so in-progress keystrokes would be lost every 5s).
    /// Falls back to a full refresh only when the app count changed, i.e. the list is genuinely
    /// stale rather than just needing new labels.</summary>
    public void RefreshAppsTab()
    {
        if (!Visible || _tabs.SelectedIndex != AppsTabIndex) return;

        UpdateAppListLabels();
    }

    /// <summary>Updates the Apps list's item text in place — no membership/order change, so
    /// nothing gets disposed and the currently-focused control (if any) survives. Used both by
    /// the periodic tab refresh and by the detail panel's Save(), which runs from inside a
    /// control event still on the stack (see RebuildAppDetailPanel's Save() local) and must not
    /// trigger a rebuild of the very panel that control lives in.</summary>
    private void UpdateAppListLabels()
    {
        var apps = SortedApps();
        if (apps.Count != _appsList.Items.Count)
        {
            // Membership/order actually changed — the in-place path below assumes a 1:1 index
            // correspondence with the current list, which no longer holds.
            RefreshAppsList();
            return;
        }

        // Reassigning the CURRENTLY-SELECTED row fires SelectedIndexChanged twice (deselect to -1,
        // then reselect), which would rebuild the detail panel and destroy whatever control the
        // user is mid-edit in. Verified empirically against a real ListBox handle. Detaching for
        // the duration of a pure label update is safe: the selection genuinely doesn't change, so
        // there is no state the handler needs to observe.
        _appsList.SelectedIndexChanged -= OnAppsListSelectedIndexChanged;
        try
        {
            for (var i = 0; i < apps.Count; i++)
            {
                var text = FormatAppListItem(apps[i]);
                if (!string.Equals(_appsList.Items[i] as string, text, StringComparison.Ordinal))
                    _appsList.Items[i] = text;
            }
        }
        finally
        {
            _appsList.SelectedIndexChanged += OnAppsListSelectedIndexChanged;
        }
    }
}
