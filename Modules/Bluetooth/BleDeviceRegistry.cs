namespace VrSessionMonitor.Modules.Bluetooth;

/// <summary>What a passive scan can learn about a device, which is presence and not much else.</summary>
public sealed record BleDeviceSighting(
    string Address,
    string? AdvertisedName,
    short Rssi,
    DateTime LastSeenUtc,
    IReadOnlyList<string> ServiceUuids)
{
    /// <summary>Standard Heart Rate Service. A strap advertising this identifies itself without
    /// anyone having to recognise its name.</summary>
    public const string HeartRateServiceUuid = "0000180d-0000-1000-8000-00805f9b34fb";

    public bool LooksLikeHeartRateMonitor =>
        ServiceUuids.Any(u => string.Equals(u, HeartRateServiceUuid, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Turns a stream of BLE advertisement sightings into stable present/absent state.
///
/// Separated from the WinRT watcher so the interesting part — debouncing a noisy radio into
/// appear/disappear events — is testable without a Bluetooth adapter, the same split used for
/// AfkHoldGate and LightSetting.
///
/// Appearing is immediate: one advertisement proves the device is there. Disappearing needs a
/// timeout, because missed packets are completely routine — devices advertise every few hundred
/// milliseconds, cheap adapters drop plenty, and a device that walked behind your body should not
/// read as switched off. Without that asymmetry, presence flaps constantly.
/// </summary>
public sealed class BleDeviceRegistry
{
    private readonly TimeSpan _presenceTimeout;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, BleDeviceSighting> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BleDeviceSighting> _everSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public BleDeviceRegistry(TimeSpan presenceTimeout, Func<DateTime>? clock = null)
    {
        _presenceTimeout = presenceTimeout;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised the first time a device is seen after being absent. Never re-raised while
    /// it stays present, so a subscriber can treat it as an edge.</summary>
    public event Action<BleDeviceSighting>? DeviceAppeared;

    /// <summary>Raised once the presence timeout elapses with no further sighting.</summary>
    public event Action<BleDeviceSighting>? DeviceDisappeared;

    /// <summary>Everything seen since the scan started, present or not — the discovery list the
    /// settings picker offers, so nobody has to type a BLE address by hand.</summary>
    public IReadOnlyCollection<BleDeviceSighting> EverSeen
    {
        get { lock (_lock) return _everSeen.Values.ToList(); }
    }

    public IReadOnlyCollection<BleDeviceSighting> Present
    {
        get { lock (_lock) return _present.Values.ToList(); }
    }

    public bool IsPresent(string address)
    {
        lock (_lock) return _present.ContainsKey(address);
    }

    public BleDeviceSighting? LastSighting(string address)
    {
        lock (_lock) return _everSeen.GetValueOrDefault(address);
    }

    /// <summary>Feeds in one advertisement. Returns true when this made the device newly present.</summary>
    public bool Observe(string address, string? name, short rssi, IReadOnlyList<string>? serviceUuids = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        BleDeviceSighting sighting;
        bool isNew;

        lock (_lock)
        {
            // Advertisement packets alternate between types, and only some carry the name or the
            // service list. Carry forward what we already know rather than overwriting it with the
            // nulls of a sparser packet — otherwise a device's name flickers in and out of the UI.
            var previous = _everSeen.GetValueOrDefault(address);
            sighting = new BleDeviceSighting(
                address,
                string.IsNullOrWhiteSpace(name) ? previous?.AdvertisedName : name,
                rssi,
                _clock(),
                serviceUuids is { Count: > 0 } ? serviceUuids : previous?.ServiceUuids ?? Array.Empty<string>());

            _everSeen[address] = sighting;
            isNew = !_present.ContainsKey(address);
            _present[address] = sighting;
        }

        if (isNew) DeviceAppeared?.Invoke(sighting);
        return isNew;
    }

    /// <summary>Expires devices unseen for longer than the timeout. Called on a timer by the
    /// monitor; returns the ones that just went away.</summary>
    public IReadOnlyList<BleDeviceSighting> ExpireStale()
    {
        List<BleDeviceSighting> gone;

        lock (_lock)
        {
            var cutoff = _clock() - _presenceTimeout;
            gone = _present.Values.Where(s => s.LastSeenUtc < cutoff).ToList();
            foreach (var s in gone) _present.Remove(s.Address);
        }

        foreach (var s in gone) DeviceDisappeared?.Invoke(s);
        return gone;
    }

    /// <summary>Clears presence without raising events — used when the scan stops, where "we are
    /// no longer looking" must not be reported as "the device went away".</summary>
    public void ResetPresence()
    {
        lock (_lock) _present.Clear();
    }
}
