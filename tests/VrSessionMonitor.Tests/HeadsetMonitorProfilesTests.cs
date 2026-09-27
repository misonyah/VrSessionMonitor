using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class HeadsetMonitorProfilesTests
{
    private static (HeadsetMonitor mon, List<HeadsetStateChangedEventArgs> ev, HashSet<string> up, List<string> pinged) Build(System.Action<MonitorConfig>? tweak = null)
    {
        var c = new MonitorConfig();
        c.Network.HeadsetIp = "10.0.0.5";
        HeadsetProfiles.SeedDefaults(c);              // quest2 -> 10.0.0.5, frame -> "frame"
        c.Polling.HeadsetOfflineDebounceFailures = 1;
        tweak?.Invoke(c);
        var up = new HashSet<string>();
        var pinged = new List<string>();
        var mon = new HeadsetMonitor(c, h => { pinged.Add(h); return Task.FromResult(up.Contains(h)); });
        var ev = new List<HeadsetStateChangedEventArgs>();
        mon.StateChanged += (_, e) => ev.Add(e);
        return (mon, ev, up, pinged);
    }

    [Fact]
    public async Task Frame_answering_makes_frame_active()
    {
        var (mon, ev, up, _) = Build();
        up.Add("frame");
        await mon.CheckOnceAsync();
        Assert.True(mon.IsOnline);
        Assert.Equal("frame", mon.ActiveHeadsetId);
        Assert.Equal("frame", Assert.Single(ev).HeadsetId);
    }

    [Fact]
    public async Task Both_online_prefers_last_active()
    {
        var (mon, _, up, _) = Build(c => c.LastActiveHeadset = "frame");
        up.Add("10.0.0.5"); up.Add("frame");
        await mon.CheckOnceAsync();
        Assert.Equal("frame", mon.ActiveHeadsetId);
    }

    [Fact]
    public async Task Pinned_headset_pings_only_that_profile()
    {
        var (mon, _, up, pinged) = Build(c => c.PinnedHeadset = "quest2");
        up.Add("frame");
        await mon.CheckOnceAsync();
        Assert.False(mon.IsOnline);
        Assert.DoesNotContain("frame", pinged);
    }

    [Fact]
    public async Task Switching_headset_goes_through_offline_then_new_headset_online()
    {
        var (mon, ev, up, _) = Build();              // debounce = 1 failure
        up.Add("10.0.0.5");
        await mon.CheckOnceAsync();
        up.Clear(); up.Add("frame");
        await mon.CheckOnceAsync();                   // quest misses -> offline (debounce exhausted)
        await mon.CheckOnceAsync();                   // now probe all -> frame online
        Assert.Equal(3, ev.Count);
        Assert.False(ev[1].IsOnline);
        Assert.True(ev[2].IsOnline);
        Assert.Equal("frame", ev[2].HeadsetId);
    }

    [Fact]
    public async Task Missed_ping_of_active_headset_does_not_switch_to_other_headset()
    {
        var (mon, ev, up, _) = Build(c => c.Polling.HeadsetOfflineDebounceFailures = 3);
        up.Add("10.0.0.5"); up.Add("frame");
        await mon.CheckOnceAsync();                   // quest (list order, no last-active) online
        Assert.Equal("quest2", mon.ActiveHeadsetId);
        up.Remove("10.0.0.5");                        // quest misses one ping; frame still answers
        await mon.CheckOnceAsync();
        Assert.True(mon.IsOnline);
        Assert.Equal("quest2", mon.ActiveHeadsetId);
        Assert.Single(ev);                            // no switch event
    }

    [Fact]
    public async Task Offline_clears_active_id()
    {
        var (mon, ev, up, _) = Build();
        up.Add("frame");
        await mon.CheckOnceAsync();
        up.Clear();
        await mon.CheckOnceAsync();
        Assert.False(mon.IsOnline);
        Assert.Equal("", mon.ActiveHeadsetId);
        Assert.False(ev[^1].IsOnline);
    }
}
