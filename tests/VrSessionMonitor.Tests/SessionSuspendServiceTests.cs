using System;
using System.Collections.Generic;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Freezing is pressure-triggered, and the tests that matter are the ones proving it does NOT fire.
/// Freezing an editor would make it unusable, so the bar for doing it has to be evidence that the
/// machine is actually struggling — not merely that a number looks small.
///
/// Two corrections are encoded here, both from real incidents. A single low reading froze 52
/// processes ten seconds into a session on 2026-09-08, while VRChat was still loading. And low
/// available memory alone is not evidence at all: Windows deliberately keeps almost nothing
/// genuinely free, filling the rest with reclaimable cache, so the fault rate is what separates
/// "using memory" from "thrashing" — 297/sec while VRChat stuttered, 64/sec once healthy.
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
        // Most tests are about the memory decision, so collapse the corroboration to a single
        // check; the sustained and fault-rate rules get their own tests below.
        config.MemoryPressure.ConsecutiveChecksRequired = 1;
        config.MemoryPressure.HardFaultsPerSecondThreshold = 0; // memory alone
        return config;
    }

    private static ManagedApp App(string id, string processName, bool suspend) =>
        new() { Id = id, DisplayName = id, ProcessName = processName, SuspendDuringSession = suspend };

    private static FakeSuspender FakeWithCode()
    {
        var suspender = new FakeSuspender();
        suspender.ByName["Code"] = new List<int> { 100 };
        suspender.Alive.Add(100);
        return suspender;
    }

    private static (SessionSuspendService, FakeSuspender) Build(MonitorConfig config, double freeGb, double faults = 0)
    {
        var suspender = FakeWithCode();
        var svc = new SessionSuspendService(config, suspender, currentProcessId: 999,
            freeMemoryGb: () => freeGb, hardFaultsPerSecond: () => faults);
        svc.SkipGracePeriodForTests();
        return (svc, suspender);
    }

    [Fact]
    public void Starting_a_session_does_not_freeze_anything_on_its_own()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 22);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Empty(fake.Suspended);
        Assert.Equal(0, svc.FrozenCount);
    }

    [Fact]
    public void Nothing_is_frozen_during_the_grace_period_however_low_memory_is()
    {
        // VRChat allocates heavily while loading a world and its avatars; that spike resolves
        // itself. Acting inside the window solves a problem that was about to disappear.
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        config.MemoryPressure.GracePeriodSeconds = 90;
        var fake = FakeWithCode();
        var svc = new SessionSuspendService(config, fake, 999, () => 1.0, () => 5000);

        svc.HandleSteamVrRunningChanged(true); // starts the grace clock now
        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Plenty_of_free_memory_leaves_apps_alone()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 22);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Sustained_pressure_freezes_the_opted_in_apps()
    {
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: 3.2);

        svc.CheckMemoryPressure();

        Assert.Contains(100, fake.Suspended);
        Assert.Contains(100, fake.Trimmed); // trimming is what releases the RAM immediately
    }

    [Fact]
    public void Pressure_must_be_sustained_across_consecutive_checks()
    {
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        config.MemoryPressure.ConsecutiveChecksRequired = 3;
        var (svc, fake) = Build(config, freeGb: 3.2);

        svc.CheckMemoryPressure();
        svc.CheckMemoryPressure();
        Assert.Empty(fake.Suspended); // not yet

        svc.CheckMemoryPressure();
        Assert.Contains(100, fake.Suspended);
    }

    [Fact]
    public void One_healthy_check_resets_the_streak()
    {
        // Otherwise brief dips accumulate across a whole session and eventually trip on noise.
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        config.MemoryPressure.ConsecutiveChecksRequired = 3;

        var fake = FakeWithCode();
        var free = 3.2;
        var svc = new SessionSuspendService(config, fake, 999, () => free, () => 0);
        svc.SkipGracePeriodForTests();

        svc.CheckMemoryPressure();
        svc.CheckMemoryPressure();
        free = 22;  // recovered
        svc.CheckMemoryPressure();
        free = 3.2; // dipped again
        svc.CheckMemoryPressure();
        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended); // streak restarted, so only two in a row
    }

    [Fact]
    public void Low_memory_without_paging_does_not_freeze()
    {
        // 64 faults/sec was measured on a machine that was completely healthy.
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        config.MemoryPressure.HardFaultsPerSecondThreshold = 200;
        var (svc, fake) = Build(config, freeGb: 3.2, faults: 64);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Low_memory_with_heavy_paging_freezes()
    {
        // 297 faults/sec was measured while VRChat was actually stuttering.
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        config.MemoryPressure.HardFaultsPerSecondThreshold = 200;
        var (svc, fake) = Build(config, freeGb: 3.2, faults: 297);

        svc.CheckMemoryPressure();

        Assert.Contains(100, fake.Suspended);
    }

    [Fact]
    public void Heavy_paging_with_plenty_of_memory_does_not_freeze()
    {
        // Page-ins also come from memory-mapped file reads, which are not memory pressure at all.
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        config.MemoryPressure.HardFaultsPerSecondThreshold = 200;
        var (svc, fake) = Build(config, freeGb: 30, faults: 100000);

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void An_unavailable_fault_rate_falls_back_to_the_memory_reading()
    {
        // The first sample of a session has no rate yet. Refusing to act until one exists would
        // silently disable the feature for the first interval.
        var config = ConfigWith(8.0, App("vscode", "Code", suspend: true));
        config.MemoryPressure.HardFaultsPerSecondThreshold = 200;
        var (svc, fake) = Build(config, freeGb: 3.2, faults: -1);

        svc.CheckMemoryPressure();

        Assert.Contains(100, fake.Suspended);
    }

    [Fact]
    public void An_unreadable_memory_reading_does_nothing()
    {
        // Treating "could not read" as zero would freeze the user's applications for no reason.
        var (svc, fake) = Build(ConfigWith(8.0, App("vscode", "Code", suspend: true)), freeGb: -1);

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
        // Memory stays low right after freezing, since eviction is not instant. Without this, the
        // next poll would re-suspend and re-record the same processes.
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
        // SuspendSafety is the backstop; a config naming VRChat must not be honoured.
        var config = ConfigWith(8.0, App("vrchat", "VRChat", suspend: true));
        var fake = new FakeSuspender();
        fake.ByName["VRChat"] = new List<int> { 200 };
        fake.Alive.Add(200);
        var svc = new SessionSuspendService(config, fake, 999, () => 1.0, () => 0);
        svc.SkipGracePeriodForTests();

        svc.CheckMemoryPressure();

        Assert.Empty(fake.Suspended);
    }
}
