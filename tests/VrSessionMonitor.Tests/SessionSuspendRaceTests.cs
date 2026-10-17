using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>Regressions from 2026-10-17: SteamVR stopped while a freeze pass was still running, so
/// the session-end resume ran before anything was frozen and 44 processes stayed frozen; and an
/// overlapping second pass suspended one process twice.</summary>
public class SessionSuspendRaceTests
{
    private sealed class HookSuspender : IProcessSuspender
    {
        public readonly ConcurrentDictionary<int, int> SuspendCount = new();
        public readonly ConcurrentBag<int> Resumed = new();
        public Action<int>? OnSuspend;
        public IReadOnlyList<int> GetProcessIds(string processName) => processName == "Chrome" ? new[] { 1, 2, 3 } : Array.Empty<int>();
        public bool Suspend(int processId) { OnSuspend?.Invoke(processId); SuspendCount.AddOrUpdate(processId, 1, (_, c) => c + 1); return true; }
        public bool Resume(int processId) { Resumed.Add(processId); SuspendCount.TryRemove(processId, out _); return true; }
        public bool TrimWorkingSet(int processId) => true;
        public bool IsRunning(int processId) => true;
        public long GetWorkingSetBytes(int processId) => 1L << 30;
    }

    private static MonitorConfig Config()
    {
        var c = new MonitorConfig();
        c.ManagedApps.Clear();
        c.ManagedApps.Add(new ManagedApp { Id = "chrome", DisplayName = "Chrome", ProcessName = "Chrome", SuspendDuringSession = true });
        c.MemoryPressure.FreeMemoryThresholdGb = 8;
        return c;
    }

    [Fact]
    public void SteamVr_stopping_mid_freeze_leaves_nothing_frozen()
    {
        var s = new HookSuspender();
        var svc = new SessionSuspendService(Config(), s, currentProcessId: 999, freeMemoryGb: () => 1);
        svc.HandleSteamVrRunningChanged(true);
        // SteamVR stops right after the first process is suspended — the session-end resume runs
        // while the freeze pass is still going.
        s.OnSuspend = pid => { if (pid == 1) svc.HandleSteamVrRunningChanged(false); };

        svc.SuspendUntilEnoughFree();

        Assert.Empty(s.SuspendCount);            // everything that got suspended was resumed
        Assert.Equal(0, svc.FrozenCount);
    }

    [Fact]
    public void Overlapping_freeze_passes_suspend_each_process_once()
    {
        var s = new HookSuspender();
        var svc = new SessionSuspendService(Config(), s, currentProcessId: 999, freeMemoryGb: () => 1);
        svc.HandleSteamVrRunningChanged(true);
        using var bothInside = new Barrier(2);
        s.OnSuspend = _ => bothInside.SignalAndWait(TimeSpan.FromMilliseconds(200));

        Task.WaitAll(Task.Run(svc.SuspendUntilEnoughFree), Task.Run(svc.SuspendUntilEnoughFree));

        Assert.All(s.SuspendCount.Values, count => Assert.Equal(1, count));
        Assert.Equal(3, svc.FrozenCount);
    }
}
