using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

public class SessionOrchestratorFlowTests
{
    private sealed class Ctx
    {
        public MonitorConfig Config = new();
        public FakeProcessLauncher Launcher = new();
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public bool StreamConnects = true;
        public List<string> UriLaunches = new();
        public List<SessionState> States = new();

        public SessionOrchestrator Build()
        {
            // Keep the flow fast and deterministic. DoLaunchVrChatAsync polls a REAL Task.Delay loop
            // (not the injected clock) for a VRChat process, and LaunchSlimeVrAsync does a real
            // pre-launch Task.Delay — with the shipped defaults (ProcessLaunchTimeoutMs=60000,
            // SlimeVrLaunchDelayMs=35000) the happy path would burn ~95s of wall-clock time and look
            // like a hang. Shrink them; the driver's phase ORDER is unchanged, which is what we assert.
            Config.Polling.ProcessLaunchTimeoutMs = 50;
            Config.Polling.ProcessPollIntervalMs = 10;
            Config.SessionFlow.SlimeVrLaunchDelayMs = 0;

            // Real collaborators, but preflight is stubbed out so no tracker/GitHub/ADB IO runs.
            var trackers = new SlimeVrTrackerMonitor(Config);
            var updates = new UpdateChecker(Config);
            var adb = new AdbController(Config);

            var orch = new SessionOrchestrator(
                Config, trackers, updates, adb,
                launcher: Launcher,
                clock: () => Now,
                streamWaiter: _ => Task.FromResult(StreamConnects),
                preflightOverride: () => Task.CompletedTask,
                // Mark VRChat "running" the instant its steam://rungameid URI is launched, so
                // DoLaunchVrChatAsync's confirmation poll exits on the first tick instead of timing
                // out. Only VRChat's app id is matched; OVR Toolkit's URI is just recorded.
                launchUri: uri =>
                {
                    UriLaunches.Add(uri);
                    if (uri.Contains(Config.Paths.VrChatSteamAppId)) Launcher.Running.Add("VRChat");
                });

            orch.StateChanged += (_, s) => States.Add(s);
            return orch;
        }
    }

    [Fact]
    public async Task Happy_path_launches_full_chain_and_completes()
    {
        var c = new Ctx();
        var orch = c.Build();
        await orch.RunSessionStartAsync();

        Assert.Equal(SessionState.Complete, orch.State);
        Assert.Contains("VirtualDesktop.Streamer", c.Launcher.EnsureRunningCalls);
        Assert.Contains("steam", c.Launcher.EnsureRunningCalls);
        Assert.Contains("SlimeVR", c.Launcher.EnsureRunningCalls);
        // VRChat + OVR Toolkit go through the uri launcher (steam://rungameid).
        Assert.Contains(c.UriLaunches, u => u.Contains("rungameid"));
    }

    [Fact]
    public async Task Stream_timeout_only_launches_vd_then_completes()
    {
        var c = new Ctx { StreamConnects = false };
        var orch = c.Build();
        await orch.RunSessionStartAsync();

        Assert.Equal(SessionState.Complete, orch.State);
        Assert.Contains("VirtualDesktop.Streamer", c.Launcher.EnsureRunningCalls);
        Assert.DoesNotContain("steam", c.Launcher.EnsureRunningCalls);
        Assert.DoesNotContain("SlimeVR", c.Launcher.EnsureRunningCalls);
    }

    [Fact]
    public async Task Brief_ping_flap_skips_relaunch()
    {
        var c = new Ctx();
        c.Launcher.ProcessIds["VirtualDesktop.Streamer"] = 4242; // stable PID across runs
        var orch = c.Build();

        // First genuine session completes and records the VD PID.
        await orch.RunSessionStartAsync();
        var callsAfterFirst = c.Launcher.EnsureRunningCalls.Count;

        // Headset drops for 8s (below the 90s threshold) then returns — same VD PID.
        orch.OnHeadsetStateChanged(this, new HeadsetStateChangedEventArgs { IsOnline = false });
        c.Now = c.Now.AddSeconds(8);
        await orch.RunSessionStartAsync();

        // Only VD Streamer's ensure ran again (via LaunchVdStreamerAsync); no Steam/SlimeVR relaunch.
        Assert.Equal(SessionState.Complete, orch.State);
        var newCalls = c.Launcher.EnsureRunningCalls.GetRange(callsAfterFirst, c.Launcher.EnsureRunningCalls.Count - callsAfterFirst);
        Assert.Contains("VirtualDesktop.Streamer", newCalls);
        Assert.DoesNotContain("steam", newCalls);
        Assert.DoesNotContain("SlimeVR", newCalls);
    }

    [Fact]
    public async Task Long_offline_same_pid_is_treated_as_genuine_new_session()
    {
        var c = new Ctx();
        c.Launcher.ProcessIds["VirtualDesktop.Streamer"] = 4242;
        var orch = c.Build();

        await orch.RunSessionStartAsync();
        var callsAfterFirst = c.Launcher.EnsureRunningCalls.Count;

        // Offline for 5 minutes (>= 90s threshold) even though the VD PID is unchanged.
        orch.OnHeadsetStateChanged(this, new HeadsetStateChangedEventArgs { IsOnline = false });
        c.Now = c.Now.AddMinutes(5);
        await orch.RunSessionStartAsync();

        var newCalls = c.Launcher.EnsureRunningCalls.GetRange(callsAfterFirst, c.Launcher.EnsureRunningCalls.Count - callsAfterFirst);
        Assert.Contains("steam", newCalls);        // full chain re-ran
        Assert.Contains("SlimeVR", newCalls);
    }

    [Fact]
    public async Task Exception_mid_flow_lands_in_failed()
    {
        var c = new Ctx();
        // Make the stream waiter throw to force the catch path.
        var trackers = new SlimeVrTrackerMonitor(c.Config);
        var updates = new UpdateChecker(c.Config);
        var adb = new AdbController(c.Config);
        var orch = new SessionOrchestrator(
            c.Config, trackers, updates, adb,
            launcher: c.Launcher,
            clock: () => c.Now,
            streamWaiter: _ => throw new InvalidOperationException("boom"),
            preflightOverride: () => Task.CompletedTask,
            launchUri: uri => c.UriLaunches.Add(uri));

        await orch.RunSessionStartAsync();

        Assert.Equal(SessionState.Failed, orch.State);
    }
}
