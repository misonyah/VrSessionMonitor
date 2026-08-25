using System.Linq;
using VrSessionMonitor.Config;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// SuppressUacPrompt is nullable so that a config written before the property existed keeps the
/// behaviour its launch sites already had. Getting this wrong is silent and user-hostile: the UAC
/// prompts that were fixed on 2026-07-16 would simply come back on upgrade.
/// </summary>
public class SuppressUacResolutionTests
{
    private static MonitorConfig ConfigWith(params ManagedApp[] apps)
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.AddRange(apps);
        return config;
    }

    [Fact]
    public void An_entry_predating_the_property_keeps_the_built_in_default()
    {
        // This is the upgrade case: the app exists in config, but SuppressUacPrompt was never
        // written, so it deserializes to null rather than false.
        var config = ConfigWith(new ManagedApp { Id = "sranipal", SuppressUacPrompt = null });

        Assert.True(config.SuppressUacFor("sranipal", builtInDefault: true));
    }

    [Fact]
    public void A_missing_entry_keeps_the_built_in_default()
    {
        var config = ConfigWith();

        Assert.True(config.SuppressUacFor("sranipal", builtInDefault: true));
        Assert.False(config.SuppressUacFor("slimevr", builtInDefault: false));
    }

    [Fact]
    public void An_explicit_false_really_turns_it_off()
    {
        // The whole point of the toggle: the user must be able to override the built-in default,
        // which is only distinguishable from "unset" because the property is nullable.
        var config = ConfigWith(new ManagedApp { Id = "sranipal", SuppressUacPrompt = false });

        Assert.False(config.SuppressUacFor("sranipal", builtInDefault: true));
    }

    [Fact]
    public void An_explicit_true_turns_it_on_for_an_app_that_defaulted_off()
    {
        var config = ConfigWith(new ManagedApp { Id = "vrcosc", SuppressUacPrompt = true });

        Assert.True(config.SuppressUacFor("vrcosc", builtInDefault: false));
    }

    [Fact]
    public void Seeding_turns_it_on_for_sranipal_and_leaves_everything_else_alone()
    {
        // sr_runtime's manifest asks for "highestAvailable"; every other launched binary on this
        // machine is asInvoker and would gain nothing from the shim.
        var config = new MonitorConfig();
        var apps = ManagedAppDefaults.SeedFrom(config);

        Assert.True(apps.Single(a => a.Id == "sranipal").SuppressUacPrompt);
        foreach (var app in apps.Where(a => a.Id != "sranipal"))
            Assert.NotEqual(true, app.SuppressUacPrompt);
    }

    [Fact]
    public void Id_matching_is_case_insensitive()
    {
        var config = ConfigWith(new ManagedApp { Id = "SRanipal", SuppressUacPrompt = false });

        Assert.False(config.SuppressUacFor("sranipal", builtInDefault: true));
    }
}
