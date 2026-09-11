using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Covers the flapping reported after a real session on 2026-09-11: tracker status kept
/// toggling while the boards were up. CheckOneAsync counted ConsecutiveFailures but never
/// consulted it, so one dropped ICMP packet declared a live tracker offline and the next good
/// ping undid it - the same bug already fixed for the headset and the eye camera.
/// </summary>
public class SlimeVrTrackerDebounceTests
{
    private const string Ip = "10.0.0.90";

    private static SlimeVrTrackerMonitor Build(Queue<bool> pings, int debounce = 3)
    {
        var config = new MonitorConfig();
        config.Trackers.Clear();
        config.Trackers.Add(new TrackerConfig { Name = "left-foot", Ip = Ip });
        // Pinned rather than inherited - the shipped default is free to move without silently
        // invalidating these cases (the headset tests were broken exactly that way).
        config.Polling.TrackerOfflineDebounceFailures = debounce;
        return new SlimeVrTrackerMonitor(config,
            pingOverride: _ => Task.FromResult(pings.Count > 0 && pings.Dequeue()));
    }

    [Fact]
    public async Task Single_dropped_ping_does_not_declare_offline()
    {
        var mon = Build(new Queue<bool>(new[] { true, false }));
        await mon.CheckAllAsync();
        await mon.CheckAllAsync();
        Assert.True(mon.Status[Ip].IsOnline);
        Assert.Equal(1, mon.Status[Ip].ConsecutiveFailures);
    }

    [Fact]
    public async Task Offline_only_after_the_configured_run_of_failures()
    {
        var mon = Build(new Queue<bool>(new[] { true, false, false, false }));
        await mon.CheckAllAsync();
        await mon.CheckAllAsync();
        await mon.CheckAllAsync();
        Assert.True(mon.Status[Ip].IsOnline);   // 2/3 - still online
        await mon.CheckAllAsync();
        Assert.False(mon.Status[Ip].IsOnline);  // 3/3 - now offline
    }

    [Fact]
    public async Task One_success_restores_online_immediately()
    {
        var mon = Build(new Queue<bool>(new[] { true, false, false, false, true }));
        for (var i = 0; i < 4; i++) await mon.CheckAllAsync();
        Assert.False(mon.Status[Ip].IsOnline);

        await mon.CheckAllAsync();
        Assert.True(mon.Status[Ip].IsOnline);
        Assert.Equal(0, mon.Status[Ip].ConsecutiveFailures);
    }

    [Fact]
    public async Task Alternating_drops_never_flap_the_status()
    {
        // The reported symptom: a board that answers most pings but misses the odd one must stay
        // continuously online, never toggling.
        var mon = Build(new Queue<bool>(new[] { true, false, true, false, true, false, true }));
        for (var i = 0; i < 7; i++)
        {
            await mon.CheckAllAsync();
            Assert.True(mon.Status[Ip].IsOnline);
        }
    }

    [Fact]
    public async Task A_board_that_never_answers_is_reported_offline()
    {
        var mon = Build(new Queue<bool>(new[] { false, false, false }));
        for (var i = 0; i < 3; i++) await mon.CheckAllAsync();
        Assert.False(mon.Status[Ip].IsOnline);
        Assert.Equal(0, mon.OnlineCount);
    }
}
