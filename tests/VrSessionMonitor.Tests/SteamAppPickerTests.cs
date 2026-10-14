using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class SteamAppPickerTests
{
    private const string LibraryFoldersVdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"label\"\t\t\"\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"250820\"\t\t\"123\"\n\t\t}\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n";

    private const string AppManifest = "\"AppState\"\n{\n\t\"appid\"\t\t\"1009850\"\n\t\"universe\"\t\t\"1\"\n\t\"name\"\t\t\"OVR Advanced Settings\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"OVR_AdvancedSettings\"\n}\n";

    [Fact]
    public void Library_folders_are_read_with_backslashes_unescaped()
        => Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLibrary.ParseLibraryFolders(LibraryFoldersVdf));

    [Fact]
    public void App_manifest_gives_id_name_and_install_dir()
    {
        var app = SteamLibrary.ParseAppManifest(AppManifest)!;
        Assert.Equal("1009850", app.AppId);
        Assert.Equal("OVR Advanced Settings", app.Name);
        Assert.Equal("OVR_AdvancedSettings", app.InstallDir);
    }

    [Fact]
    public void Garbage_manifest_is_null()
        => Assert.Null(SteamLibrary.ParseAppManifest("not a manifest"));

    [Fact]
    public void Installed_apps_are_found_across_libraries_sorted_by_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-steam-" + Path.GetRandomFileName());
        var lib2 = Path.Combine(root, "lib2");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\"0\" {{ \"path\" \"{root.Replace("\\", "\\\\")}\" }}\n\"1\" {{ \"path\" \"{lib2.Replace("\\", "\\\\")}\" }}\n}}");
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_1009850.acf"), AppManifest);
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_438100.acf"), AppManifest.Replace("1009850", "438100").Replace("OVR Advanced Settings", "VRChat"));

        var apps = SteamLibrary.LoadInstalled(root);

        Assert.Equal(new[] { "OVR Advanced Settings", "VRChat" }, apps.Select(a => a.Name));
        Assert.True(SteamLibrary.IsInstalled(root, "438100"));
        Assert.False(SteamLibrary.IsInstalled(root, "999"));
    }

    [Fact]
    public void Process_name_guess_skips_crash_handlers_and_picks_the_main_exe()
    {
        var lib = Path.Combine(Path.GetTempPath(), "vsm-steamlib-" + Path.GetRandomFileName());
        var dir = Path.Combine(lib, "steamapps", "common", "OVR_AdvancedSettings");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "UnityCrashHandler64.exe"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(dir, "AdvancedSettings.exe"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(dir, "small.exe"), new byte[10]);
        var app = new SteamAppInfo("1009850", "OVR Advanced Settings", "OVR_AdvancedSettings", lib);
        Assert.Equal("AdvancedSettings", app.GuessProcessName());
    }

    [Fact]
    public void Ovr_advanced_settings_is_seeded_enabled_only_when_installed()
    {
        var installed = ManagedAppDefaults.SeedFrom(new MonitorConfig(), steamAppInstalled: id => id == "1009850")
            .Single(a => a.Id == "ovradvancedsettings");
        Assert.True(installed.Enabled);
        Assert.True(installed.StartWithSteamVr);
        Assert.Equal(AppLaunchMethod.SteamAppId, installed.LaunchMethod);
        Assert.Equal("1009850", installed.Target);
        Assert.Equal(new[] { "steam.overlay.1009850" }, installed.SteamVrAppKeys);

        var missing = ManagedAppDefaults.SeedFrom(new MonitorConfig(), steamAppInstalled: _ => false)
            .Single(a => a.Id == "ovradvancedsettings");
        Assert.False(missing.Enabled);
    }

    [Fact]
    public void Apps_to_start_with_steamvr_are_enabled_ticked_and_not_running()
    {
        var apps = new List<ManagedApp>
        {
            new() { Id = "a", Target = "1", Enabled = true, StartWithSteamVr = true, ProcessName = "A" },
            new() { Id = "b", Target = "1", Enabled = false, StartWithSteamVr = true, ProcessName = "B" },
            new() { Id = "c", Target = "1", Enabled = true, StartWithSteamVr = false, ProcessName = "C" },
            new() { Id = "d", Target = "1", Enabled = true, StartWithSteamVr = true, ProcessName = "D" },
        };
        var start = SteamVrStartApps.Select(apps, isRunning: name => name == "D");
        Assert.Equal(new[] { "a" }, start.Select(a => a.Id));
    }
}
