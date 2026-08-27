using System;
using System.IO;
using VrSessionMonitor.Modules.Bluetooth;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Names arrive in only some advertisement packets, so without a cache a device is anonymous after
/// every restart until the right packet lands. The cache has to survive that gap without growing
/// without bound, because many devices rotate their address for privacy and each rotation looks
/// like a brand new device.
/// </summary>
public class BluetoothNameCacheTests : IDisposable
{
    private readonly string _dir;

    public BluetoothNameCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vrsm-btnames-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private sealed class FakeClock
    {
        private DateTime _now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    [Fact]
    public void A_remembered_name_is_returned_for_that_address()
    {
        var cache = new BluetoothNameCache();

        cache.Remember("AA:BB:CC:DD:EE:FF", "H9Z 42975");

        Assert.Equal("H9Z 42975", cache.Lookup("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void Addresses_match_case_insensitively()
    {
        var cache = new BluetoothNameCache();
        cache.Remember("aa:bb:cc:dd:ee:ff", "H9Z 42975");

        Assert.Equal("H9Z 42975", cache.Lookup("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void A_blank_name_never_overwrites_a_good_one()
    {
        // Sparse packets carry no name; storing that would erase what we already knew.
        var cache = new BluetoothNameCache();
        cache.Remember("AA:BB:CC:DD:EE:FF", "H9Z 42975");

        cache.Remember("AA:BB:CC:DD:EE:FF", null);
        cache.Remember("AA:BB:CC:DD:EE:FF", "  ");

        Assert.Equal("H9Z 42975", cache.Lookup("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void An_unknown_address_returns_null()
    {
        Assert.Null(new BluetoothNameCache().Lookup("11:22:33:44:55:66"));
    }

    [Fact]
    public void Names_survive_a_save_and_reload()
    {
        // The whole point: a device is recognisable immediately after a restart.
        var path = Path_("names.json");
        var cache = new BluetoothNameCache();
        cache.Remember("AA:BB:CC:DD:EE:FF", "H9Z 42975");
        cache.Save(path);

        var reloaded = new BluetoothNameCache();
        reloaded.Load(path);

        Assert.Equal("H9Z 42975", reloaded.Lookup("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void Loading_a_missing_file_is_harmless()
    {
        var cache = new BluetoothNameCache();
        cache.Load(Path_("does-not-exist.json"));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_corrupt_file_is_ignored_rather_than_thrown()
    {
        // A convenience cache must never be able to stop the app starting.
        var path = Path_("corrupt.json");
        File.WriteAllText(path, "{ this is not json");

        var cache = new BluetoothNameCache();
        cache.Load(path);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void The_cache_is_capped()
    {
        // Rotating privacy addresses would otherwise grow this file without limit.
        var cache = new BluetoothNameCache();

        for (var i = 0; i < BluetoothNameCache.MaxEntries + 200; i++)
            cache.Remember($"AA:BB:CC:00:{i / 256:X2}:{i % 256:X2}", $"device {i}");

        Assert.True(cache.Count <= BluetoothNameCache.MaxEntries);
    }

    [Fact]
    public void Pruning_drops_the_least_recently_seen_first()
    {
        // A real device is re-seen constantly; a rotated address is never seen again. Evicting by
        // recency is what keeps the devices that matter and discards the noise.
        var clock = new FakeClock();
        var cache = new BluetoothNameCache(clock.Now);

        cache.Remember("AA:AA:AA:AA:AA:AA", "the real device");

        for (var i = 0; i < BluetoothNameCache.MaxEntries + 100; i++)
        {
            clock.Advance(1);
            cache.Remember($"BB:BB:BB:00:{i / 256:X2}:{i % 256:X2}", $"random {i}");
            if (i % 50 == 0) cache.Touch("AA:AA:AA:AA:AA:AA"); // still being seen
        }

        Assert.Equal("the real device", cache.Lookup("AA:AA:AA:AA:AA:AA"));
    }

    [Fact]
    public void Touch_does_not_invent_an_entry()
    {
        // Touch only refreshes recency; a device with no known name must not gain a blank one.
        var cache = new BluetoothNameCache();

        cache.Touch("AA:BB:CC:DD:EE:FF");

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Lookup("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void The_registry_uses_a_cached_name_when_a_packet_carries_none()
    {
        // The restart case, end to end: the advertisement has no name, but the device is still
        // identifiable because it was named in an earlier session.
        var cache = new BluetoothNameCache();
        cache.Remember("AA:BB:CC:DD:EE:FF", "H9Z 42975");

        var registry = new BleDeviceRegistry(TimeSpan.FromSeconds(30), nameCache: cache);
        registry.Observe("AA:BB:CC:DD:EE:FF", null, -70);

        Assert.Equal("H9Z 42975", registry.LastSighting("AA:BB:CC:DD:EE:FF")!.AdvertisedName);
    }

    [Fact]
    public void A_live_name_still_beats_the_cached_one()
    {
        // If a device renames itself, the advertisement is the truth and the cache must follow.
        var cache = new BluetoothNameCache();
        cache.Remember("AA:BB:CC:DD:EE:FF", "old name");

        var registry = new BleDeviceRegistry(TimeSpan.FromSeconds(30), nameCache: cache);
        registry.Observe("AA:BB:CC:DD:EE:FF", "new name", -70);

        Assert.Equal("new name", registry.LastSighting("AA:BB:CC:DD:EE:FF")!.AdvertisedName);
        Assert.Equal("new name", cache.Lookup("AA:BB:CC:DD:EE:FF"));
    }
}
