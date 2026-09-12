using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Suspend;

/// <summary>
/// Freezes configured applications for the length of a VR session and thaws them afterwards.
///
/// PRESSURE-TRIGGERED, not session-triggered. A session only arms the watch; nothing is frozen
/// until free RAM actually falls below the configured threshold, so a session with memory to spare
/// never disturbs anything. Freezing on every session start would make an editor unusable to solve
/// a problem that was not occurring.
///
/// The point is memory, not CPU. A suspended process stops touching its pages, so Windows evicts
/// them and hands the physical RAM to the session — and it never faults them back, because it is
/// not running. TrimWorkingSet makes that immediate rather than gradual. Measured on this machine
/// on 2026-09-02: a development environment (67 VS Code processes, Unity, 256 node) had pushed RAM
/// to 95% used with 29.5 GB in the pagefile, and VRChat stuttered from paging. Closing those apps
/// recovered 7.6 GB; freezing them recovers the same memory while leaving every editor and its
/// unsaved state exactly where it was.
///
/// Everything about the resume path is defensive, because a process left frozen looks hung with no
/// visible cause and no way for the user to guess why:
///
/// - only processes we actually suspended are ever resumed, keyed on PID
/// - resume runs on app shutdown too, so quitting mid-session cannot strand one
/// - SuspendSafety refuses the VR chain, system processes and this app itself
///
/// This helps only when RAM is the constraint. It does nothing for a session limited by avatar
/// load on VRChat's main thread, which is a different bottleneck with a different fix.
/// </summary>
public sealed class SessionSuspendService : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly IProcessSuspender _suspender;
    private readonly int _currentProcessId;

    /// <summary>PIDs we suspended, with the app they belong to, so resume touches nothing else.</summary>
    private readonly Dictionary<int, string> _suspended = new();

    /// <summary>Apps the user has thawed by hand this session, which pressure must not re-freeze.
    /// Session-scoped on purpose: cleared when a session starts and when one ends, so a decision
    /// made to get at an editor mid-session never silently persists into the next session.</summary>
    private readonly HashSet<string> _userThawed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private System.Threading.Timer? _watchTimer;
    private readonly Func<double> _freeMemoryGb;
    private readonly Func<double> _hardFaultsPerSecond;
    private DateTime? _sessionStartedAt;
    private int _consecutivePressureChecks;

    /// <param name="freeMemoryGb">Available physical RAM in GB, or negative when unreadable.</param>
    /// <param name="hardFaultsPerSecond">Pages read from disk per second, or negative when
    /// unavailable. Both are injectable so the pressure logic is testable without consuming real
    /// memory or waiting for real paging.</param>
    public SessionSuspendService(MonitorConfig config, IProcessSuspender suspender,
        int? currentProcessId = null, Func<double>? freeMemoryGb = null,
        Func<double>? hardFaultsPerSecond = null)
    {
        _config = config;
        _suspender = suspender;
        _currentProcessId = currentProcessId ?? SuspendSafety.CurrentProcessId;
        _freeMemoryGb = freeMemoryGb ?? SystemMemory.FreePhysicalGb;
        _hardFaultsPerSecond = hardFaultsPerSecond ?? SystemMemory.HardFaultsPerSecond;
    }

    /// <summary>Test seam: pretend the session started long enough ago that the grace period has
    /// passed, so a test does not have to wait 90 seconds.</summary>
    internal void SkipGracePeriodForTests()
    {
        lock (_lock) _sessionStartedAt = DateTime.UtcNow.AddYears(-1);
    }

    /// <summary>Plain method rather than an event subscription, matching OptimizationsManager and
    /// SessionPriorityService, so it is directly callable from tests.</summary>
    public void HandleSteamVrRunningChanged(bool isRunning)
    {
        if (isRunning) StartWatching();
        else
        {
            StopWatching();
            ResumeAll();
        }
    }

    /// <summary>
    /// Arms the memory watch for the session. Deliberately does NOT freeze anything: the whole
    /// point of being pressure-triggered is that the applications keep working until the machine
    /// actually needs the RAM, so a session with plenty of memory never disturbs them at all.
    /// </summary>
    private void StartWatching()
    {
        if (_config.ManagedApps.All(a => !a.SuspendDuringSession)) return;

        var interval = Math.Max(1, _config.MemoryPressure.CheckIntervalSeconds) * 1000;
        lock (_lock)
        {
            _watchTimer?.Dispose();
            _watchTimer = new System.Threading.Timer(_ => SafeCheckMemoryPressure(), null, interval, interval);
            _sessionStartedAt = DateTime.UtcNow;
            _consecutivePressureChecks = 0;
            _userThawed.Clear();
        }

        // A rate computed against a sample from hours ago is meaningless.
        SystemMemory.ResetHardFaultSampling();

        Log.Info("Suspend", $"Watching memory this session. Opted-in apps are frozen only after {_config.MemoryPressure.ConsecutiveChecksRequired} consecutive checks showing under {_config.MemoryPressure.FreeMemoryThresholdGb:0.#} GB available AND over {_config.MemoryPressure.HardFaultsPerSecondThreshold:0} hard faults/sec, ignoring the first {_config.MemoryPressure.GracePeriodSeconds}s while VRChat loads.");
    }

    private void StopWatching()
    {
        lock (_lock)
        {
            _watchTimer?.Dispose();
            _watchTimer = null;
        }
    }

    private void SafeCheckMemoryPressure()
    {
        try { CheckMemoryPressure(); }
        catch (Exception ex) { Log.Debug("Suspend", $"Memory pressure check threw: {ex.Message}"); }
    }

    /// <summary>
    /// Freezes the opted-in applications if free memory has fallen below the threshold.
    ///
    /// Only ever fires once per session. There is no thaw-when-memory-recovers counterpart, and
    /// that is deliberate: memory recovers BECAUSE these processes were frozen, so resuming on
    /// recovery would immediately undo the fix and oscillate. They stay frozen until the session
    /// ends, which is the point at which the user wants them back anyway.
    /// </summary>
    public void CheckMemoryPressure()
    {
        lock (_lock)
        {
            if (_suspended.Count > 0) return; // already acted this session

            // VRChat allocates heavily while loading a world and its avatars. Memory is at its
            // most transient exactly then and recovers on its own, so acting inside this window
            // freezes the user's applications to solve a problem that was about to disappear.
            if (_sessionStartedAt is DateTime started
                && (DateTime.UtcNow - started).TotalSeconds < _config.MemoryPressure.GracePeriodSeconds)
                return;
        }

        var freeGb = _freeMemoryGb();
        if (freeGb < 0) return; // could not read it; do nothing rather than guess

        var faults = _hardFaultsPerSecond();
        var memoryLow = freeGb < _config.MemoryPressure.FreeMemoryThresholdGb;

        // Faults corroborate rather than decide. A negative reading means "unavailable" (including
        // the first sample of a session), and a threshold of 0 disables the requirement entirely —
        // in both cases fall back to the memory reading alone rather than never acting.
        var faultsRequired = _config.MemoryPressure.HardFaultsPerSecondThreshold > 0;
        var faultsHigh = !faultsRequired || faults < 0
            || faults >= _config.MemoryPressure.HardFaultsPerSecondThreshold;

        if (!memoryLow || !faultsHigh)
        {
            lock (_lock) _consecutivePressureChecks = 0; // pressure has to be SUSTAINED
            return;
        }

        int consecutive;
        lock (_lock) consecutive = ++_consecutivePressureChecks;

        if (consecutive < _config.MemoryPressure.ConsecutiveChecksRequired)
        {
            Log.Debug("Suspend", $"Memory pressure {consecutive}/{_config.MemoryPressure.ConsecutiveChecksRequired}: {freeGb:0.#} GB available, {(faults < 0 ? "fault rate unknown" : $"{faults:0} hard faults/sec")}.");
            return;
        }

        Log.Info("Suspend", $"Sustained memory pressure: {freeGb:0.#} GB available with {(faults < 0 ? "an unknown fault rate" : $"{faults:0} hard faults/sec")}, over {consecutive} consecutive checks — freezing the opted-in apps to release theirs.");
        SuspendConfiguredApps();
    }

    /// <param name="onlyDisplayName">Restricts the sweep to one app, for the status view's
    /// per-app toggle. Null means every opted-in app, the pressure-triggered path.</param>
    public void SuspendConfiguredApps(string? onlyDisplayName = null)
    {
        var apps = _config.ManagedApps.Where(a => a.SuspendDuringSession)
            .Where(a => onlyDisplayName is null
                || string.Equals(a.DisplayName, onlyDisplayName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (apps.Count == 0) return;

        var frozen = 0;

        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.ProcessName)) continue;

            lock (_lock)
            {
                // Thawed by hand this session - leave it alone, or the toggle in the status view
                // would be undone by the next pressure check.
                if (_userThawed.Contains(app.DisplayName)) continue;
            }

            foreach (var pid in _suspender.GetProcessIds(app.ProcessName))
            {
                lock (_lock)
                {
                    if (_suspended.ContainsKey(pid)) continue; // already frozen this session
                }

                if (SuspendSafety.RefusalReason(app.ProcessName, pid, _currentProcessId) is string refusal)
                {
                    Log.Warn("Suspend", $"Refusing to freeze {app.DisplayName} (PID {pid}): {refusal}.");
                    continue;
                }

                if (!_suspender.Suspend(pid))
                {
                    Log.Debug("Suspend", $"Could not freeze {app.DisplayName} (PID {pid}) — probably elevated above this app.");
                    continue;
                }

                lock (_lock) _suspended[pid] = app.DisplayName;
                frozen++;

                // Only after a successful suspend: trimming a running process just makes it fault
                // its pages straight back in, which costs disk I/O and achieves nothing.
                _suspender.TrimWorkingSet(pid);
            }
        }

        if (frozen > 0)
            Log.Info("Suspend", $"Froze {frozen} process(es) for the session and released their memory. All will be resumed when SteamVR stops.");
    }

    public void ResumeAll()
    {
        List<KeyValuePair<int, string>> toResume;
        lock (_lock)
        {
            _userThawed.Clear(); // the session is over; next one starts from the config again
            if (_suspended.Count == 0) return;
            toResume = _suspended.ToList();
            _suspended.Clear();
        }

        var resumed = 0;
        var failed = new List<string>();

        foreach (var (pid, name) in toResume)
        {
            if (!_suspender.IsRunning(pid)) continue; // exited while frozen; nothing to do

            if (_suspender.Resume(pid)) resumed++;
            else failed.Add($"{name} (PID {pid})");
        }

        if (resumed > 0) Log.Info("Suspend", $"Resumed {resumed} process(es) now the session has ended.");

        // Loud, because the symptom of getting this wrong is an application that appears hung and
        // gives the user no way to work out why.
        if (failed.Count > 0)
            Log.Error("Suspend", $"FAILED to resume: {string.Join(", ", failed)}. These are still frozen and will look hung — resume them with Process Explorer, or end and restart them.");
    }

    /// <summary>
    /// Thaws one app by hand and keeps it thawed for the rest of the session.
    ///
    /// The status view's per-app toggle: a frozen editor is exactly the thing you want back for a
    /// minute mid-session, and without the remembered override the next pressure check would
    /// simply freeze it again. "Rest of the session" is the whole scope - StartWatching and
    /// ResumeAll both clear it, so the next session goes back to obeying the config.
    /// </summary>
    public void ThawApp(string displayName)
    {
        List<int> pids;
        lock (_lock)
        {
            _userThawed.Add(displayName);
            pids = _suspended.Where(kv => string.Equals(kv.Value, displayName, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            foreach (var pid in pids) _suspended.Remove(pid);
        }

        if (pids.Count == 0)
        {
            Log.Info("Suspend", $"'{displayName}' will not be frozen again this session.");
            return;
        }

        var resumed = 0;
        var failed = new List<string>();
        foreach (var pid in pids)
        {
            if (!_suspender.IsRunning(pid)) continue;
            if (_suspender.Resume(pid)) resumed++;
            else failed.Add($"{displayName} (PID {pid})");
        }

        if (resumed > 0)
            Log.Info("Suspend", $"Thawed {resumed} process(es) of '{displayName}' on request; it will not be frozen again this session.");
        if (failed.Count > 0)
            Log.Error("Suspend", $"FAILED to thaw: {string.Join(", ", failed)}. Still frozen and will look hung - resume with Process Explorer, or end and restart.");
    }

    /// <summary>Re-freezes an app the user thawed, dropping the override so pressure governs it
    /// again. Freezes immediately if it is opted in, matching the toggle's "on means frozen now"
    /// reading rather than "frozen the next time memory gets tight".</summary>
    public void RefreezeApp(string displayName)
    {
        lock (_lock) _userThawed.Remove(displayName);
        SuspendConfiguredApps(onlyDisplayName: displayName);
    }

    /// <summary>
    /// Brings what is frozen back in line with the config, for when the opted-in set changes
    /// mid-session.
    ///
    /// Unticking "freeze this app" used to have no effect until the session ended, because the
    /// opted-in list was only ever read at the moment of freezing - the app stayed frozen with no
    /// way to get it back. Anything still frozen that is no longer opted in gets thawed here.
    /// </summary>
    public void ReconcileWithConfig()
    {
        var optedIn = _config.ManagedApps.Where(a => a.SuspendDuringSession)
            .Select(a => a.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> noLongerWanted;
        lock (_lock)
        {
            noLongerWanted = _suspended.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !optedIn.Contains(name)).ToList();
        }

        foreach (var name in noLongerWanted)
        {
            Log.Info("Suspend", $"'{name}' is no longer set to freeze - thawing it now.");
            ThawApp(name);
            // Not left in _userThawed: the override is for a deliberate mid-session thaw. Re-ticking
            // the box should let pressure freeze it again without needing the toggle as well.
            lock (_lock) _userThawed.Remove(name);
        }
    }

    /// <summary>True if any process of this app is frozen right now.</summary>
    public bool IsFrozen(string displayName)
    {
        lock (_lock)
            return _suspended.Values.Any(n => string.Equals(n, displayName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True if the user thawed this app by hand this session, so pressure will leave it
    /// alone until the session ends.</summary>
    public bool IsThawedByUser(string displayName)
    {
        lock (_lock) return _userThawed.Contains(displayName);
    }

    /// <summary>Which apps are frozen right now, for the status view.</summary>
    public IReadOnlyList<string> FrozenAppNames
    {
        get { lock (_lock) return _suspended.Values.Distinct().OrderBy(n => n).ToList(); }
    }

    public int FrozenCount { get { lock (_lock) return _suspended.Count; } }

    /// <summary>Stops the watch. Resuming frozen processes is the caller's job on shutdown, since
    /// it must happen whether or not this was ever disposed cleanly.</summary>
    public void Dispose() => StopWatching();
}
