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

    // Regression for the OR-signal crash-recovery gap (2026-08-09): isRunning is
    // IsRunning("vhui64") || IsRunning("sr_runtime"), so if only sr_runtime dies the OR stays true
    // and the machine's own "not running -> ensure" branch never fires. Before runningTick was
    // wired to EnsureBothRunningAsync, the dead sr_runtime was never relaunched until vhui64 ALSO
    // died. This test fails without that fix (sr_runtime is not re-ensured) and passes with it.
    [Fact]
    public async Task Sr_runtime_crash_while_vhui64_alive_is_relaunched()
    {
        var c = new Ctx(); c.Build();
        c.Headset.SetOnline(true);
        await c.Mgr.TickForTestAsync();              // both launch -> Running
        Assert.Equal(PresenceState.Running, c.Mgr.State);

        // Simulate ONLY sr_runtime crashing; vhui64 stays up, so the OR signal stays true.
        c.Launcher.Running.Remove("sr_runtime");
        var callsBefore = c.Launcher.EnsureRunningCalls.Count;

        await c.Mgr.TickForTestAsync();              // Running tick must re-ensure the missing one

        var newCalls = c.Launcher.EnsureRunningCalls.GetRange(
            callsBefore, c.Launcher.EnsureRunningCalls.Count - callsBefore);
        Assert.Contains("sr_runtime", newCalls);     // re-ensured (relaunched)
        Assert.True(c.Launcher.IsRunning("sr_runtime"));
        Assert.Equal(PresenceState.Running, c.Mgr.State);
    }
}
