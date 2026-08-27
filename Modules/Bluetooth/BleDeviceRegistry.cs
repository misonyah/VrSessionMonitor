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

    private readonly BluetoothNameCache? _nameCache;

    public BleDeviceRegistry(TimeSpan presenceTimeout, Func<DateTime>? clock = null, BluetoothNameCache? nameCache = null)
    {
        _presenceTimeout = presenceTimeout;
        _clock = clock ?? (() => DateTime.UtcNow);
        _nameCache = nameCache;
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

    /// <summary>
    /// The value Windows reports when it has no signal strength for a device — including the
    /// event it raises to say a device has gone OUT OF RANGE. It is a sentinel, not a very weak
    /// reading: real sightings here run about -66 to -100 dBm.
    ///
    /// Confirmed live 2026-08-27. Switching a toy off produced exactly this cycle: the device
    /// timed out and was reported gone, then ~29 seconds later an RSSI -127 event arrived, which
    /// this method counted as a fresh sighting and brought it back to life, which then timed out
    /// again — presence flapping on a roughly 30-second period long after the device was powered
    /// down. Treating the "it's gone" notification as proof it is here is precisely backwards.
    /// </summary>
    public const short NoSignalRssi = -127;

    /// <summary>Feeds in one advertisement. Returns true when this made the device newly present.</summary>
    public bool Observe(string address, string? name, short rssi, IReadOnlyList<string>? serviceUuids = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        // Never let an out-of-range notification refresh presence. Ignoring it entirely lets the
        // normal timeout retire the device, rather than this event holding it alive forever.
        if (rssi <= NoSignalRssi) return false;

        BleDeviceSighting sighting;
        bool isNew;

        lock (_lock)
        {
            // Advertisement packets alternate between types, and only some carry the name or the
            // service list. Carry forward what we already know rather than overwriting it with the
            // nulls of a sparser packet — otherwise a device's name flickers in and out of the UI.
            //
            // The name falls back through: this packet, what we saw earlier this session, then the
            // on-disk cache. That last step is what makes a device recognisable immediately after
            // a restart, rather than anonymous until a naming packet happens to arrive.
            var previous = _everSeen.GetValueOrDefault(address);
            var resolvedName = !string.IsNullOrWhiteSpace(name) ? name
                : previous?.AdvertisedName ?? _nameCache?.Lookup(address);

            sighting = new BleDeviceSighting(
                address,
                resolvedName,
                rssi,
                _clock(),
                serviceUuids is { Count: > 0 } ? serviceUuids : previous?.ServiceUuids ?? Array.Empty<string>());

            _everSeen[address] = sighting;
            isNew = !_present.ContainsKey(address);
            _present[address] = sighting;
        }

        // Outside the lock: persisting is the cache's own concern, and it takes its own.
        if (!string.IsNullOrWhiteSpace(name)) _nameCache?.Remember(address, name);
        else _nameCache?.Touch(address); // keep a regularly-seen device from being pruned

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
