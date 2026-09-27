using System;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Frame;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

public class FrameLinkMonitorTests
{
    private static FrameLinkStatus Link(bool internet = false, string band = "6 GHz", int width = 160) =>
        new(true, band, 37, width, 2161, 1441, -50, 0, 0, internet, internet ? "HomeHotspot" : "", internet ? "connected" : "disconnected");

    [Fact]
    public void Internet_searching_warns_once_after_60s_and_rearms()
    {
        var w = new FrameLinkWarnings();
        var t0 = new DateTime(2026, 1, 1);
        Assert.Empty(w.Evaluate(Link(), "OK - up", t0));
        Assert.Empty(w.Evaluate(Link(), "OK - up", t0.AddSeconds(30)));
        Assert.Single(w.Evaluate(Link(), "OK - up", t0.AddSeconds(61)));
        Assert.Empty(w.Evaluate(Link(), "OK - up", t0.AddSeconds(120)));     // once
        Assert.Empty(w.Evaluate(Link(internet: true), "OK - up", t0.AddSeconds(130)));
        Assert.Empty(w.Evaluate(Link(), "OK - up", t0.AddSeconds(140)));
        Assert.Single(w.Evaluate(Link(), "OK - up", t0.AddSeconds(201)));    // re-armed
    }

    [Fact]
    public void Undelivered_warning_is_retried_on_the_next_poll()
    {
        // SteamVR not up yet -> the notification can't be shown; it must not be "used up".
        var w = new FrameLinkWarnings();
        var t = new DateTime(2026, 1, 1);
        Assert.Empty(w.Evaluate(Link(internet: true), "hotspot down", t, deliver: _ => false));
        Assert.Single(w.Evaluate(Link(internet: true), "hotspot down", t.AddSeconds(30), deliver: _ => true));
    }

    [Fact]
    public async Task Warnings_rearm_when_the_frame_goes_inactive()
    {
        var config = new MonitorConfig();
        HeadsetProfiles.SeedDefaults(config);
        HeadsetProfiles.Find(config, "frame")!.HotspotKeeperLog = "keeper.log";
        var headset = new FakeHeadsetMonitor { ActiveHeadsetId = "frame" };
        headset.SetOnline(true);
        var sent = new System.Collections.Generic.List<string>();
        var mon = new FrameLinkMonitor(config, headset, new FakeSshRunner(), new DownKeeper(), m => { sent.Add(m); return true; });
        await mon.PollOnceAsync();                   // keeper warning #1
        headset.ActiveHeadsetId = "quest2";
        await mon.PollOnceAsync();                   // Frame inactive
        headset.ActiveHeadsetId = "frame";
        await mon.PollOnceAsync();                   // new Frame session -> warn again
        Assert.Equal(2, sent.FindAll(m => m.Contains("hotspot down")).Count);
    }

    [Fact]
    public async Task Failed_notification_while_steamvr_runs_is_not_retried_every_poll()
    {
        // The real notifier can fail with SteamVR up (OpenVR error 102); retrying each poll made
        // VrSessionMonitor connect/disconnect to SteamVR every ~30 s (live 2026-09-27).
        var config = new MonitorConfig();
        HeadsetProfiles.SeedDefaults(config);
        HeadsetProfiles.Find(config, "frame")!.HotspotKeeperLog = "keeper.log";
        var headset = new FakeHeadsetMonitor { ActiveHeadsetId = "frame" };
        headset.SetOnline(true);
        var attempts = 0;
        var mon = new FrameLinkMonitor(config, headset, new FakeSshRunner(), new DownKeeper(),
            _ => { attempts++; return false; }, steamVrRunning: () => true);
        await mon.PollOnceAsync();
        await mon.PollOnceAsync();
        await mon.PollOnceAsync();
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Warning_waits_for_steamvr_without_attempting()
    {
        var config = new MonitorConfig();
        HeadsetProfiles.SeedDefaults(config);
        HeadsetProfiles.Find(config, "frame")!.HotspotKeeperLog = "keeper.log";
        var headset = new FakeHeadsetMonitor { ActiveHeadsetId = "frame" };
        headset.SetOnline(true);
        var running = false;
        var attempts = 0;
        var mon = new FrameLinkMonitor(config, headset, new FakeSshRunner(), new DownKeeper(),
            _ => { attempts++; return true; }, steamVrRunning: () => running);
        await mon.PollOnceAsync();
        Assert.Equal(0, attempts);
        running = true;
        await mon.PollOnceAsync();
        Assert.Equal(1, attempts);
    }

    private sealed class DownKeeper : IHotspotKeeper
    {
        public Task TriggerAndWaitAsync(string taskName, TimeSpan timeout) => Task.CompletedTask;
        public string ReadStatus(string logPath) => "hotspot down";
    }

    [Fact]
    public void Keeper_not_ok_and_degraded_link_warn_once_each()
    {
        var w = new FrameLinkWarnings();
        var t = new DateTime(2026, 1, 1);
        var msgs = w.Evaluate(Link(internet: true, band: "5 GHz", width: 80), "adapter still lacks Wi-Fi Direct support", t);
        Assert.Equal(2, msgs.Count);
        Assert.Empty(w.Evaluate(Link(internet: true, band: "5 GHz", width: 80), "adapter still lacks Wi-Fi Direct support", t.AddSeconds(30)));
    }

    [Fact]
    public async Task Poll_skips_while_previous_in_flight()
    {
        var config = new MonitorConfig();
        HeadsetProfiles.SeedDefaults(config);
        var headset = new FakeHeadsetMonitor { ActiveHeadsetId = "frame" };
        headset.SetOnline(true);
        var gate = new TaskCompletionSource();
        var ssh = new FakeSshRunner { Gate = () => gate.Task };
        var mon = new FrameLinkMonitor(config, headset, ssh, new NullKeeper(), _ => true);
        var first = mon.PollOnceAsync();
        await mon.PollOnceAsync();                  // must return immediately, not queue a 2nd ssh
        Assert.Single(ssh.Calls);
        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task Ssh_failure_shows_unavailable_and_does_not_throw()
    {
        var config = new MonitorConfig();
        HeadsetProfiles.SeedDefaults(config);
        var headset = new FakeHeadsetMonitor { ActiveHeadsetId = "frame" };
        headset.SetOnline(true);
        var ssh = new FakeSshRunner();
        ssh.Responses["iw dev"] = new SshResult(255, "", "Permission denied (publickey)", false);
        var mon = new FrameLinkMonitor(config, headset, ssh, new NullKeeper(), _ => true);
        await mon.PollOnceAsync();
        Assert.StartsWith("SSH unavailable: Permission denied", mon.StatusText);
    }

    [Fact]
    public async Task Not_polling_when_quest_is_active()
    {
        var config = new MonitorConfig();
        HeadsetProfiles.SeedDefaults(config);
        var headset = new FakeHeadsetMonitor { ActiveHeadsetId = "quest2" };
        headset.SetOnline(true);
        var ssh = new FakeSshRunner();
        var mon = new FrameLinkMonitor(config, headset, ssh, new NullKeeper(), _ => true);
        await mon.PollOnceAsync();
        Assert.Empty(ssh.Calls);
        Assert.Equal("", mon.StatusText);
    }

    private sealed class NullKeeper : IHotspotKeeper
    {
        public Task TriggerAndWaitAsync(string taskName, TimeSpan timeout) => Task.CompletedTask;
        public string ReadStatus(string logPath) => "OK - up";
    }
}
