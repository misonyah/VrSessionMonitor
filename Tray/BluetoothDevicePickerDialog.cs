using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Bluetooth;

namespace VrSessionMonitor.Tray;

/// <summary>
/// Picks a device from what the passive scan has actually seen, and chooses which apps it starts.
///
/// A picker rather than a text field for the same reason the process picker exists: a BLE address
/// must match exactly, a wrong one fails silently, and nobody can type one from memory. Many
/// devices never advertise a name at all, so the list also shows signal strength and whether the
/// device advertises the Heart Rate service — often the only way to tell which row is your strap.
/// </summary>
public sealed class BluetoothDevicePickerDialog : Form
{
    private readonly ListView _list;
    private readonly CheckedListBox _apps;
    private readonly TextBox _name;
    private readonly Label _selectionLabel;
    private readonly List<BleDeviceSighting> _sightings;
    private readonly List<ManagedApp> _launchable;

    /// <summary>Set once the user types their own name, so following the selection stops
    /// overwriting their choice. Distinguished from our own writes by _suppressNameEdit.</summary>
    private bool _nameEditedByUser;
    private bool _suppressNameEdit;

    public BluetoothDeviceConfig? SelectedDevice { get; private set; }

    public BluetoothDevicePickerDialog(IReadOnlyList<BleDeviceSighting> sightings, MonitorConfig config)
    {
        _sightings = sightings
            .OrderByDescending(s => s.LooksLikeHeartRateMonitor)   // the one you're most likely after
            .ThenByDescending(s => s.Rssi)                          // then nearest first
            .ToList();

        // Steam-launched apps are excluded: the Bluetooth trigger starts processes directly and
        // does not go through the steam:// path, so offering them would promise something that
        // silently does nothing.
        _launchable = config.ManagedApps
            .Where(a => a.LaunchMethod == AppLaunchMethod.Executable)
            .OrderBy(a => a.Order)
            .ToList();

        Text = "Track a Bluetooth device";
        Width = 620;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        _list = new ListView { Dock = DockStyle.Top, Height = 200, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Address", 140);
        _list.Columns.Add("Signal", 70);
        _list.Columns.Add("Type", 140);
        foreach (var s in _sightings)
        {
            var item = new ListViewItem(s.AdvertisedName is { Length: > 0 } ? s.AdvertisedName : "(no name advertised)");
            item.SubItems.Add(s.Address);
            item.SubItems.Add($"{s.Rssi} dBm");
            item.SubItems.Add(s.LooksLikeHeartRateMonitor ? "Heart rate monitor" : "");
            item.Tag = s;
            _list.Items.Add(item);
        }
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();

        _name = new TextBox { Dock = DockStyle.Top, PlaceholderText = "What to call it (optional)" };
        _name.TextChanged += (_, _) => { if (!_suppressNameEdit) _nameEditedByUser = true; };

        _selectionLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(3, 2, 3, 0),
            Text = "No device selected.",
        };

        _apps = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        foreach (var app in _launchable) _apps.Items.Add(app.DisplayName);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        var ok = new Button { Text = "Track", AutoSize = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_apps);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 22, Text = "Start these apps when it appears:", Padding = new Padding(3, 4, 3, 0) });
        Controls.Add(_name);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 22, Text = "Name:", Padding = new Padding(3, 4, 3, 0) });
        Controls.Add(_selectionLabel);
        Controls.Add(_list);
        Controls.Add(buttons);
        CancelButton = cancel;

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    /// <summary>
    /// Keeps the name box and the confirmation line following the selected row.
    ///
    /// An earlier version only filled the name when the box was empty, so after the first row
    /// populated it, selecting any other device left the previous device's name in place. The
    /// entry that got saved then carried the right address with the wrong name — and since the
    /// Bluetooth tab lists DisplayName first, it read as though the picker had ignored the click
    /// and re-added the earlier device.
    ///
    /// A name the user typed themselves is never overwritten; that is the one case where the box
    /// should not follow the selection.
    /// </summary>
    private void OnSelectionChanged()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BleDeviceSighting s)
        {
            _selectionLabel.Text = "No device selected.";
            return;
        }

        if (!_nameEditedByUser)
        {
            _suppressNameEdit = true;
            _name.Text = s.AdvertisedName ?? "";
            _suppressNameEdit = false;
        }

        // Always shows the address, so what is about to be saved is never in doubt — the name can
        // be blank, shared between devices, or edited, but the address is what gets matched.
        _selectionLabel.Text = $"Will track {s.Address}"
                               + (s.AdvertisedName is { Length: > 0 } ? $"  ({s.AdvertisedName})" : "  (no name advertised)")
                               + (s.LooksLikeHeartRateMonitor ? "  — heart rate monitor" : "");
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BleDeviceSighting sighting)
        {
            MessageBox.Show(this, "Pick a device first.", "VR Session Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SelectedDevice = new BluetoothDeviceConfig
        {
            Address = sighting.Address,
            DisplayName = _name.Text.Trim(),
            StartAppIds = _apps.CheckedIndices.Cast<int>().Select(i => _launchable[i].Id).ToList(),
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
