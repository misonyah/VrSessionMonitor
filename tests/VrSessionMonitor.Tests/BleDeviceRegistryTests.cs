using System;
using System.Collections.Generic;
using System.Linq;
using VrSessionMonitor.Modules.Bluetooth;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Presence has to be asymmetric: one advertisement proves a device is there, but a missing one
/// proves nothing. BLE devices advertise every few hundred milliseconds and dropped packets are
/// routine, so treating a single gap as "gone" would flap constantly — and with apps wired to the
/// appear event, flapping means repeated launches.
/// </summary>
public class BleDeviceRegistryTests
{
    private const string Addr = "AA:BB:CC:DD:EE:FF";
    private const string Other = "11:22:33:44:55:66";

    private sealed class FakeClock
    {
        private DateTime _now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private static (BleDeviceRegistry, FakeClock) Build(int timeoutSeconds = 30)
    {
        var clock = new FakeClock();
        return (new BleDeviceRegistry(TimeSpan.FromSeconds(timeoutSeconds), clock.Now), clock);
    }

    [Fact]
    public void First_sighting_makes_a_device_present_immediately()
    {
        var (registry, _) = Build();
        var appeared = new List<BleDeviceSighting>();
        registry.DeviceAppeared += appeared.Add;

        var isNew = registry.Observe(Addr, "Polar H10", -60);

        Assert.True(isNew);
        Assert.True(registry.IsPresent(Addr));
        Assert.Single(appeared);
    }

    [Fact]
    public void Repeated_sightings_do_not_re_raise_appeared()
    {
        // The launch trigger hangs off this event; re-raising it would start apps over and over.
        var (registry, clock) = Build();
        var appeared = 0;
        registry.DeviceAppeared += _ => appeared++;

        registry.Observe(Addr, "Polar H10", -60);
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(1);
            registry.Observe(Addr, "Polar H10", -61);
        }

        Assert.Equal(1, appeared);
    }

    [Fact]
    public void A_device_goes_away_only_after_the_timeout()
    {
        var (registry, clock) = Build(timeoutSeconds: 30);
        var gone = new List<BleDeviceSighting>();
        registry.DeviceDisappeared += gone.Add;

        registry.Observe(Addr, "Polar H10", -60);

        clock.Advance(29);
        Assert.Empty(registry.ExpireStale());
        Assert.True(registry.IsPresent(Addr));

        clock.Advance(2);
        Assert.Single(registry.ExpireStale());
        Assert.False(registry.IsPresent(Addr));
        Assert.Single(gone);
    }

    [Fact]
    public void A_missed_packet_does_not_drop_presence()
    {
        // The whole point of the timeout: gaps shorter than it are invisible.
        var (registry, clock) = Build(timeoutSeconds: 30);
        var gone = 0;
        registry.DeviceDisappeared += _ => gone++;

        registry.Observe(Addr, "Polar H10", -60);
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(10);      // a 10s gap, well inside the window
            registry.ExpireStale();
            registry.Observe(Addr, "Polar H10", -60);
        }

