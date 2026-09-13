using System;
using System.Collections.Generic;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Freezing should take only as much as the situation calls for. The whole opted-in list used to
/// go at once - one low reading froze 52 processes on 2026-09-08 - when the point is only to get
/// free memory back over the threshold. Biggest holder first means the fewest applications are
/// disturbed to recover a given amount.
/// </summary>
public class SessionSuspendIncrementalTests
{
    private sealed class FakeSuspender : IProcessSuspender
    {
        public readonly Dictionary<string, List<int>> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, long> WorkingSet = new();
        public readonly HashSet<int> Alive = new();
        public readonly List<int> Suspended = new();
        public readonly List<int> Resumed = new();

        public IReadOnlyList<int> GetProcessIds(string processName) =>
            ByName.TryGetValue(processName, out var pids) ? pids : new List<int>();

        public bool Suspend(int processId) { Suspended.Add(processId); return true; }
        public bool Resume(int processId) { Resumed.Add(processId); return true; }
        public bool TrimWorkingSet(int processId) => true;
        public bool IsRunning(int processId) => Alive.Contains(processId);
        public long GetWorkingSetBytes(int processId) => WorkingSet.TryGetValue(processId, out var b) ? b : 0;
    }

    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>Three apps holding 1, 4 and 2 GB. Free memory rises by whatever has been frozen so
    /// far, which is what freezing plus trimming really does.</summary>
    private static (SessionSuspendService svc, FakeSuspender fake) Build(double startFreeGb, double thresholdGb = 8.0)
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.Add(new ManagedApp { Id = "small", DisplayName = "small", ProcessName = "Small", SuspendDuringSession = true });
        config.ManagedApps.Add(new ManagedApp { Id = "huge", DisplayName = "huge", ProcessName = "Huge", SuspendDuringSession = true });
        config.ManagedApps.Add(new ManagedApp { Id = "medium", DisplayName = "medium", ProcessName = "Medium", SuspendDuringSession = true });
        config.MemoryPressure.FreeMemoryThresholdGb = thresholdGb;
        config.MemoryPressure.ConsecutiveChecksRequired = 1;
        config.MemoryPressure.HardFaultsPerSecondThreshold = 0;

        var fake = new FakeSuspender();
        fake.ByName["Small"] = new List<int> { 1 };
        fake.ByName["Huge"] = new List<int> { 2 };
        fake.ByName["Medium"] = new List<int> { 3 };
        fake.WorkingSet[1] = 1 * Gb;
        fake.WorkingSet[2] = 4 * Gb;
        fake.WorkingSet[3] = 2 * Gb;
        foreach (var pid in new[] { 1, 2, 3 }) fake.Alive.Add(pid);

        double Free() => startFreeGb + fake.Suspended.Sum(pid => fake.WorkingSet[pid]) / (double)Gb;

        var svc = new SessionSuspendService(config, fake, currentProcessId: 999,
            freeMemoryGb: Free, hardFaultsPerSecond: () => 0);
        svc.SkipGracePeriodForTests();
        return (svc, fake);
    }

    [Fact]
    public void Freezes_the_biggest_holder_first()
    {
        var (svc, fake) = Build(startFreeGb: 2);
        svc.SuspendUntilEnoughFree();
        Assert.Equal(2, fake.Suspended[0]); // the 4 GB one
    }

    [Fact]
    public void Stops_as_soon_as_free_memory_clears_the_threshold()
    {
        // 2 GB free, 8 GB wanted: the 4 GB app gets it to 6, the 2 GB app to 8. The 1 GB app
        // should never be touched.
        var (svc, fake) = Build(startFreeGb: 2);
        svc.SuspendUntilEnoughFree();

        Assert.Equal(new[] { 2, 3 }, fake.Suspended);
        Assert.False(svc.IsFrozen("small"));
        Assert.True(svc.IsFrozen("huge"));
        Assert.True(svc.IsFrozen("medium"));
    }

    [Fact]
    public void One_app_is_enough_when_it_covers_the_shortfall()
    {
        var (svc, fake) = Build(startFreeGb: 5);
        svc.SuspendUntilEnoughFree();

        Assert.Equal(new[] { 2 }, fake.Suspended);
        Assert.Equal(1, svc.FrozenCount);
    }

    [Fact]
    public void Freezes_everything_when_that_still_is_not_enough()
    {
        var (svc, fake) = Build(startFreeGb: 0, thresholdGb: 50);
        svc.SuspendUntilEnoughFree();

        Assert.Equal(3, fake.Suspended.Count);
    }

    [Fact]
    public void An_app_thawed_by_hand_is_not_a_candidate()
    {
        var (svc, fake) = Build(startFreeGb: 2);
        svc.ThawApp("huge");

        svc.SuspendUntilEnoughFree();

        Assert.DoesNotContain(2, fake.Suspended);
        Assert.False(svc.IsFrozen("huge"));
    }

    [Fact]
    public void Frozen_working_set_is_reported_for_the_pagefile_discount()
    {
        var (svc, _) = Build(startFreeGb: 5);
        svc.SuspendUntilEnoughFree();

        // Only the 4 GB app was needed.
        Assert.Equal(4.0, svc.FrozenWorkingSetGb, precision: 3);
    }

    [Fact]
    public void The_tally_resets_once_the_session_ends()
    {
        var (svc, _) = Build(startFreeGb: 2);
        svc.SuspendUntilEnoughFree();
        Assert.True(svc.FrozenWorkingSetGb > 0);

        svc.HandleSteamVrRunningChanged(false);

        Assert.Equal(0, svc.FrozenWorkingSetGb);
    }
}
