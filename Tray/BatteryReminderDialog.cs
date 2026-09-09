using VrSessionMonitor.Modules.Battery;

namespace VrSessionMonitor.Tray;

/// <summary>
/// Shown once a session ends, listing devices worth charging.
///
/// It says plainly that these are the LAST KNOWN readings rather than live ones, because they
/// necessarily are: once SteamVR stops the devices are unreachable, so the numbers are however old
/// the final sample was. Presenting them as current would be a small lie that matters the one time
/// a device died mid-session.
/// </summary>
public sealed class BatteryReminderDialog : Form
{
    public BatteryReminderDialog(IReadOnlyList<DeviceBattery> devices, int sampleIntervalMinutes)
    {
        Text = "Charge these devices";
        Width = 720;
        Height = 320;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        // Not TopMost: the session just ended, the user may still have the headset on, and
        // stealing focus from whatever they moved to is worse than waiting to be noticed.
        ShowInTaskbar = true;

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(10, 8, 10, 0),
            Text = $"Your VR session has ended. These devices are worth charging — last known readings, "
                   + $"sampled every {sampleIntervalMinutes} minutes while the session was running.",
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
        };
        list.Columns.Add("Device", 240);
        list.Columns.Add("Serial number", 170);
        list.Columns.Add("Battery", 80, HorizontalAlignment.Right);
        list.Columns.Add("Charging", 80);
        list.Columns.Add("Last checked", 120);

        foreach (var device in devices)
        {
            var item = new ListViewItem(device.Assignment);
            item.SubItems.Add(device.Serial);
            item.SubItems.Add($"{device.Percent:0}%");
            item.SubItems.Add(device.Charging ? "Yes" : "No");
            item.SubItems.Add(device.SampledAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));

            // Red for the ones that will not survive another session; amber for the rest.
            item.ForeColor = device.Percent <= 15 ? Color.FromArgb(200, 0, 0) : Color.FromArgb(180, 95, 0);
            list.Items.Add(item);
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(8),
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.Add(close);

        Controls.Add(list);
        Controls.Add(buttons);
        Controls.Add(header);
        AcceptButton = close;
        CancelButton = close;
    }
}
