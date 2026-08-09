using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class HeadsetMonitorDebounceTests
{
    // Builds a monitor whose ping returns the scripted queue of results (true=reachable).
    private static (HeadsetMonitor mon, List<HeadsetStateChangedEventArgs> events) Build(Queue<bool> pings)
    {
        var config = new MonitorConfig();
        config.Network.HeadsetIp = "10.0.0.5";
        config.Network.HeadsetIpSecondary = ""; // primary only for these cases

        var mon = new HeadsetMonitor(config, pingOverride: _ => Task.FromResult(pings.Count > 0 && pings.Dequeue()));
        var events = new List<HeadsetStateChangedEventArgs>();
        mon.StateChanged += (_, e) => events.Add(e);
        return (mon, events);
    }

    [Fact]
    public async Task First_success_transitions_online_and_raises_event()
    {
        var (mon, events) = Build(new Queue<bool>(new[] { true }));
        await mon.CheckOnceAsync();
        Assert.True(mon.IsOnline);
        Assert.Single(events);
        Assert.True(events[0].IsOnline);
    }

    [Fact]
    public async Task Single_failure_after_online_does_not_declare_offline()
    {
        // online, then one failure — debounce (3) keeps it online, no offline event.
        var (mon, events) = Build(new Queue<bool>(new[] { true, false }));
        await mon.CheckOnceAsync(); // online
        events.Clear();
        await mon.CheckOnceAsync(); // failure 1/3
        Assert.True(mon.IsOnline);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Three_consecutive_failures_declare_offline_once()
    {
        var (mon, events) = Build(new Queue<bool>(new[] { true, false, false, false }));
        await mon.CheckOnceAsync(); // online
        events.Clear();
        await mon.CheckOnceAsync(); // 1/3
        await mon.CheckOnceAsync(); // 2/3
        Assert.True(mon.IsOnline);  // still online at 2/3
        await mon.CheckOnceAsync(); // 3/3 -> offline
        Assert.False(mon.IsOnline);
        Assert.Single(events);
        Assert.False(events[0].IsOnline);
    }

    [Fact]
    public async Task Recovery_flips_online_on_first_success_after_failures()
    {
        var (mon, events) = Build(new Queue<bool>(new[] { true, false, false, false, true }));
        await mon.CheckOnceAsync(); // online
        await mon.CheckOnceAsync(); // 1/3
        await mon.CheckOnceAsync(); // 2/3
        await mon.CheckOnceAsync(); // 3/3 offline
        events.Clear();
        await mon.CheckOnceAsync(); // success -> online immediately
        Assert.True(mon.IsOnline);
        Assert.Single(events);
        Assert.True(events[0].IsOnline);
    }

    [Fact]
    public async Task Secondary_ip_answers_when_primary_fails()
    {
        var config = new MonitorConfig();
        config.Network.HeadsetIp = "10.0.0.5";
        config.Network.HeadsetIpSecondary = "192.168.137.5";
        // primary always fails, secondary always succeeds
        var mon = new HeadsetMonitor(config, pingOverride: ip => Task.FromResult(ip == "192.168.137.5"));

        await mon.CheckOnceAsync();

        Assert.True(mon.IsOnline);
        Assert.Equal("192.168.137.5", mon.RespondingIp);
    }
}
