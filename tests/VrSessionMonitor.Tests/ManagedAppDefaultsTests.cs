using System.Linq;
using VrSessionMonitor.Config;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>Migration must preserve current behaviour exactly — a user upgrading into this feature
/// should see no change in which apps auto-start or how VRChat opens.</summary>
public class ManagedAppDefaultsTests
{
    private static MonitorConfig BaseConfig() => new();

    [Fact]
    public void Seeds_all_known_apps()
    {
        var apps = ManagedAppDefaults.SeedFrom(BaseConfig());

        foreach (var id in new[] { "vrchat", "slimevr", "vrcosc", "vrcfacetracking", "baballonia", "sranipal", "virtualhere", "ovrtoolkit", "xsoverlay" })
            Assert.Contains(apps, a => a.Id == id);
    }

    [Fact]
    public void Ids_are_unique_and_orders_are_distinct()
    {
        var apps = ManagedAppDefaults.SeedFrom(BaseConfig());

        Assert.Equal(apps.Count, apps.Select(a => a.Id).Distinct().Count());
        Assert.Equal(apps.Count, apps.Select(a => a.Order).Distinct().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VrChat_enabled_follows_AutoLaunchVrChat(bool autoLaunch)
    {
        var config = BaseConfig();
        config.SessionFlow.AutoLaunchVrChat = autoLaunch;

        var vrchat = ManagedAppDefaults.SeedFrom(config).Single(a => a.Id == "vrchat");

        Assert.Equal(autoLaunch, vrchat.Enabled);
    }

    [Fact]
    public void VrChat_launches_via_steam_app_id()
    {
        var config = BaseConfig();

        var vrchat = ManagedAppDefaults.SeedFrom(config).Single(a => a.Id == "vrchat");

        Assert.Equal(AppLaunchMethod.SteamAppId, vrchat.LaunchMethod);
        Assert.Equal(config.Paths.VrChatSteamAppId, vrchat.Target);
        Assert.Equal("VRChat", vrchat.ProcessName);
    }

    /// <summary>Background mode was the only existing window-ish setting — it must carry over as a
    /// real window rule (start minimized) rather than being silently dropped. TargetMonitor must
    /// NOT be seeded from VrChatBackgroundMonitor: the old background-mode code never relocated the
    /// window, and MoveToMonitor preserves window size rather than resizing it, so seeding a
    /// monitor here would move VRChat's deliberately-small background window somewhere it never
    /// used to go — a real behaviour change, not upgrade-safe carryover.</summary>
    [Fact]
    public void VrChat_background_mode_seeds_minimized_without_relocating()
    {
        var config = BaseConfig();
        config.SessionFlow.VrChatBackgroundMode = true;
        config.SessionFlow.VrChatBackgroundMonitor = 2;

        var vrchat = ManagedAppDefaults.SeedFrom(config).Single(a => a.Id == "vrchat");

        Assert.Equal(AppWindowState.Minimized, vrchat.WindowState);
        Assert.Null(vrchat.TargetMonitor);
    }

    [Fact]
    public void VrChat_without_background_mode_leaves_window_unchanged()
    {
        var config = BaseConfig();
        config.SessionFlow.VrChatBackgroundMode = false;

        var vrchat = ManagedAppDefaults.SeedFrom(config).Single(a => a.Id == "vrchat");

        Assert.Equal(AppWindowState.Unchanged, vrchat.WindowState);
        Assert.Null(vrchat.TargetMonitor);
    }

    [Theory]
    [InlineData(VrOverlayChoice.None, false, false)]
    [InlineData(VrOverlayChoice.OvrToolkit, true, false)]
    [InlineData(VrOverlayChoice.XSOverlay, false, true)]
    public void Overlay_picker_maps_to_which_overlay_app_is_enabled(VrOverlayChoice choice, bool ovrEnabled, bool xsEnabled)
    {
        var config = BaseConfig();
        config.SessionFlow.VrOverlay = choice;

        var apps = ManagedAppDefaults.SeedFrom(config);

        Assert.Equal(ovrEnabled, apps.Single(a => a.Id == "ovrtoolkit").Enabled);
        Assert.Equal(xsEnabled, apps.Single(a => a.Id == "xsoverlay").Enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lifecycle_toggles_map_to_their_apps(bool enabled)
    {
        var config = BaseConfig();
        config.BaballoniaLifecycle.Enabled = enabled;
        config.VrcFaceTrackingLifecycle.Enabled = enabled;
        config.VrcOscLifecycle.Enabled = enabled;
        config.VirtualHereSRanipalLifecycle.Enabled = enabled;
        config.SlimeVrLifecycle.Enabled = enabled;

        var apps = ManagedAppDefaults.SeedFrom(config);

        Assert.Equal(enabled, apps.Single(a => a.Id == "baballonia").Enabled);
        Assert.Equal(enabled, apps.Single(a => a.Id == "vrcfacetracking").Enabled);
        Assert.Equal(enabled, apps.Single(a => a.Id == "vrcosc").Enabled);
        Assert.Equal(enabled, apps.Single(a => a.Id == "sranipal").Enabled);
        Assert.Equal(enabled, apps.Single(a => a.Id == "slimevr").Enabled);
    }

    /// <summary>SlimeVR needs manual calibration checking, so it is the one app seeded to come to
    /// the front rather than being left alone.</summary>
    [Fact]
    public void SlimeVr_is_seeded_to_come_to_front()
    {
        var slime = ManagedAppDefaults.SeedFrom(BaseConfig()).Single(a => a.Id == "slimevr");

        Assert.True(slime.BringToFront);
    }

    [Fact]
    public void Executable_apps_carry_their_configured_paths()
    {
        var config = BaseConfig();

        var apps = ManagedAppDefaults.SeedFrom(config);

        Assert.Equal(config.Paths.SlimeVrExe, apps.Single(a => a.Id == "slimevr").Target);
        Assert.Equal(config.Paths.VrcOscExe, apps.Single(a => a.Id == "vrcosc").Target);
        Assert.Equal(config.Paths.SRanipalExe, apps.Single(a => a.Id == "sranipal").Target);
    }
}
