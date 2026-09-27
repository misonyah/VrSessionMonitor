using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Modules.Frame;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

public class SessionOrchestratorSteamLinkTests
{
    private sealed class FakeKeeper : IHotspotKeeper
    {
        public List<string> Triggered = new();
        public Task TriggerAndWaitAsync(string taskName, TimeSpan timeout) { Triggered.Add(taskName); return Task.CompletedTask; }
        public string ReadStatus(string logPath) => "OK - HomeHotspot is up on the target adapter, 5 GHz";
    }

    private sealed class Ctx
    {
        public MonitorConfig Config = new();
        public FakeProcessLauncher Launcher = new();
        public FakeSshRunner Ssh = new();
        public FakeKeeper Keeper = new();
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public List<string> Uris = new();

        public SessionOrchestrator Build()
        {
            Config.Polling.ProcessLaunchTimeoutMs = 50;
            Config.Polling.ProcessPollIntervalMs = 10;
            Config.SessionFlow.SlimeVrLaunchDelayMs = 0;
            HeadsetProfiles.SeedDefaults(Config);
            var frame = HeadsetProfiles.Find(Config, "frame")!;
            frame.HotspotKeeperTask = "Keeper";
            frame.HotspotKeeperLog = "keeper.log";
            return new SessionOrchestrator(Config, new SlimeVrTrackerMonitor(Config), new UpdateChecker(Config), new AdbController(Config),
                launcher: Launcher, clock: () => Now, streamWaiter: _ => Task.FromResult(true),
                preflightOverride: () => Task.CompletedTask,
                launchUri: u => { Uris.Add(u); if (u.EndsWith("/250820")) Launcher.Running.Add("vrserver"); },
                ssh: Ssh, hotspotKeeper: Keeper);
        }
    }

    [Fact]
    public async Task SteamLink_triggers_keeper_starts_steamvr_and_tools_but_not_vrchat()
    {
        var c = new Ctx();
        var o = c.Build();
        await o.RunSessionStartAsync("frame");
        Assert.Equal(SessionState.Complete, o.State);
        Assert.Equal(new[] { "Keeper" }, c.Keeper.Triggered);
        Assert.Contains("steam://rungameid/250820", c.Uris);
        Assert.DoesNotContain(c.Uris, u => u.EndsWith("/" + c.Config.Paths.VrChatSteamAppId));
        Assert.DoesNotContain("VirtualDesktop.Streamer", c.Launcher.EnsureRunningCalls);
        Assert.Contains("SlimeVR", c.Launcher.EnsureRunningCalls);
    }

    [Fact]
    public async Task SteamLink_skips_steamvr_launch_when_running()
    {
        var c = new Ctx();
        c.Launcher.Running.Add("vrserver");
        await c.Build().RunSessionStartAsync("frame");
        Assert.DoesNotContain("steam://rungameid/250820", c.Uris);
    }

    [Fact]
    public async Task Keeper_not_triggered_when_part_disabled()
    {
        var c = new Ctx();
        var o = c.Build();
        HeadsetProfiles.Find(c.Config, "frame")!.Parts[HeadsetPartNames.HotspotKeeper] = false;
        await o.RunSessionStartAsync("frame");
        Assert.Empty(c.Keeper.Triggered);
    }

    [Fact]
    public async Task Headset_commands_run_in_order_and_failures_do_not_abort()
    {
        var c = new Ctx();
        var o = c.Build();
        var f = HeadsetProfiles.Find(c.Config, "frame")!;
        f.HeadsetCommands.Add(new HeadsetCommand { Name = "a", Command = "cmd-a" });
        f.HeadsetCommands.Add(new HeadsetCommand { Name = "off", Command = "cmd-off", Enabled = false });
        f.HeadsetCommands.Add(new HeadsetCommand { Name = "b", Command = "cmd-b" });
        c.Ssh.Responses["cmd-a"] = new SshResult(1, "", "boom", false);
        await o.RunSessionStartAsync("frame");
        Assert.Equal(new[] { "cmd-a", "cmd-b" }, c.Ssh.Calls.Select(x => x.Command));
        Assert.Equal(SessionState.Complete, o.State);
    }

    [Fact]
    public async Task Brief_flap_while_steamvr_running_does_not_rerun()
    {
        var c = new Ctx();
        var o = c.Build();
        await o.RunSessionStartAsync("frame");
        o.OnHeadsetStateChanged(null, new HeadsetStateChangedEventArgs { IsOnline = false, HeadsetId = "" });
        c.Keeper.Triggered.Clear();
        c.Now = c.Now.AddSeconds(5);
        await o.RunSessionStartAsync("frame");
        Assert.Empty(c.Keeper.Triggered);
    }

    [Fact]
    public async Task Quest_profile_still_runs_virtual_desktop_flow()
    {
        var c = new Ctx();
        var o = c.Build();
        await o.RunSessionStartAsync("quest2");
        Assert.Contains("VirtualDesktop.Streamer", c.Launcher.EnsureRunningCalls);
        Assert.Empty(c.Keeper.Triggered);
    }
}
