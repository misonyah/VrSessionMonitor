using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using Windows.Devices.Bluetooth.Advertisement;

namespace VrSessionMonitor.Modules.Bluetooth;

/// <summary>
/// Watches BLE advertisements to tell which of the user's devices are switched on.
///
/// PASSIVE SCANNING ONLY. This never connects. A BLE peripheral accepts exactly one central
/// connection, so connecting to the heart rate strap would steal it from VRCOSC and connecting to
/// a toy would steal it from Intiface. Passive scanning is unlimited and invisible to the device:
/// it never transmits, so it also cannot provoke a scan response or drain the device's battery.
///
/// This class is deliberately thin — it converts WinRT events into registry calls and nothing
/// else. All the debouncing logic lives in BleDeviceRegistry, which is testable without a radio.
/// </summary>
public sealed class BluetoothPresenceMonitor : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly BleDeviceRegistry _registry;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private System.Threading.Timer? _expiryTimer;
    private bool _startFailureLogged;

    public BluetoothPresenceMonitor(MonitorConfig config)
    {
        _config = config;
        _registry = new BleDeviceRegistry(TimeSpan.FromSeconds(Math.Max(5, config.Bluetooth.PresenceTimeoutSeconds)));
    }

    public BleDeviceRegistry Registry => _registry;

    /// <summary>Raised when a tracked device appears, carrying its config entry so a caller can
    /// act on StartAppIds without re-resolving it.</summary>
    public event Action<BluetoothDeviceConfig, BleDeviceSighting>? TrackedDeviceAppeared;

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public void Start()
    {
        if (!_config.Bluetooth.Enabled)
        {
            Log.Info("Bluetooth", "Bluetooth presence detection is disabled in config — not scanning.");
            return;
        }

        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                // Neither mode ever connects, which is the property that keeps this from stealing
                // the heart rate strap from VRCOSC or a toy from Intiface. Active additionally
                // sends a scan request, which is how device NAMES arrive — a real passive scan
                // here found 23 devices and not one name, leaving only MAC addresses to choose
                // between. Passive stays available for anyone who would rather never transmit.
                ScanningMode = _config.Bluetooth.ActiveScanning
                    ? BluetoothLEScanningMode.Active
                    : BluetoothLEScanningMode.Passive,
            };
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Stopped += OnWatcherStopped;
            _watcher.Start();

            _registry.DeviceAppeared += OnDeviceAppeared;
            _registry.DeviceDisappeared += OnDeviceDisappeared;

            // Presence expiry is time-based, so it needs its own tick — no advertisement arrives
            // to tell us a device has gone.
            _expiryTimer = new System.Threading.Timer(_ => SafeExpire(), null, 2000, 2000);

            Log.Info("Bluetooth", $"BLE scan started ({(_config.Bluetooth.ActiveScanning ? "active — requests names, still never connects" : "passive — listen only, most devices will show no name")}). Tracking {_config.Bluetooth.Devices.Count} configured device(s); presence times out after {_config.Bluetooth.PresenceTimeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            // No adapter, radio off, or the capability denied. All are normal on some machines and
            // none should stop the app — presence detection simply stays unavailable.
            Log.Warn("Bluetooth", $"Could not start BLE scanning: {ex.Message}. Presence detection is unavailable — check that Bluetooth is switched on.");
            _watcher = null;
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var address = FormatAddress(args.BluetoothAddress);
            var uuids = args.Advertisement.ServiceUuids.Select(u => u.ToString()).ToList();
            _registry.Observe(address, args.Advertisement.LocalName, args.RawSignalStrengthInDBm, uuids);
        }
        catch (Exception ex)
        {
            Log.Debug("Bluetooth", $"Malformed advertisement ignored: {ex.Message}");
        }
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        // Windows stops the watcher by itself when the radio is switched off or the adapter is
        // removed. Presence is cleared without raising "disappeared" for everything, because the
        // devices did not go anywhere — we simply stopped looking.
        _registry.ResetPresence();
        if (_startFailureLogged) return;

        _startFailureLogged = true;
        Log.Warn("Bluetooth", $"BLE scanning stopped ({args.Error}). This usually means the Bluetooth radio was turned off.");
    }

    private void SafeExpire()
    {
        try { _registry.ExpireStale(); }
        catch (Exception ex) { Log.Debug("Bluetooth", $"Presence expiry pass threw: {ex.Message}"); }
    }

    private void OnDeviceAppeared(BleDeviceSighting sighting)
    {
        var tracked = _config.Bluetooth.GetDevice(sighting.Address);
        var label = DescribeFor(tracked, sighting);

        if (tracked is null)
        {
            Log.Debug("Bluetooth", $"Saw an untracked device: {label} (RSSI {sighting.Rssi}).");
            return;
        }

        Log.Info("Bluetooth", $"{label} is present (RSSI {sighting.Rssi}).");
        TrackedDeviceAppeared?.Invoke(tracked, sighting);
    }

    private void OnDeviceDisappeared(BleDeviceSighting sighting)
    {
        var tracked = _config.Bluetooth.GetDevice(sighting.Address);
        if (tracked is null) return;

        Log.Info("Bluetooth", $"{DescribeFor(tracked, sighting)} is no longer visible.");
    }

    /// <summary>
    /// Always includes the address, never just a name.
    ///
    /// An earlier version returned the name alone when one was known, which made a real bug much
    /// harder to diagnose: a device logged as "H9Z 42975" gave no way to tell which address it
    /// actually was, so a saved entry pairing that name with a different device's address looked
    /// self-consistent in the log. The address is the identity here; the name is a label that can
    /// be blank, duplicated across devices, or edited by the user.
    /// </summary>
    private static string DescribeFor(BluetoothDeviceConfig? tracked, BleDeviceSighting sighting)
    {
        var label = tracked is { DisplayName.Length: > 0 } ? tracked.DisplayName
            : sighting.AdvertisedName is { Length: > 0 } ? sighting.AdvertisedName
            : sighting.LooksLikeHeartRateMonitor ? "heart rate monitor"
            : null;

        if (label is null) return sighting.Address;

        // When the user's label disagrees with what the device actually calls itself, show both.
        // A mislabelled entry is otherwise invisible: the log would keep repeating the wrong name
        // back, which is exactly how a device saved under another device's name went unnoticed.
        var advertised = sighting.AdvertisedName;
        var mismatch = advertised is { Length: > 0 }
                       && !string.Equals(advertised, label, StringComparison.OrdinalIgnoreCase);

        return mismatch
            ? $"{label} [{sighting.Address}, advertises itself as \"{advertised}\"]"
            : $"{label} [{sighting.Address}]";
    }

    /// <summary>WinRT hands the address over as a 48-bit integer; render it the way every other
    /// tool shows a MAC so it can be matched against config and recognised by a human.</summary>
    public static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => bytes[i].ToString("X2")));
    }

    public void Dispose()
    {
        _expiryTimer?.Dispose();
        _expiryTimer = null;

        if (_watcher is not null)
        {
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped -= OnWatcherStopped;
            try { _watcher.Stop(); } catch { /* already stopped, or the radio went away */ }
            _watcher = null;
        }

        _registry.DeviceAppeared -= OnDeviceAppeared;
        _registry.DeviceDisappeared -= OnDeviceDisappeared;
    }
}
