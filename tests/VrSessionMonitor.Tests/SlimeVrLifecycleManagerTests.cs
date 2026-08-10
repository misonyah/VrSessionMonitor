using System;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

public class SlimeVrLifecycleManagerTests
{
    private sealed class Ctx
    {
        public MonitorConfig Config = new();
        public FakeProcessLauncher Launcher = new();
        public bool SteamVr, Headset, TrackersIdle;
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public SlimeVrLifecycleManager Mgr = null!;

        public SlimeVrLifecycleManager Build()
        {
            Config.SlimeVrLifecycle.ShutdownDelayMs = 300000;
            Mgr = new SlimeVrLifecycleManager(
                Config, () => SteamVr, () => Headset, () => TrackersIdle, Launcher, () => Now);
            return Mgr;
        }
    }

    [Fact]
    public async Task Does_not_stop_slimevr_while_steamvr_running()
    {
        var c = new Ctx(); c.Build();
        c.Launcher.Running.Add("SlimeVR");
        c.SteamVr = true; c.Headset = false; c.TrackersIdle = true;
        await c.Mgr.TickForTestAsync();
        c.Now = c.Now.AddMilliseconds(300001);
        await c.Mgr.TickForTestAsync();
        Assert.DoesNotContain("SlimeVR", c.Launcher.KillCalls);
    }

    [Fact]
    public async Task Does_not_stop_slimevr_while_trackers_active()
    {
        var c = new Ctx(); c.Build();
        c.Launcher.Running.Add("SlimeVR");
        c.SteamVr = false; c.Headset = false; c.TrackersIdle = false; // trackers still moving
        await c.Mgr.TickForTestAsync();
        c.Now = c.Now.AddMilliseconds(300001);
        await c.Mgr.TickForTestAsync();
        Assert.DoesNotContain("SlimeVR", c.Launcher.KillCalls);
    }

    [Fact]
    public async Task Stops_slimevr_after_delay_when_all_signals_inactive()
    {
        var c = new Ctx(); c.Build();
        c.Launcher.Running.Add("SlimeVR");
        c.SteamVr = true; c.Headset = true; c.TrackersIdle = false;
        await c.Mgr.TickForTestAsync();                 // Running
        c.SteamVr = false; c.Headset = false; c.TrackersIdle = true; // now fully done
        await c.Mgr.TickForTestAsync();                 // -> ShuttingDown
        c.Now = c.Now.AddMilliseconds(300001);
        await c.Mgr.TickForTestAsync();                 // stop
        Assert.Contains("SlimeVR", c.Launcher.KillCalls);
        Assert.Contains("orphan:java", c.Launcher.KillCalls); // java child reaped
    }

    [Fact]
    public async Task Never_launches_slimevr()
    {
        var c = new Ctx(); c.Build();
        // Present (SteamVR on) but SlimeVR not running — a launching manager would start it; this one must not.
        c.SteamVr = true;
        await c.Mgr.TickForTestAsync();
        await c.Mgr.TickForTestAsync();
        Assert.DoesNotContain("SlimeVR", c.Launcher.EnsureRunningCalls);
    }

    [Fact]
    public async Task Disabled_config_is_a_no_op()
    {
        var c = new Ctx();
        c.Config.SlimeVrLifecycle.Enabled = false;
        c.Build();
        c.Launcher.Running.Add("SlimeVR"); // leftover, all signals inactive
        await c.Mgr.TickForTestAsync();
        c.Now = c.Now.AddMilliseconds(300001);
        await c.Mgr.TickForTestAsync();
        Assert.DoesNotContain("SlimeVR", c.Launcher.KillCalls);
    }
}
