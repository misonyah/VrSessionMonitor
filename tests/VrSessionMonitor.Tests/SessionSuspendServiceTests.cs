using System;
using System.Collections.Generic;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Freezing is pressure-triggered, and the tests that matter are the ones proving it does NOT fire.
/// Freezing an editor on every session start would make it unusable to solve a problem that was
/// not happening — the applications have to keep working until the machine actually needs the RAM.
/// </summary>
public class SessionSuspendServiceTests
{
    private sealed class FakeSuspender : IProcessSuspender
    {
        public readonly Dictionary<string, List<int>> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> Alive = new();
        public readonly List<int> Suspended = new();
        public readonly List<int> Resumed = new();
        public readonly List<int> Trimmed = new();

        public IReadOnlyList<int> GetProcessIds(string processName) =>
            ByName.TryGetValue(processName, out var pids) ? pids : new List<int>();

        public bool Suspend(int processId) { Suspended.Add(processId); return true; }
        public bool Resume(int processId) { Resumed.Add(processId); return true; }
        public bool TrimWorkingSet(int processId) { Trimmed.Add(processId); return true; }
        public bool IsRunning(int processId) => Alive.Contains(processId);
    }

    private static MonitorConfig ConfigWith(double thresholdGb, params ManagedApp[] apps)
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.AddRange(apps);
        config.MemoryPressure.FreeMemoryThresholdGb = thresholdGb;
        return config;
    }

    private static ManagedApp App(string id, string processName, bool suspend) =>
        new() { Id = id, DisplayName = id, ProcessName = processName, SuspendDuringSession = suspend };

    private static (SessionSuspendService, FakeSuspender) Build(MonitorConfig config, double freeGb)
    {
        var suspender = new FakeSuspender();
        suspender.ByName["Code"] = new List<int> { 100 };
        suspender.Alive.Add(100);
        return (new SessionSuspendService(config, suspender, currentProcessId: 999, freeMemoryGb: () => freeGb), suspender);
    }

    [Fact]
    public void Starting_a_session_does_not_freeze_anything_on_its_own()
    {
        // The whole point of the change: apps keep working until memory is actually short.
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 22);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Empty(fake.Suspended);
        Assert.Equal(0, svc.FrozenCount);
    }

    [Fact]
    public void Plenty_of_free_memory_leaves_apps_alone()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 22);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Falling_below_the_threshold_freezes_the_opted_in_apps()
    {
        // The state measured while VRChat was stuttering: 3.2 GB free.
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 3.2);

        svc.CheckMemoryPressure();

        Assert.Contains(100, fake.Suspended);
        Assert.Contains(100, fake.Trimmed); // trimming is what releases the RAM immediately
    }

    [Fact]
    public void The_threshold_boundary_does_not_freeze()
    {
        // Exactly at the threshold is not below it; an off-by-one here freezes a machine that was
        // fine.
        var (_, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 8.0);
        var svc = new SessionSuspendService(
            ConfigWith(8.0, App("vscode", "Code", suspend: true)), fake, 999, () => 8.0);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void An_unreadable_memory_reading_does_nothing()
    {
        // Treating "could not read" as zero would freeze the user's applications for no reason.
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        var fake = new FakeSuspender();
        fake.ByName["Code"] = new List<int> { 100 };
        var svc = new SessionSuspendService(config, fake, 999, () => -1);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Apps_not_opted_in_are_never_frozen()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: false)), freeGb: 1.0);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Pressure_only_acts_once_per_session()
    {
        // Memory stays low right after freezing — the pages take time to be evicted — so a second
        // pass must not re-suspend and, worse, re-record the same processes.
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 3.2);

        svc.CheckMemoryPressure();
        svc.CheckMemoryPressure();
        svc.CheckMemoryPressure();

        Assert.Single(fake.Suspended);
    }

    [Fact]
    public void Ending_the_session_resumes_everything_that_was_frozen()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 3.2);
        svc.CheckMemoryPressure();

        svc.HandleSteamVrRunningChanged(false);

        Assert.Contains(100, fake.Resumed);
        Assert.Equal(0, svc.FrozenCount);
    }

    [Fact]
    public void Ending_a_session_that_never_froze_anything_resumes_nothing()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 22);
        svc.HandleSteamVrRunningChanged(true);

        svc.HandleSteamVrRunningChanged(false);

        Assert.Empty(fake.Resumed);
    }

    [Fact]
    public void A_process_that_exited_while_frozen_is_skipped_on_resume()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 3.2);
        svc.CheckMemoryPressure();
        fake.Alive.Remove(100); // the user closed it while it was frozen

        svc.HandleSteamVrRunningChanged(false); // must not throw

        Assert.Empty(fake.Resumed);
    }

    [Fact]
    public void The_vr_chain_is_refused_even_when_opted_in()
    {
        // SuspendSafety is the backstop; a config that names VRChat must not be honoured.
        var config = ConfigWith(8.0, App("vrchat", "VRChat", suspend: true));
        var fake = new FakeSuspender();
        fake.ByName["VRChat"] = new List<int> { 200 };
        fake.Alive.Add(200);
        var svc = new SessionSuspendService(config, fake, 999, () => 1.0);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }
}
