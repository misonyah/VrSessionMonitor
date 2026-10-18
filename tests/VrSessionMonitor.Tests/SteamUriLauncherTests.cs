using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>Regression from 2026-10-17: SteamVR auto-started Steam, and the steam:// requests for OVR
/// Toolkit and OVR Advanced Settings were sent while Steam was still logging in, so Steam silently
/// dropped them.</summary>
public class SteamUriLauncherTests
{
    private sealed class Harness
    {
        public readonly List<string> Sent = new();
        public readonly List<TimeSpan> Waited = new();
        public bool LoggedIn;
        public int LoggedInAfterChecks = -1;    // becomes logged in after N checks (-1 = use LoggedIn)
        public Func<int, bool> RunningAfterSends = _ => true;
        private int _checks;

        public SteamUriLauncher Build() => new(
            steamLoggedIn: () => LoggedInAfterChecks >= 0 ? _checks++ >= LoggedInAfterChecks : LoggedIn,
            launchUri: Sent.Add,
            isRunning: _ => RunningAfterSends(Sent.Count),
            delay: d => { Waited.Add(d); return Task.CompletedTask; },
            readyTimeout: TimeSpan.FromSeconds(90),
            verifyAfter: TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Waits_for_steam_login_before_sending()
    {
        var h = new Harness { LoggedInAfterChecks = 3 };
        var ok = await h.Build().LaunchAsync("1068820", "OVR Toolkit", "OVR Toolkit");
        Assert.True(ok);
        Assert.Equal(new[] { "steam://rungameid/1068820" }, h.Sent);
        Assert.True(h.Waited.Count >= 3);      // polled while Steam was logging in
    }

    [Fact]
    public async Task Retries_once_when_the_app_did_not_appear()
    {
        var h = new Harness { LoggedIn = true, RunningAfterSends = sends => sends >= 2 };
        var ok = await h.Build().LaunchAsync("1009850", "AdvancedSettings", "OVR Advanced Settings");
        Assert.True(ok);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Gives_up_after_one_retry()
    {
        var h = new Harness { LoggedIn = true, RunningAfterSends = _ => false };
        Assert.False(await h.Build().LaunchAsync("1", "X", "X"));
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Steam_never_ready_sends_nothing_and_returns_within_the_timeout()
    {
        var h = new Harness { LoggedIn = false };
        Assert.False(await h.Build().LaunchAsync("1", "X", "X"));
        Assert.Empty(h.Sent);
        var total = TimeSpan.Zero;
        foreach (var w in h.Waited) total += w;
        Assert.True(total <= TimeSpan.FromSeconds(91));
    }

    [Fact]
    public async Task No_process_name_means_send_without_verifying()
    {
        var h = new Harness { LoggedIn = true, RunningAfterSends = _ => false };
        Assert.True(await h.Build().LaunchAsync("1", "", "X"));
        Assert.Single(h.Sent);
    }
}
