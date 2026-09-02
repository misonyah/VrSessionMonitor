using System.Collections.Generic;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// The service changes other processes' scheduling, so the restore path is what needs pinning:
/// leaving a background app throttled after a session, or forcing a remembered priority onto an
/// unrelated process that reused a PID, are both worse than doing nothing.
/// </summary>
public class SessionPriorityServiceTests
{
    private sealed class FakeController : IProcessPriorityController
    {
        public readonly Dictionary<string, List<int>> ByName = new();
        public readonly Dictionary<int, AppProcessPriority> Priorities = new();
        public readonly HashSet<int> DenySet = new();
        public readonly List<(int Pid, AppProcessPriority Priority)> Sets = new();

        public IReadOnlyList<int> GetProcessIds(string processName) =>
            ByName.TryGetValue(processName, out var pids) ? pids : new List<int>();

        public AppProcessPriority? GetPriority(int processId) =>
            Priorities.TryGetValue(processId, out var p) ? p : null;

        public bool SetPriority(int processId, AppProcessPriority priority)
        {
            if (DenySet.Contains(processId)) return false;
            if (!Priorities.ContainsKey(processId)) return false; // gone
            Priorities[processId] = priority;
            Sets.Add((processId, priority));
            return true;
        }
    }

    private static MonitorConfig ConfigWith(params ManagedApp[] apps)
    {
        var config = new MonitorConfig();
        config.ManagedApps.Clear();
        config.ManagedApps.AddRange(apps);
        return config;
    }

    private static ManagedApp App(string id, string processName, AppProcessPriority? priority) =>
        new() { Id = id, DisplayName = id, ProcessName = processName, SessionPriority = priority };

    [Fact]
    public void A_configured_app_is_lowered_when_the_session_starts()
    {
        var c = new FakeController();
        c.ByName["Unity"] = new List<int> { 100 };
        c.Priorities[100] = AppProcessPriority.Normal;
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Equal(AppProcessPriority.BelowNormal, c.Priorities[100]);
    }

    [Fact]
    public void It_is_put_back_when_the_session_ends()
    {
        var c = new FakeController();
        c.ByName["Unity"] = new List<int> { 100 };
        c.Priorities[100] = AppProcessPriority.Normal;
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(true);
        svc.HandleSteamVrRunningChanged(false);

        Assert.Equal(AppProcessPriority.Normal, c.Priorities[100]);
        Assert.Equal(0, svc.AdjustedCount);
    }

    [Fact]
    public void Restore_returns_the_process_to_what_it_actually_was_not_to_normal()
    {
        // Someone who had deliberately set a process to AboveNormal should get that back, not have
        // it quietly normalised by us.
        var c = new FakeController();
        c.ByName["Unity"] = new List<int> { 100 };
        c.Priorities[100] = AppProcessPriority.AboveNormal;
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.Idle)), c);

        svc.HandleSteamVrRunningChanged(true);
        svc.HandleSteamVrRunningChanged(false);

        Assert.Equal(AppProcessPriority.AboveNormal, c.Priorities[100]);
    }

    [Fact]
    public void Apps_with_no_session_priority_are_untouched()
    {
        var c = new FakeController();
        c.ByName["VRChat"] = new List<int> { 200 };
        c.Priorities[200] = AppProcessPriority.Normal;
        var svc = new SessionPriorityService(ConfigWith(App("vrchat", "VRChat", null)), c);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Empty(c.Sets);
    }

    [Fact]
    public void A_process_already_at_the_wanted_priority_is_not_recorded()
    {
        // Nothing was changed, so there is nothing to restore — recording it would mean "restoring"
        // a process we never touched.
        var c = new FakeController();
        c.ByName["Unity"] = new List<int> { 100 };
        c.Priorities[100] = AppProcessPriority.BelowNormal;
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Empty(c.Sets);
        Assert.Equal(0, svc.AdjustedCount);
    }

    [Fact]
    public void Every_instance_of_a_multi_process_app_is_adjusted()
    {
        // SlimeVR runs five processes under one name; adjusting an arbitrary one would be useless.
        var c = new FakeController();
        c.ByName["SlimeVR"] = new List<int> { 1, 2, 3 };
        foreach (var pid in c.ByName["SlimeVR"]) c.Priorities[pid] = AppProcessPriority.Normal;
        var svc = new SessionPriorityService(ConfigWith(App("slimevr", "SlimeVR", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Equal(3, svc.AdjustedCount);
        Assert.All(c.ByName["SlimeVR"], pid => Assert.Equal(AppProcessPriority.BelowNormal, c.Priorities[pid]));
    }

    [Fact]
    public void A_refused_change_is_not_recorded_for_restore()
    {
        // Access-denied on an elevated process is routine. Recording it would make us later "restore"
        // a process we never actually changed.
        var c = new FakeController();
        c.ByName["Unity"] = new List<int> { 100 };
        c.Priorities[100] = AppProcessPriority.Normal;
        c.DenySet.Add(100);
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Equal(0, svc.AdjustedCount);
    }

    [Fact]
    public void A_process_that_exits_mid_session_is_skipped_on_restore()
    {
        var c = new FakeController();
        c.ByName["Unity"] = new List<int> { 100 };
        c.Priorities[100] = AppProcessPriority.Normal;
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(true);
        c.Priorities.Remove(100); // Unity was closed during the session

        svc.HandleSteamVrRunningChanged(false); // must not throw
        Assert.Equal(0, svc.AdjustedCount);
    }

    [Fact]
    public void A_second_session_start_does_not_overwrite_the_remembered_original()
    {
        // Otherwise the second pass would record BelowNormal as the "original" and restore to that,
        // leaving the process throttled forever.
        var c = new FakeController();
        c.ByName["Unity"] = new List<int> { 100 };
        c.Priorities[100] = AppProcessPriority.Normal;
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(true);
        svc.ApplySessionPriorities();          // e.g. a SteamVR state flap
        svc.HandleSteamVrRunningChanged(false);

        Assert.Equal(AppProcessPriority.Normal, c.Priorities[100]);
    }

    [Fact]
    public void Restoring_without_a_session_having_started_does_nothing()
    {
        var c = new FakeController();
        var svc = new SessionPriorityService(ConfigWith(App("unity", "Unity", AppProcessPriority.BelowNormal)), c);

        svc.HandleSteamVrRunningChanged(false);

        Assert.Empty(c.Sets);
    }

    [Fact]
    public void An_app_with_no_process_name_is_ignored()
    {
        var c = new FakeController();
        var svc = new SessionPriorityService(ConfigWith(App("blank", "", AppProcessPriority.Idle)), c);

        svc.HandleSteamVrRunningChanged(true);

        Assert.Empty(c.Sets);
    }
}
