using System.Text.Json;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Bluetooth;

/// <summary>
/// Remembers what each Bluetooth address called itself, so a device is recognisable straight away
/// instead of after however long it takes a naming packet to arrive.
///
/// Names are not in every advertisement — devices alternate packet types, and with active scanning
/// the name usually arrives in a scan response rather than the advertisement itself. Within a
/// session BleDeviceRegistry carries the last known name forward, but that dies with the process,
/// so after every restart the picker shows "(no name advertised)" until the right packet lands.
/// Caching to disk removes that gap.
///
/// Capped, because many devices rotate their address for privacy (a resolvable private address
/// typically changes every 15 minutes). Those entries are junk almost immediately, and in a busy
/// RF environment they would otherwise accumulate without limit — a single 20-second scan here saw
/// 23 devices, most of them randomised. When the cap is reached the least recently seen entries go
/// first, which is exactly the right order: rotating addresses are never seen twice, while a real
/// device is refreshed every time it appears.
/// </summary>
public sealed class BluetoothNameCache
{
    /// <summary>Comfortably more than any real set of devices, small enough that rotating
    /// addresses cannot grow the file without bound.</summary>
    public const int MaxEntries = 500;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Func<DateTime> _clock;

    public BluetoothNameCache(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    private sealed record Entry(string Name, DateTime LastSeenUtc);

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>Records a name actually advertised by a device. Blank names are ignored rather
    /// than stored, so a sparse packet cannot erase a good name.</summary>
    public void Remember(string address, string? name)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(name)) return;

        lock (_lock)
        {
            _entries[address] = new Entry(name, _clock());
            if (_entries.Count > MaxEntries) PruneLocked();
        }
    }

    /// <summary>Refreshes an existing entry's recency without needing a name — so a device seen
    /// regularly survives pruning even when its packets carry no name.</summary>
    public void Touch(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;

        lock (_lock)
        {
            if (_entries.TryGetValue(address, out var existing))
                _entries[address] = existing with { LastSeenUtc = _clock() };
        }
    }

    public string? Lookup(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        lock (_lock) return _entries.TryGetValue(address, out var e) ? e.Name : null;
    }

    /// <summary>Drops the least recently seen entries down to 90% of the cap, so pruning is not
    /// re-triggered by the very next sighting.</summary>
    private void PruneLocked()
    {
        var keep = (int)(MaxEntries * 0.9);
        foreach (var address in _entries.OrderBy(kv => kv.Value.LastSeenUtc)
                     .Take(_entries.Count - keep).Select(kv => kv.Key).ToList())
            _entries.Remove(address);
    }

    // --- persistence -------------------------------------------------------
    // Its own file rather than a section of appsettings.json: this is machine-observed state that
    // rewrites itself constantly, and mixing it into the hand-editable config would bury the
    // user's real settings under hundreds of MAC addresses.

    public void Save(string path)
    {
        try
        {
            Dictionary<string, Entry> snapshot;
            lock (_lock) snapshot = new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);

            var directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

            // Write-then-replace, so an interrupted save cannot leave a truncated cache behind.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // A name cache is a convenience; never let it break anything.
            Log.Debug("Bluetooth", $"Could not save the device name cache: {ex.Message}");
        }
    }

    public void Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
            if (loaded is null) return;

            lock (_lock)
            {
                _entries.Clear();
                foreach (var (address, entry) in loaded)
                    if (!string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(entry.Name))
                        _entries[address] = entry;

                if (_entries.Count > MaxEntries) PruneLocked();
            }
        }
        catch (Exception ex)
        {
            // A corrupt cache is not worth a crash, or even a warning — it rebuilds itself.
            Log.Debug("Bluetooth", $"Could not read the device name cache: {ex.Message}");
        }
    }
}
