using System;
using System.Collections.Generic;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// The Status tab's per-process freeze table: one row per running process that matches a freeze
/// rule, each with its own Freeze/Thaw button. An app such as VS Code runs dozens of processes and
/// Unity one per open project, so thawing the whole app to get one project back frees far more
/// than needed.
/// </summary>
public class SessionSuspendProcessTests
{
    private sealed class FakeSuspender : IProcessSuspender
    {
        public readonly Dictionary<string, List<int>> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> Alive = new();
        public readonly List<int> Suspended = new();
        public readonly List<int> Resumed = new();
        public readonly Dictionary<int, long> WorkingSet = new();
        public readonly Dictionary<int, string> Titles = new();

        /// <summary>null (the default) means "can't tell", so the table shows every process; a set
        /// restricts the table to those PIDs, as the real EnumWindows pass would.</summary>
        public HashSet<int>? WindowOwners;

        public IReadOnlyList<int> GetProcessIds(string processName) =>
            ByName.TryGetValue(processName, out var pids) ? pids : new List<int>();

        public IReadOnlySet<int>? GetWindowOwningProcessIds() => WindowOwners;

        public bool Suspend(int processId) { Suspended.Add(processId); return true; }
        public bool Resume(int processId) { Resumed.Add(processId); return true; }
        public bool TrimWorkingSet(int processId) { WorkingSet[processId] = 0; return true; }
        public bool IsRunning(int processId) => Alive.Contains(processId);
        public long GetWorkingSetBytes(int processId) => WorkingSet.TryGetValue(processId, out var b) ? b : 0;
        public string GetMainWindowTitle(int processId) => Titles.TryGetValue(processId, out var t) ? t : "";
    }

    private const long Mb = 1024L * 1024;

    private static (SessionSuspendService svc, FakeSuspender fake) Build()
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.Add(new ManagedApp { Id = "unity", DisplayName = "Unity", ProcessName = "Unity", SuspendDuringSession = true });
        config.ManagedApps.Add(new ManagedApp { Id = "blender", DisplayName = "Blender", ProcessName = "blender", SuspendDuringSession = true });
        config.ManagedApps.Add(new ManagedApp { Id = "notes", DisplayName = "Notes", ProcessName = "notes", SuspendDuringSession = false });
        config.MemoryPressure.FreeMemoryThresholdGb = 8.0;
        config.MemoryPressure.ConsecutiveChecksRequired = 1;
        config.MemoryPressure.HardFaultsPerSecondThreshold = 0;

        var fake = new FakeSuspender();
        fake.ByName["Unity"] = new List<int> { 100, 101 };
        fake.ByName["blender"] = new List<int> { 200 };
        fake.ByName["notes"] = new List<int> { 300 };
        foreach (var pid in new[] { 100, 101, 200, 300 }) fake.Alive.Add(pid);
        fake.WorkingSet[100] = 900 * Mb;
        fake.WorkingSet[101] = 400 * Mb;
        fake.WorkingSet[200] = 2000 * Mb;
        fake.WorkingSet[300] = 50 * Mb;

