namespace VrSessionMonitor.Config;

/// <summary>
/// One Bluetooth LE device the user cares about.
///
/// Identified by its address rather than its name: a name is optional in an advertisement, is
/// often absent from the packets a device sends most of the time, and several devices from the
/// same vendor share one. The address is stable and always present.
/// </summary>
public sealed class BluetoothDeviceConfig
{
    /// <summary>48-bit BLE address, formatted "AA:BB:CC:DD:EE:FF". The persistence key.</summary>
    public string Address { get; set; } = "";

    /// <summary>What to call it in the UI. Free text — the advertised name is used as a fallback
    /// when this is blank, and many devices never advertise one.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Show this device's presence in the tray tooltip and status view.</summary>
    public bool ShowInStatus { get; set; } = true;

    /// <summary>
    /// Managed app ids to start when this device appears. This is what turns "my toy is switched
    /// on" into "Intiface and OSCGoesBrrr are already running", instead of starting them by hand.
    ///
    /// Apps are started once per appearance, not repeatedly while the device stays visible, and
    /// never stopped when it disappears — quitting an app someone is actively using because a
    /// battery-saving device stopped advertising for a moment would be far worse than leaving it
    /// running.
    /// </summary>
    public List<string> StartAppIds { get; set; } = new();
}

/// <summary>
/// Passive BLE presence detection.
///
/// Scanning only — this never opens a GATT connection. That restraint is the whole design: a BLE
/// peripheral accepts exactly ONE central connection, so connecting to the heart rate strap would
/// take it away from VRCOSC, and connecting to a toy would take it from Intiface. Advertisement
/// scanning is passive and unlimited, so any number of apps can watch at once without interfering.
///
/// The consequence is that presence is all this can see. A heart rate VALUE lives behind a GATT
/// characteristic, not in the advertisement, so BPM has to come from VRCOSC over OSC.
/// </summary>
public sealed class BluetoothConfig
{
    /// <summary>Off by default: this starts a radio scan, which should be a deliberate choice.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Ask devices for a scan response, which is where most of them put their name.
    ///
    /// On by default because without it the device list is unusable: a real 20-second passive scan
    /// on this machine found 23 devices and not one advertised a name, so the picker would offer
    /// nothing but anonymous MAC addresses.
    ///
    /// This does NOT weaken the no-conflict guarantee. The thing that would steal the heart rate
    /// strap from VRCOSC, or a toy from Intiface, is opening a GATT CONNECTION — and neither
    /// scanning mode ever connects. Active scanning only transmits a short scan request, which any
    /// number of scanners may do at once. It costs slightly more radio power and is visible to
    /// nearby devices; turn it off if you would rather listen silently and identify devices by
    /// address alone.
    /// </summary>
    public bool ActiveScanning { get; set; } = true;

    /// <summary>
    /// How long a device may go unseen before it counts as gone. BLE devices advertise every few
    /// hundred milliseconds to a couple of seconds, and individual packets are missed routinely, so
    /// this must be comfortably longer than one interval or presence will flap constantly.
    /// </summary>
    public int PresenceTimeoutSeconds { get; set; } = 30;

    /// <summary>Devices to watch. Anything not listed is still discovered, so it can be picked in
    /// the settings UI, but is not tracked or acted on.</summary>
    public List<BluetoothDeviceConfig> Devices { get; set; } = new();

    /// <summary>
    /// Keep every device seen while scanning in a discovery list, so the settings UI can offer a
    /// picker instead of asking for a hand-typed address — the same reasoning that made the
    /// process picker worth building. Costs a little memory in a busy RF environment.
    /// </summary>
    public bool RememberDiscoveredDevices { get; set; } = true;

    public BluetoothDeviceConfig? GetDevice(string address) =>
        Devices.FirstOrDefault(d => string.Equals(d.Address, address, StringComparison.OrdinalIgnoreCase));
}
