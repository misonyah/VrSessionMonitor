using System;
using System.Collections.Generic;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// The Status tab's per-app freeze toggle, and making a config change take effect mid-session.
///
/// Both exist because a frozen process looks hung with no visible cause: you need to be able to
/// get an editor back for a minute without ending the session, and unticking "freeze this app"
/// used to do nothing until the session ended because the opted-in list was only read at the
/// moment of freezing. A hand thaw lasts the rest of the session by design - otherwise the next
/// pressure check, ten seconds later, simply freezes it again.
/// </summary>
public class SessionSuspendToggleTests
{
    private sealed class FakeSuspender : IProcessSuspender
    {
        public readonly Dictionary<string, List<int>> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> Alive = new();
        public readonly List<int> Suspended = new();
        public readonly List<int> Resumed = new();

        public IReadOnlyList<int> GetProcessIds(string processName) =>
            ByName.TryGetValue(processName, out var pids) ? pids : new List<int>();

        public bool Suspend(int processId) { Suspended.Add(processId); return true; }
        public bool Resume(int processId) { Resumed.Add(processId); return true; }
        public bool TrimWorkingSet(int processId) => true;
        public bool IsRunning(int processId) => Alive.Contains(processId);
    }

    private static ManagedApp App(string id, string processName, bool suspend = true) =>
        new() { Id = id, DisplayName = id, ProcessName = processName, SuspendDuringSession = suspend };

    private static (SessionSuspendService svc, FakeSuspender fake, MonitorConfig config) Build(double freeGb = 2.0)
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.Add(App("vscode", "Code"));
        config.ManagedApps.Add(App("unity", "Unity"));
        config.MemoryPressure.FreeMemoryThresholdGb = 8.0;
        config.MemoryPressure.ConsecutiveChecksRequired = 1;
        config.MemoryPressure.HardFaultsPerSecondThreshold = 0;

        var fake = new FakeSuspender();
        fake.ByName["Code"] = new List<int> { 100 };
        fake.ByName["Unity"] = new List<int> { 200 };
        fake.Alive.Add(100);
        fake.Alive.Add(200);

        var svc = new SessionSuspendService(config, fake, currentProcessId: 999,
            freeMemoryGb: () => freeGb, hardFaultsPerSecond: () => 0);
        svc.SkipGracePeriodForTests();
        return (svc, fake, config);
    }

    [Fact]
    public void Thawing_resumes_only_that_app()
    {
        var (svc, fake, _) = Build();
        svc.SuspendConfiguredApps();
        Assert.True(svc.IsFrozen("vscode"));
        Assert.True(svc.IsFrozen("unity"));

        svc.ThawApp("vscode");

        Assert.False(svc.IsFrozen("vscode"));
        Assert.True(svc.IsFrozen("unity"));
        Assert.Equal(new[] { 100 }, fake.Resumed);
    }

    [Fact]
    public void A_hand_thaw_survives_later_pressure_for_the_rest_of_the_session()
    {
        var (svc, fake, _) = Build();
        svc.SuspendConfiguredApps();
        svc.ThawApp("vscode");
        fake.Suspended.Clear();

        // Everything else thawed too, so the "already acted this session" guard cannot be what
        // keeps it running - the override has to.
        svc.ThawApp("unity");
        svc.CheckMemoryPressure();

        Assert.False(svc.IsFrozen("vscode"));
        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Refreezing_hands_it_back_to_pressure_and_freezes_now()
    {
        var (svc, fake, _) = Build();
        svc.SuspendConfiguredApps();
        svc.ThawApp("vscode");
        Assert.True(svc.IsThawedByUser("vscode"));

        svc.RefreezeApp("vscode");

        Assert.False(svc.IsThawedByUser("vscode"));
        Assert.True(svc.IsFrozen("vscode"));
        Assert.Contains(100, fake.Suspended);
    }

    [Fact]
    public void The_override_resets_when_the_session_ends()
    {
        var (svc, _, _) = Build();
        svc.SuspendConfiguredApps();
        svc.ThawApp("vscode");

        svc.HandleSteamVrRunningChanged(false); // session over

        Assert.False(svc.IsThawedByUser("vscode"));
        Assert.False(svc.IsFrozen("vscode"));
    }

    [Fact]
    public void The_override_resets_when_a_new_session_starts()
    {
        var (svc, _, _) = Build();
        svc.SuspendConfiguredApps();
        svc.ThawApp("vscode");

        svc.HandleSteamVrRunningChanged(false);
        svc.HandleSteamVrRunningChanged(true);

        Assert.False(svc.IsThawedByUser("vscode"));
    }

    [Fact]
    public void Unticking_freeze_thaws_that_app_immediately()
    {
        var (svc, fake, config) = Build();
        svc.SuspendConfiguredApps();
        Assert.True(svc.IsFrozen("unity"));

        config.ManagedApps.Find(a => a.DisplayName == "unity")!.SuspendDuringSession = false;
        svc.ReconcileWithConfig();

        Assert.False(svc.IsFrozen("unity"));
        Assert.Equal(new[] { 200 }, fake.Resumed);
        Assert.True(svc.IsFrozen("vscode")); // untouched
    }

    [Fact]
    public void Reconciling_does_not_leave_a_re_ticked_app_permanently_exempt()
    {
        // Unticking then re-ticking must hand the app back to pressure. If reconcile had recorded
        // a hand-thaw override, re-ticking would look enabled while never actually freezing.
        var (svc, _, config) = Build();
        svc.SuspendConfiguredApps();

        var unity = config.ManagedApps.Find(a => a.DisplayName == "unity")!;
        unity.SuspendDuringSession = false;
        svc.ReconcileWithConfig();
        unity.SuspendDuringSession = true;

        Assert.False(svc.IsThawedByUser("unity"));
        svc.SuspendConfiguredApps();
        Assert.True(svc.IsFrozen("unity"));
    }

    [Fact]
    public void Reconciling_with_nothing_frozen_is_a_no_op()
    {
        var (svc, fake, config) = Build();
        config.ManagedApps.Find(a => a.DisplayName == "unity")!.SuspendDuringSession = false;

        svc.ReconcileWithConfig();

        Assert.Empty(fake.Resumed);
    }
}
