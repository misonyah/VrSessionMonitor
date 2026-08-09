using System;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

public class VrcFaceTrackingLifecycleManagerTests
{
    private sealed class Ctx
    {
        public MonitorConfig Config = new();
        public FakeProcessLauncher Launcher = new();
        public bool EyeCam;
        public bool ViveTracker;
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public VrcFaceTrackingLifecycleManager Mgr = null!;

        public VrcFaceTrackingLifecycleManager Build()
        {
            Config.VrcFaceTrackingLifecycle.ShutdownDelayMs = 30000;
            // Neutralize the two paths that read REAL OS processes (Process.GetProcessesByName),
            // which would otherwise record a phantom KillCall on this dev machine while it's running
            // a live VRChat + VRCFaceTracking session, flaking Assert.Empty(KillCalls):
            //   - MaxContinuousUptimeMs = 0 disables MaybeRestartForMaxUptime (via runningTick).
            //   - MinVrChatUptimeBeforeRestartMs = int.MaxValue makes CheckVrChatRestart never fire.
            Config.VrcFaceTrackingLifecycle.MaxContinuousUptimeMs = 0;
            Config.VrcFaceTrackingLifecycle.MinVrChatUptimeBeforeRestartMs = int.MaxValue;
            Mgr = new VrcFaceTrackingLifecycleManager(
                Config, () => EyeCam, () => ViveTracker, Launcher, () => Now);
            return Mgr;
        }
    }

    [Fact]
    public async Task Eye_camera_present_launches_vrcfacetracking()
    {
        var c = new Ctx(); c.Build();
        c.EyeCam = true;
        await c.Mgr.TickForTestAsync();
        Assert.Contains("VRCFaceTracking", c.Launcher.EnsureRunningCalls);
    }

    [Fact]
    public async Task Vive_tracker_present_launches_vrcfacetracking()
    {
        var c = new Ctx(); c.Build();
        c.ViveTracker = true;
        await c.Mgr.TickForTestAsync();
        Assert.Contains("VRCFaceTracking", c.Launcher.EnsureRunningCalls);
    }

    [Fact]
    public async Task No_tracker_shuts_down_after_delay()
    {
        var c = new Ctx(); c.Build();
        c.EyeCam = true;
        await c.Mgr.TickForTestAsync();          // Running
        c.EyeCam = false;
        await c.Mgr.TickForTestAsync();          // ShuttingDown
        c.Now = c.Now.AddMilliseconds(30001);
        await c.Mgr.TickForTestAsync();          // kill
        Assert.Contains("VRCFaceTracking", c.Launcher.KillCalls);
    }

    [Fact]
    public async Task Tracker_regained_mid_countdown_keeps_it_running()
    {
        var c = new Ctx(); c.Build();
        c.EyeCam = true;
        await c.Mgr.TickForTestAsync();
        c.EyeCam = false;
        await c.Mgr.TickForTestAsync();          // ShuttingDown
        c.Now = c.Now.AddSeconds(10);
        c.EyeCam = true;
        await c.Mgr.TickForTestAsync();          // back to Running
        c.Now = c.Now.AddMilliseconds(30001);
        await c.Mgr.TickForTestAsync();
        Assert.Empty(c.Launcher.KillCalls);
    }

    [Fact]
    public async Task Disabled_config_is_a_no_op()
    {
        var c = new Ctx();
        c.Config.VrcFaceTrackingLifecycle.Enabled = false;
        c.Build();
        c.EyeCam = true;
        await c.Mgr.TickForTestAsync();
        Assert.Empty(c.Launcher.EnsureRunningCalls);
    }
}
