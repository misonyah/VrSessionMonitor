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
    private readonly List<BleDeviceSighting> _sightings;
    private readonly List<ManagedApp> _launchable;

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
        Controls.Add(_list);
        Controls.Add(buttons);
        CancelButton = cancel;

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    private void OnSelectionChanged()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BleDeviceSighting s) return;
        if (_name.Text.Length == 0 && s.AdvertisedName is { Length: > 0 })
            _name.Text = s.AdvertisedName;
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
