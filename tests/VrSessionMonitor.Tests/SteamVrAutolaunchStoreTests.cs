using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class SteamVrAutolaunchStoreTests
{
    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsm-vrapp-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "volcanicarts.vrcosc.vrappconfig"), "{\n   \"autolaunch\" : true,\n   \"last_launch_time\" : \"0\"\n}\n");
        File.WriteAllText(Path.Combine(d, "benaclejames.vrcft.vrappconfig"), "{ \"autolaunch\" : false, \"last_launch_time\" : \"0\" }");
        File.WriteAllText(Path.Combine(d, "broken.vrappconfig"), "{ not json");
        return d;
    }

    [Fact]
    public void ReadAll_lists_entries_and_skips_unreadable()
    {
        var s = new SteamVrAutolaunchStore(Dir());
        var all = s.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(e => e.AppKey == "volcanicarts.vrcosc").Autolaunch);
    }

    [Fact]
    public void TryDisable_sets_false_and_preserves_other_keys()
    {
        var d = Dir();
        var s = new SteamVrAutolaunchStore(d);
        Assert.True(s.TryDisable("volcanicarts.vrcosc", out _));
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(d, "volcanicarts.vrcosc.vrappconfig")))!;
        Assert.False(j["autolaunch"]!.GetValue<bool>());
        Assert.Equal("0", j["last_launch_time"]!.GetValue<string>());
    }

    [Fact]
    public void TryDisable_unknown_key_fails_cleanly()
    {
        var s = new SteamVrAutolaunchStore(Dir());
        Assert.False(s.TryDisable("nope", out var err));
        Assert.Contains("not found", err);
    }

    [Fact]
    public void Seeded_apps_carry_their_steamvr_app_keys()
    {
        var apps = ManagedAppDefaults.SeedFrom(new MonitorConfig());
        Assert.Equal(new[] { "volcanicarts.vrcosc" }, apps.Single(a => a.Id == "vrcosc").SteamVrAppKeys);
        Assert.Equal(new[] { "projectbabble.baballonia", "steam.overlay.4091970" }, apps.Single(a => a.Id == "baballonia").SteamVrAppKeys);
    }

    [Fact]
    public void Backfills_SteamVrAppKeys_for_existing_apps()
    {
        var c = new MonitorConfig();
        c.ManagedApps = ManagedAppDefaults.SeedFrom(c);
        foreach (var a in c.ManagedApps) a.SteamVrAppKeys.Clear();   // config written before this field
        ManagedAppDefaults.AddNewlyIntroducedApps(c);
        Assert.Equal(new[] { "volcanicarts.vrcosc" }, c.GetApp("vrcosc")!.SteamVrAppKeys);
    }
}