        var svc = new SessionSuspendService(config, fake, currentProcessId: 999,
            freeMemoryGb: () => 2.0, hardFaultsPerSecond: () => 0);
        svc.SkipGracePeriodForTests();
        return (svc, fake);
    }

    [Fact]
    public void Thawing_one_process_resumes_only_that_process()
    {
        var (svc, fake) = Build();
        svc.SuspendConfiguredApps();

        svc.ThawProcess(100);

        Assert.Equal(new[] { 100 }, fake.Resumed);
        Assert.True(svc.IsFrozen("Unity"));   // its sibling is still frozen
    }

    [Fact]
    public void A_thawed_process_survives_later_pressure_while_its_siblings_can_still_be_frozen()
    {
        var (svc, fake) = Build();
        svc.ThawProcess(100);                  // pre-emptively, before anything was frozen

        svc.CheckMemoryPressure();

        Assert.DoesNotContain(100, fake.Suspended);
        Assert.Contains(101, fake.Suspended);
    }

    [Fact]
    public void Freezing_one_process_freezes_only_that_process_and_drops_its_thaw()
    {
        var (svc, fake) = Build();
        svc.ThawProcess(100);

        svc.FreezeProcess(100);

        Assert.Equal(new[] { 100 }, fake.Suspended);
        var row = svc.GetProcessRows().Single(r => r.ProcessId == 100);
        Assert.Equal(FreezeState.Frozen, row.State);
    }

    [Fact]
    public void Freezing_a_process_that_matches_no_freeze_rule_is_refused()
    {
        var (svc, fake) = Build();

        svc.FreezeProcess(300);   // "notes" is not opted in
        svc.FreezeProcess(12345); // not a candidate at all

        Assert.Empty(fake.Suspended);
    }

    [Fact]
    public void Freeze_all_clears_the_per_process_thaws_of_that_app()
    {
        var (svc, fake) = Build();
        svc.ThawProcess(100);

        svc.RefreezeApp("Unity");

        Assert.Contains(100, fake.Suspended);
        Assert.Contains(101, fake.Suspended);
    }

    [Fact]
    public void Per_process_thaws_reset_when_the_session_ends()
    {
        var (svc, fake) = Build();
        svc.ThawProcess(100);

        svc.ResumeAll();
        svc.SuspendConfiguredApps();

        Assert.Contains(100, fake.Suspended);
    }

    [Fact]
    public void Rows_list_only_opted_in_processes_grouped_by_app_biggest_first_with_state()
    {
        var (svc, fake) = Build();
        fake.Titles[100] = "ProjA - Main - Unity 2022.3.22f1";
        svc.ThawProcess(101);

        var rows = svc.GetProcessRows();

        Assert.Equal(new[] { 200, 100, 101 }, rows.Select(r => r.ProcessId));   // Blender (2 GB) before Unity (1.3 GB)
        Assert.DoesNotContain(rows, r => r.ProcessId == 300);
        Assert.Equal("ProjA - Main - Unity 2022.3.22f1", rows.Single(r => r.ProcessId == 100).WindowTitle);
        Assert.Equal(FreezeState.Running, rows.Single(r => r.ProcessId == 100).State);
        Assert.Equal(FreezeState.KeptRunning, rows.Single(r => r.ProcessId == 101).State);
        Assert.Equal("Unity", rows.Single(r => r.ProcessId == 101).AppName);
    }

    [Fact]
    public void Rows_show_only_processes_that_own_a_window_when_the_suspender_can_tell()
    {
        var (svc, fake) = Build();
        fake.WindowOwners = new HashSet<int> { 100, 200 }; // 101 is a window-less Unity helper

        var rows = svc.GetProcessRows();

        Assert.Equal(new[] { 200, 100 }, rows.Select(r => r.ProcessId)); // 101 left out
        Assert.DoesNotContain(rows, r => r.ProcessId == 101);
    }

    [Fact]
    public void A_frozen_row_shows_the_memory_it_held_when_frozen_not_its_trimmed_working_set()
    {
        var (svc, fake) = Build();

        svc.FreezeProcess(100);

        Assert.Equal(0, fake.WorkingSet[100]);   // trimmed
        var row = svc.GetProcessRows().Single(r => r.ProcessId == 100);
        Assert.Equal(900 * Mb, row.MemoryBytes);
    }

    [Fact]
    public void An_app_thawed_as_a_whole_shows_its_processes_as_kept_running()
    {
        var (svc, _) = Build();
        svc.ThawApp("Unity");

        Assert.All(svc.GetProcessRows().Where(r => r.AppName == "Unity"),
            r => Assert.Equal(FreezeState.KeptRunning, r.State));
    }
}
