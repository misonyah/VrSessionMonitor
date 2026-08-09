using System;
using System.Linq;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

public class VirtualHereSRanipalLifecycleManagerTests
{
    private sealed class Ctx
    {
        public MonitorConfig Config = new();
        public FakeHeadsetMonitor Headset = new();
        public FakeProcessLauncher Launcher = new();
        public bool ViveTracker;
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public VirtualHereSRanipalLifecycleManager Mgr = null!;

        public VirtualHereSRanipalLifecycleManager Build()
        {
            Config.VirtualHereSRanipalLifecycle.ShutdownDelayMs = 300000;
            Mgr = new VirtualHereSRanipalLifecycleManager(
                Config, Headset, () => ViveTracker, Launcher, () => Now);
            return Mgr;
        }
    }

    [Fact]
    public async Task Headset_online_launches_both_processes()
    {
        var c = new Ctx(); c.Build();
        c.Headset.SetOnline(true);
        await c.Mgr.TickForTestAsync();
        Assert.Contains("vhui64", c.Launcher.EnsureRunningCalls);
        Assert.Contains("sr_runtime", c.Launcher.EnsureRunningCalls);
    }

    [Fact]
    public async Task No_presence_shuts_both_down_after_delay()
    {
        var c = new Ctx(); c.Build();
        c.Headset.SetOnline(true);
        await c.Mgr.TickForTestAsync();              // launched (Running)
        c.Headset.SetOnline(false);
        await c.Mgr.TickForTestAsync();              // ShuttingDown
        c.Now = c.Now.AddMilliseconds(300001);
        await c.Mgr.TickForTestAsync();              // kill both
        Assert.Contains("vhui64", c.Launcher.KillCalls);
        Assert.Contains("sr_runtime", c.Launcher.KillCalls);
    }

    [Fact]
    public async Task Vive_tracker_alone_keeps_it_running()
    {
        var c = new Ctx(); c.Build();
        c.Headset.SetOnline(true);
        await c.Mgr.TickForTestAsync();              // Running, launched
        c.Headset.SetOnline(false);
        c.ViveTracker = true;                        // headset off but tracker in use
        await c.Mgr.TickForTestAsync();
        c.Now = c.Now.AddMilliseconds(300001);
        await c.Mgr.TickForTestAsync();
        Assert.Empty(c.Launcher.KillCalls);          // never shut down while tracker present
    }

    [Fact]
    public async Task Disabled_config_is_a_no_op()
    {
        var c = new Ctx();
        c.Config.VirtualHereSRanipalLifecycle.Enabled = false;
        c.Build();
        c.Headset.SetOnline(true);
        await c.Mgr.TickForTestAsync();
        Assert.Empty(c.Launcher.EnsureRunningCalls);
    }
}
