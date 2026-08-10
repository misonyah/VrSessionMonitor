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
            // CheckVrChatRestart / MaybeRestartForMaxUptime now read process start times through the
            // launcher seam (_launcher.GetStartTime), so the fake returns null for anything not in
            // FakeProcessLauncher.StartTimes — no real OS process is ever touched. The old
            // MaxContinuousUptimeMs=0 / MinVrChatUptimeBeforeRestartMs=int.MaxValue workaround is no
            // longer needed for hermeticity; leaving the shipped defaults exercises the real config.
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

    // These two exercise the previously-untestable start-time-comparison paths, now that they read
    // through the launcher seam (_launcher.GetStartTime) instead of real OS Process objects.

    [Fact]
    public async Task Max_uptime_exceeded_restarts_vrcfacetracking()
    {
        var c = new Ctx();
        c.Config.VrcFaceTrackingLifecycle.MaxContinuousUptimeMs = 3600000; // 1h limit
        c.Build();
        c.EyeCam = true;
        await c.Mgr.TickForTestAsync();   // launches (fake marks VRCFaceTracking running)
        // Report it as having been up for 2h — exceeds the 1h limit (compared against real DateTime.Now).
        c.Launcher.StartTimes["VRCFaceTracking"] = DateTime.Now.AddHours(-2);
        await c.Mgr.TickForTestAsync();   // Running-alive tick -> MaybeRestartForMaxUptime -> kill
        Assert.Contains("VRCFaceTracking", c.Launcher.KillCalls);
    }

    [Fact]
    public async Task Vrcfacetracking_predating_current_vrchat_is_restarted()
    {
        var c = new Ctx();
        c.Config.VrcFaceTrackingLifecycle.MinVrChatUptimeBeforeRestartMs = 0; // no min-uptime gate for the test
        c.Build();
        // VRChat came up 1 min ago; VRCFaceTracking has been up 5 min — it predates this VRChat
        // instance, so its OSC handshake is stale and CheckVrChatRestart should restart it.
        c.Launcher.Running.Add("VRChat");
        c.Launcher.Running.Add("VRCFaceTracking");
        c.Launcher.StartTimes["VRChat"] = DateTime.Now.AddMinutes(-1);
        c.Launcher.StartTimes["VRCFaceTracking"] = DateTime.Now.AddMinutes(-5);
        await c.Mgr.TickForTestAsync();   // CheckVrChatRestart runs first -> kill VRCFaceTracking
        Assert.Contains("VRCFaceTracking", c.Launcher.KillCalls);
    }
}