        Assert.Equal(0, gone);
        Assert.True(registry.IsPresent(Addr));
    }

    [Fact]
    public void Coming_back_after_a_timeout_raises_appeared_again()
    {
        var (registry, clock) = Build(timeoutSeconds: 30);
        var appeared = 0;
        registry.DeviceAppeared += _ => appeared++;

        registry.Observe(Addr, "Polar H10", -60);
        clock.Advance(31);
        registry.ExpireStale();
        registry.Observe(Addr, "Polar H10", -60);

        Assert.Equal(2, appeared);
        Assert.True(registry.IsPresent(Addr));
    }

    [Fact]
    public void A_sparser_packet_does_not_erase_the_name_or_services()
    {
        // Advertisement types alternate and only some carry the name or the service list. Letting
        // a sparse packet overwrite them makes the device's name flicker in the UI.
        var (registry, _) = Build();
        registry.Observe(Addr, "Polar H10", -60, new[] { BleDeviceSighting.HeartRateServiceUuid });

        registry.Observe(Addr, null, -62, Array.Empty<string>());

        var last = registry.LastSighting(Addr);
        Assert.Equal("Polar H10", last!.AdvertisedName);
        Assert.True(last.LooksLikeHeartRateMonitor);
    }

    [Fact]
    public void A_heart_rate_strap_identifies_itself_by_service_uuid()
    {
        // Means a strap is recognisable without anyone knowing its name or address in advance.
        var (registry, _) = Build();
        registry.Observe(Addr, null, -60, new[] { BleDeviceSighting.HeartRateServiceUuid });
        registry.Observe(Other, "Some Toy", -70, new[] { "0000fff0-0000-1000-8000-00805f9b34fb" });

        Assert.True(registry.LastSighting(Addr)!.LooksLikeHeartRateMonitor);
        Assert.False(registry.LastSighting(Other)!.LooksLikeHeartRateMonitor);
    }

    [Fact]
    public void Discovery_remembers_devices_that_are_no_longer_present()
    {
        // EverSeen backs the settings picker, so a device that has gone quiet must still be
        // selectable rather than forcing the user to type its address.
        var (registry, clock) = Build(timeoutSeconds: 30);
        registry.Observe(Addr, "Polar H10", -60);
        clock.Advance(31);
        registry.ExpireStale();

        Assert.False(registry.IsPresent(Addr));
        Assert.Contains(registry.EverSeen, s => s.Address == Addr);
    }

    [Fact]
    public void Stopping_the_scan_clears_presence_without_claiming_devices_left()
    {
        // "We stopped looking" is not "it went away" — raising disappeared here would fire
        // whatever a user wired to that event on every radio toggle.
        var (registry, _) = Build();
        var gone = 0;
        registry.DeviceDisappeared += _ => gone++;
        registry.Observe(Addr, "Polar H10", -60);

        registry.ResetPresence();

        Assert.False(registry.IsPresent(Addr));
        Assert.Equal(0, gone);
    }

    [Fact]
    public void Devices_are_tracked_independently()
    {
        var (registry, clock) = Build(timeoutSeconds: 30);
        registry.Observe(Addr, "Polar H10", -60);
        clock.Advance(20);
        registry.Observe(Other, "Toy", -70);

        clock.Advance(15); // Addr is now 35s stale, Other only 15s
        registry.ExpireStale();

        Assert.False(registry.IsPresent(Addr));
        Assert.True(registry.IsPresent(Other));
    }

    [Fact]
    public void Addresses_match_case_insensitively()
    {
        var (registry, _) = Build();
        registry.Observe(Addr.ToLowerInvariant(), "Polar H10", -60);

        Assert.True(registry.IsPresent(Addr.ToUpperInvariant()));
    }

    [Fact]
    public void A_blank_address_is_ignored()
    {
        var (registry, _) = Build();

        Assert.False(registry.Observe("", "x", -60));
        Assert.Empty(registry.Present);
    }

    [Fact]
    public void An_out_of_range_notification_does_not_make_a_device_present()
    {
        // Windows raises a Received event with RSSI -127 to say a device has gone out of range.
        // It is a sentinel, not a weak signal — counting it as a sighting says the device is here
        // at the exact moment Windows is reporting that it is not.
        var (registry, _) = Build();

        var isNew = registry.Observe(Addr, "LVS-Gush", BleDeviceRegistry.NoSignalRssi);

        Assert.False(isNew);
        Assert.False(registry.IsPresent(Addr));
    }

    [Fact]
    public void An_out_of_range_notification_does_not_resurrect_a_departed_device()
    {
        // The exact flapping seen live on 2026-08-27 after a toy was switched off: gone, then an
        // RSSI -127 event brought it back, then it timed out again, on a ~30s cycle.
        var (registry, clock) = Build(timeoutSeconds: 30);
        var appeared = 0;
        registry.DeviceAppeared += _ => appeared++;

        registry.Observe(Addr, "LVS-Gush", -70);
        clock.Advance(31);
        registry.ExpireStale();
        Assert.False(registry.IsPresent(Addr));

        registry.Observe(Addr, "LVS-Gush", BleDeviceRegistry.NoSignalRssi);

        Assert.False(registry.IsPresent(Addr));
        Assert.Equal(1, appeared); // not re-raised, so nothing re-launches apps
    }

    [Fact]
    public void An_out_of_range_notification_does_not_keep_a_present_device_alive()
    {
        // If -127 refreshed last-seen, a powered-off device would never time out at all.
        var (registry, clock) = Build(timeoutSeconds: 30);
        registry.Observe(Addr, "LVS-Gush", -70);

        clock.Advance(20);
        registry.Observe(Addr, "LVS-Gush", BleDeviceRegistry.NoSignalRssi);
        clock.Advance(15); // 35s since the last REAL sighting

        Assert.Single(registry.ExpireStale());
        Assert.False(registry.IsPresent(Addr));
    }

    [Fact]
    public void Genuinely_weak_but_real_signals_still_count()
    {
        // Only the sentinel is rejected; a distant device is still a device.
        var (registry, _) = Build();

        Assert.True(registry.Observe(Addr, "far away", -120));
        Assert.True(registry.IsPresent(Addr));
    }
}
