using System.Linq;
using VrSessionMonitor.Config;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Apps introduced by a newer build have to reach configs that already have a managed-app list,
/// since plain seeding only runs when that list is empty. The hard part is doing it without
/// resurrecting an app the user deliberately deleted, which would otherwise come back on every
/// single launch.
/// </summary>
public class ManagedAppReconcileTests
{
    private static MonitorConfig ConfigWithApps(params string[] ids)
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        var order = 0;
        foreach (var id in ids)
            config.ManagedApps.Add(new ManagedApp { Id = id, DisplayName = id, Order = order++ });
        return config;
    }

    [Fact]
    public void A_newly_introduced_app_is_added_to_an_existing_config()
    {
        // The upgrade case: a config seeded before the haptics pair existed.
        var config = ConfigWithApps("vrchat", "slimevr", "vrcosc");

        ManagedAppDefaults.AddNewlyIntroducedApps(config);

        Assert.Contains(config.ManagedApps, a => a.Id == "intiface");
        Assert.Contains(config.ManagedApps, a => a.Id == "oscgoesbrrr");
    }

    [Fact]
    public void Apps_already_present_are_not_duplicated()
    {
        var config = ConfigWithApps("vrchat", "intiface");

        ManagedAppDefaults.AddNewlyIntroducedApps(config);

        Assert.Single(config.ManagedApps, a => a.Id == "intiface");
    }

    [Fact]
    public void A_deleted_app_stays_deleted_across_restarts()
    {
        // The bug this ledger exists to prevent: without it, "missing" is indistinguishable from
        // "never offered", and a removed app returns on every launch.
        var config = ConfigWithApps("vrchat");
        ManagedAppDefaults.AddNewlyIntroducedApps(config);
        Assert.Contains(config.ManagedApps, a => a.Id == "intiface");

        config.ManagedApps.RemoveAll(a => a.Id == "intiface");   // user deletes it
        ManagedAppDefaults.AddNewlyIntroducedApps(config);        // next launch

        Assert.DoesNotContain(config.ManagedApps, a => a.Id == "intiface");
    }

    [Fact]
    public void Apps_already_in_a_pre_ledger_config_are_never_re_offered()
    {
        // A config written before SeededAppIds existed has an empty ledger. Everything it already
        // holds must count as offered, or deleting any of them would resurrect it.
        var config = ConfigWithApps("vrchat", "slimevr");
        Assert.Empty(config.SeededAppIds);

        ManagedAppDefaults.AddNewlyIntroducedApps(config);
        config.ManagedApps.RemoveAll(a => a.Id == "slimevr");
        ManagedAppDefaults.AddNewlyIntroducedApps(config);

        Assert.DoesNotContain(config.ManagedApps, a => a.Id == "slimevr");
    }

    [Fact]
    public void Added_apps_get_orders_after_the_existing_ones()
    {
        var config = ConfigWithApps("vrchat", "slimevr");
        var highestBefore = config.ManagedApps.Max(a => a.Order);

        ManagedAppDefaults.AddNewlyIntroducedApps(config);

        foreach (var added in config.ManagedApps.Where(a => a.Id is "intiface" or "oscgoesbrrr"))
            Assert.True(added.Order > highestBefore);
    }

    [Fact]
    public void Orders_stay_unique_after_reconciling()
    {
        var config = ConfigWithApps("vrchat", "slimevr", "vrcosc");

        ManagedAppDefaults.AddNewlyIntroducedApps(config);

        var orders = config.ManagedApps.Select(a => a.Order).ToList();
        Assert.Equal(orders.Count, orders.Distinct().Count());
    }

    [Fact]
    public void Reconciling_twice_changes_nothing_the_second_time()
    {
        var config = ConfigWithApps("vrchat");

        ManagedAppDefaults.AddNewlyIntroducedApps(config);
        var afterFirst = config.ManagedApps.Count;
        ManagedAppDefaults.AddNewlyIntroducedApps(config);

        Assert.Equal(afterFirst, config.ManagedApps.Count);
    }

    [Fact]
    public void The_haptics_apps_are_seeded_disabled()
    {
        // They should only run in sessions where the hardware is actually in use — that is what
        // the Bluetooth trigger is for.
        var config = new MonitorConfig();

        var apps = ManagedAppDefaults.SeedFrom(config);

        Assert.False(apps.Single(a => a.Id == "intiface").Enabled);
        Assert.False(apps.Single(a => a.Id == "oscgoesbrrr").Enabled);
    }
}
