using System.Linq;
using VrSessionMonitor.Config;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// ManagedApp.Target is what actually gets launched; the pre-ManagedApps Paths entry is only a
/// fallback. These pin the resolution order, because getting it backwards fails silently — the app
/// launches from the wrong place rather than erroring, which is exactly how the "Target is edited
/// but ignored" bug behaved before this wiring existed.
/// </summary>
public class LaunchTargetResolutionTests
{
    private static MonitorConfig ConfigWith(params ManagedApp[] apps)
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.AddRange(apps);
        return config;
    }

    [Fact]
    public void Managed_target_wins_over_the_legacy_path()
    {
        var config = ConfigWith(new ManagedApp { Id = "slimevr", Target = @"D:\Custom\slimevr.exe" });

        Assert.Equal(@"D:\Custom\slimevr.exe", config.LaunchTargetFor("slimevr", @"C:\Legacy\slimevr.exe"));
    }

    [Fact]
    public void Falls_back_when_the_app_has_no_entry()
    {
        // A hand-edited config that dropped an entry must still launch, not break.
        var config = ConfigWith();

        Assert.Equal(@"C:\Legacy\slimevr.exe", config.LaunchTargetFor("slimevr", @"C:\Legacy\slimevr.exe"));
    }

    [Fact]
    public void Blank_target_falls_back_rather_than_launching_nothing()
    {
        // The Apps tab writes "" when a field is cleared. Treating that as authoritative would turn
        // clearing the box into an unlaunchable app.
        var config = ConfigWith(new ManagedApp { Id = "slimevr", Target = "" });

        Assert.Equal(@"C:\Legacy\slimevr.exe", config.LaunchTargetFor("slimevr", @"C:\Legacy\slimevr.exe"));
    }

    [Fact]
    public void Whitespace_only_target_is_treated_as_set()
    {
        // Documents a deliberate limit: only truly empty counts as unset. A stray space is a
        // user-visible value in the text box, so it is not silently second-guessed.
        var config = ConfigWith(new ManagedApp { Id = "slimevr", Target = " " });

        Assert.Equal(" ", config.LaunchTargetFor("slimevr", @"C:\Legacy\slimevr.exe"));
    }

    [Fact]
    public void Id_matching_is_case_insensitive()
    {
        var config = ConfigWith(new ManagedApp { Id = "SlimeVR", Target = @"D:\Custom\slimevr.exe" });

        Assert.Equal(@"D:\Custom\slimevr.exe", config.LaunchTargetFor("slimevr", @"C:\Legacy\slimevr.exe"));
    }

    [Fact]
    public void Steam_app_ids_resolve_the_same_way_as_exe_paths()
    {
        // VRChat and the overlays launch via steam://rungameid/<id>, so Target holds an app id
        // rather than a path. The resolver must not assume it is dealing with a filesystem path.
        var config = ConfigWith(new ManagedApp
        {
            Id = "vrchat",
            LaunchMethod = AppLaunchMethod.SteamAppId,
            Target = "999999",
        });

        Assert.Equal("999999", config.LaunchTargetFor("vrchat", "438100"));
    }

    [Fact]
    public void Seeded_config_resolves_every_app_to_its_seeded_path()
    {
        // The seeded state must be a no-op: every managed app resolves to exactly the legacy value
        // it was seeded from, so upgrading into this wiring changes nothing about what launches.
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.AddRange(ManagedAppDefaults.SeedFrom(config));

        Assert.Equal(config.Paths.SlimeVrExe, config.LaunchTargetFor("slimevr", config.Paths.SlimeVrExe));
        Assert.Equal(config.Paths.VrcOscExe, config.LaunchTargetFor("vrcosc", config.Paths.VrcOscExe));
        Assert.Equal(config.Paths.SRanipalExe, config.LaunchTargetFor("sranipal", config.Paths.SRanipalExe));
        Assert.Equal(config.Paths.VrChatSteamAppId, config.LaunchTargetFor("vrchat", config.Paths.VrChatSteamAppId));
    }

    [Fact]
    public void Unseeded_apps_always_take_the_legacy_path()
    {
        // Steam itself and the Virtual Desktop Streamer are launched but never seeded as managed
        // apps, so they resolve through the fallback. Pinned so that adding them later is a
        // deliberate change with a failing test, not an accident.
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.AddRange(ManagedAppDefaults.SeedFrom(config));

        Assert.DoesNotContain(config.ManagedApps, a => a.Id is "steam" or "virtualdesktop");
        Assert.Equal(config.Paths.SteamExe, config.LaunchTargetFor("steam", config.Paths.SteamExe));
        Assert.Equal(config.Paths.VirtualDesktopStreamerExe,
            config.LaunchTargetFor("virtualdesktop", config.Paths.VirtualDesktopStreamerExe));
    }
}
