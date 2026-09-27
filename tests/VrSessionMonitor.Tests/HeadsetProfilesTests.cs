using System.IO;
using System.Linq;
using VrSessionMonitor.Config;
using Xunit;

namespace VrSessionMonitor.Tests;

public class HeadsetProfilesTests
{
    [Fact]
    public void Empty_list_synthesizes_quest_profile_from_legacy_network_fields()
    {
        var c = new MonitorConfig();
        c.Network.HeadsetIp = "10.0.0.5";
        c.Network.HeadsetIpSecondary = "192.168.137.20";
        var eff = HeadsetProfiles.Effective(c);
        var q = Assert.Single(eff);
        Assert.Equal(HeadsetProfiles.QuestId, q.Id);
        Assert.Equal(new[] { "10.0.0.5", "192.168.137.20" }, q.DetectHosts);
        Assert.Equal(HeadsetSessionKind.VirtualDesktop, q.SessionKind);
        Assert.True(q.Part(HeadsetPartNames.Adb));
    }

    [Fact]
    public void SeedDefaults_adds_quest_and_frame_once()
    {
        var c = new MonitorConfig();
        c.Network.HeadsetIp = "10.0.0.5";
        HeadsetProfiles.SeedDefaults(c);
        Assert.Equal(new[] { "quest2", "frame" }, c.Headsets.Select(h => h.Id));
        var frame = c.Headsets[1];
        Assert.Equal(HeadsetSessionKind.SteamLink, frame.SessionKind);
        Assert.Equal(new[] { "frame" }, frame.DetectHosts);
        Assert.Equal("", frame.HotspotKeeperTask);   // public repo: no personal defaults

        // User deletes the Frame profile -> must not come back
        c.Headsets.RemoveAt(1);
        HeadsetProfiles.SeedDefaults(c);
        Assert.Equal(new[] { "quest2" }, c.Headsets.Select(h => h.Id));
    }

    [Fact]
    public void SyncQuestFromNetwork_follows_edited_legacy_ip_fields()
    {
        var c = new MonitorConfig();
        c.Network.HeadsetIp = "10.0.0.5";
        HeadsetProfiles.SeedDefaults(c);
        c.Network.HeadsetIp = "10.0.0.9";           // Settings tab / auto-detect writes the old field
        c.Network.HeadsetIpSecondary = "192.168.137.20";
        HeadsetProfiles.SyncQuestFromNetwork(c);
        Assert.Equal(new[] { "10.0.0.9", "192.168.137.20" }, HeadsetProfiles.Find(c, "quest2")!.DetectHosts);
        Assert.Equal(new[] { "frame" }, HeadsetProfiles.Find(c, "frame")!.DetectHosts);
    }

    [Fact]
    public void Profile_with_no_hosts_is_not_detectable()
    {
        var c = new MonitorConfig();
        c.Headsets.Add(new HeadsetProfile { Id = "x", DetectHosts = new() { " ", "" } });
        Assert.Empty(HeadsetProfiles.Effective(c).Where(HeadsetProfiles.IsDetectable));
    }

    [Fact]
    public void Find_is_case_insensitive_and_null_safe()
    {
        var c = new MonitorConfig();
        HeadsetProfiles.SeedDefaults(c);
        Assert.Equal("frame", HeadsetProfiles.Find(c, "FRAME")!.Id);
        Assert.Null(HeadsetProfiles.Find(c, null));
        Assert.Null(HeadsetProfiles.Find(c, "nope"));
    }

    [Fact]
    public void Round_trips_through_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vsm-hp-{System.Guid.NewGuid():N}.json");
        try
        {
            var c = MonitorConfig.CreateDefault();
            c.PinnedHeadset = "frame";
            c.Headsets[1].HeadsetCommands.Add(new HeadsetCommand { Name = "t", Command = "true" });
            c.Save(path);
            var l = MonitorConfig.LoadOrCreateDefault(path);
            Assert.Equal("frame", l.PinnedHeadset);
            Assert.Equal(2, l.Headsets.Count);
            Assert.Equal(HeadsetSessionKind.SteamLink, l.Headsets[1].SessionKind);
            Assert.True(l.Headsets[1].Part(HeadsetPartNames.HotspotKeeper));
            Assert.Equal("true", Assert.Single(l.Headsets[1].HeadsetCommands).Command);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Concurrent_saves_from_several_threads_do_not_throw()
    {
        // LastActiveHeadset is saved from HeadsetMonitor's thread while UI handlers save too.
        var path = Path.Combine(Path.GetTempPath(), $"vsm-hp-{System.Guid.NewGuid():N}.json");
        try
        {
            var c = MonitorConfig.CreateDefault();
            c.Save(path);
            System.Threading.Tasks.Parallel.For(0, 64, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => c.Save(path));
            Assert.Equal(2, MonitorConfig.LoadOrCreateDefault(path).Headsets.Count);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public void Unknown_session_kind_binds_to_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vsm-hp-{System.Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"Headsets\":[{\"Id\":\"q\",\"DetectHosts\":[\"1.2.3.4\"],\"SessionKind\":\"Bogus\"}],\"SeededHeadsetIds\":[\"quest2\",\"frame\"]}");
            var l = MonitorConfig.LoadOrCreateDefault(path);
            Assert.Equal(HeadsetSessionKind.VirtualDesktop, Assert.Single(l.Headsets).SessionKind);
        }
        finally { File.Delete(path); }
    }
}
