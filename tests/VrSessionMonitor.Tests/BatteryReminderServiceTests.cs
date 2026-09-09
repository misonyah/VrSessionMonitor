using System;
using System.Collections.Generic;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Battery;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// The reminder exists because battery levels can only be read WHILE a session runs — once SteamVR
/// stops the devices are gone. So the value reported is necessarily the last one sampled while the
/// session was alive, and these pin that: readings must survive the session ending, and a failed
/// sample must not erase what was already known.
/// </summary>
public class BatteryReminderServiceTests
{
    private static MonitorConfig ConfigWith(int warnBelow = 30)
    {
        var config = new MonitorConfig();
        config.BatteryReminder.Enabled = true;
        config.BatteryReminder.WarnBelowPercent = warnBelow;
        return config;
    }

    private static DeviceBattery Device(string serial, double percent, bool charging = false) =>
        new(serial, $"Tracker {serial}", percent, charging, new DateTime(2026, 9, 9, 0, 22, 0, DateTimeKind.Utc));

    [Fact]
    public void A_low_device_is_reported_when_the_session_ends()
    {
        // The Tundra tracker at 22% from the real screenshot.
        var readings = new List<DeviceBattery> { Device("LHR-007F128C", 22) };
        var svc = new BatteryReminderService(ConfigWith(), () => readings);
        IReadOnlyList<DeviceBattery>? reported = null;
        svc.ChargeReminder += d => reported = d;

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();
        svc.HandleSteamVrRunningChanged(false);

        Assert.NotNull(reported);
        Assert.Single(reported!);
        Assert.Equal("LHR-007F128C", reported![0].Serial);
    }

    [Fact]
    public void A_healthy_device_produces_no_reminder()
    {
        var svc = new BatteryReminderService(ConfigWith(), () => new List<DeviceBattery> { Device("A", 85) });
        var fired = false;
        svc.ChargeReminder += _ => fired = true;

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();
        svc.HandleSteamVrRunningChanged(false);

        Assert.False(fired);
    }

    [Fact]
    public void A_charging_device_is_never_reported_however_low()
    {
        // It is already being dealt with; reminding about it is noise.
        var svc = new BatteryReminderService(ConfigWith(), () => new List<DeviceBattery> { Device("A", 3, charging: true) });

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();

        Assert.Empty(svc.LowDevices());
    }

    [Fact]
    public void A_failed_sample_does_not_erase_what_was_already_known()
    {
        // The child process can crash or SteamVR can briefly refuse; that must cost one sample, not
        // the reading that would otherwise be reported at the end.
        var readings = new List<DeviceBattery> { Device("A", 22) };
        var svc = new BatteryReminderService(ConfigWith(), () => readings);

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();
        readings = new List<DeviceBattery>(); // probe returns nothing
        svc.SampleNowForTests();

        Assert.Single(svc.LowDevices());
    }

    [Fact]
    public void The_latest_reading_for_a_device_replaces_the_earlier_one()
    {
        var readings = new List<DeviceBattery> { Device("A", 60) };
        var svc = new BatteryReminderService(ConfigWith(), () => readings);

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();
        readings = new List<DeviceBattery> { Device("A", 18) }; // drained over the session
        svc.SampleNowForTests();

        Assert.Single(svc.LowDevices());
        Assert.Equal(18, svc.LowDevices()[0].Percent);
    }

    [Fact]
    public void Readings_from_a_previous_session_are_discarded()
    {
        // Last week's 22% says nothing about today, and reporting it would train the user to
        // ignore the dialog.
        var readings = new List<DeviceBattery> { Device("A", 22) };
        var svc = new BatteryReminderService(ConfigWith(), () => readings);

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();
        svc.HandleSteamVrRunningChanged(false);

        readings = new List<DeviceBattery>(); // nothing readable in the new session
        svc.HandleSteamVrRunningChanged(true);

        Assert.Empty(svc.LowDevices());
    }

    [Fact]
    public void Devices_are_reported_lowest_first()
    {
        var svc = new BatteryReminderService(ConfigWith(), () => new List<DeviceBattery>
        {
            Device("A", 28), Device("B", 9), Device("C", 21),
        });

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();

        Assert.Equal(new[] { "B", "C", "A" }, svc.LowDevices().Select(d => d.Serial));
    }

    [Fact]
    public void Nothing_is_sampled_when_the_feature_is_disabled()
    {
        var config = ConfigWith();
        config.BatteryReminder.Enabled = false;
        var svc = new BatteryReminderService(config, () => new List<DeviceBattery> { Device("A", 5) });
        var fired = false;
        svc.ChargeReminder += _ => fired = true;

        svc.HandleSteamVrRunningChanged(true);
        svc.HandleSteamVrRunningChanged(false);

        Assert.False(fired);
    }

    [Fact]
    public void The_threshold_boundary_counts_as_low()
    {
        // "Warn below 30" reads as "30 is already worth charging" to a user setting it.
        var svc = new BatteryReminderService(ConfigWith(warnBelow: 30), () => new List<DeviceBattery> { Device("A", 30) });

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();

        Assert.Single(svc.LowDevices());
    }

    [Fact]
    public void A_device_with_a_blank_serial_is_ignored()
    {
        // Serial is the identity key; without one, later readings could not replace earlier ones.
        var svc = new BatteryReminderService(ConfigWith(), () => new List<DeviceBattery>
        {
            new("", "mystery device", 5, false, DateTime.UtcNow),
        });

        svc.HandleSteamVrRunningChanged(true);
        svc.SampleNowForTests();

        Assert.Empty(svc.LowDevices());
    }
}
