using System.Collections.Concurrent;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Watches the configured managed apps and applies each one's window rules when its process
/// appears — whether VrSessionMonitor launched it or the user started it by hand. The manual case
/// is the point: "minimize VRChat even when I start it myself" is only possible by noticing an
/// externally-started process and acting on it.
///
/// Rules are applied once per process instance (keyed on PID), so a minimised window the user then
/// restores deliberately doesn't get re-minimised on the next poll.
/// </summary>
public sealed class ManagedAppWindowService : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly IProcessLauncher _launcher;
    /// <summary>How many polls to keep re-applying a window rule that hasn't taken effect before
    /// giving up, when the config doesn't say. At a 1s process poll (so a 2s loop) the default
    /// spans three minutes.
    ///
    /// Raised from 12 (24 seconds) after OVR Toolkit was seen giving up on a Minimized rule at the
    /// old ceiling: some apps take a long time to swap their startup window for the real one, and
    /// Steam-launched ones add Steam's own startup on top. A longer ceiling costs nothing for a
    /// fast app — retrying stops the moment the state is satisfied, so only apps that would have
    /// failed anyway keep trying.</summary>
    private const int DefaultMaxRuleAttempts = 90;

    private int MaxRuleAttempts => _config.Polling.WindowRuleMaxAttempts > 0
        ? _config.Polling.WindowRuleMaxAttempts
        : DefaultMaxRuleAttempts;

    private readonly Dictionary<string, int> _rulesAppliedForPid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _ruleAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AppStartOrigin> _origins = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public ManagedAppWindowService(MonitorConfig config, IProcessLauncher launcher)
    {
        _config = config;
        _launcher = launcher;
    }

    /// <summary>How each managed app's process was started, for the Status tab and tray tooltip.
    /// Written from the background polling loop and read from the UI thread, so a plain Dictionary
    /// would risk a concurrent-enumeration failure — same reasoning as LaunchProvenance.</summary>
    public IReadOnlyDictionary<string, AppStartOrigin> CurrentOrigins => _origins;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("ManagedApps", $"Watching {_config.ManagedApps.Count} managed app(s) for window rules.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await CheckOnceAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("ManagedApps", $"Window-rule pass threw: {ex.Message}"); }

            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 2, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task CheckOnceAsync()
    {
        foreach (var app in _config.ManagedApps.ToArray())
        {
            if (string.IsNullOrWhiteSpace(app.ProcessName)) continue;

            var pid = _launcher.GetProcessId(app.ProcessName);
            var origin = _launcher.Provenance.Classify(app.ProcessName, pid);
            _origins[app.Id] = origin;

            if (pid is not int currentPid)
            {
                // Gone — drop the applied marker so a future instance gets rules applied again.
                _rulesAppliedForPid.Remove(app.Id);
                continue;
            }

            if (_rulesAppliedForPid.TryGetValue(app.Id, out var done) && done == currentPid) continue;

            if (origin == AppStartOrigin.Manual && !app.ApplyWindowRulesWhenStartedManually)
            {
                _rulesAppliedForPid[app.Id] = currentPid; // decided: leave this instance alone
                _ruleAttempts.Remove(app.Id);
                continue;
            }

            var hasRules = app.WindowState != AppWindowState.Unchanged || app.BringToFront
                           || app.KeepInBackground || app.TargetMonitor is not null;
            if (!hasRules)
            {
                _rulesAppliedForPid[app.Id] = currentPid;
                _ruleAttempts.Remove(app.Id);
                continue;
            }

            // Window rules act on whichever process owns a window, NOT necessarily the first one
            // matching the name — SlimeVR runs five same-named processes and only one has the GUI.
            // Falls back to the presence pid so a single-process app is unaffected.
            var windowPid = WindowController.ResolveWindowedProcessId(app.ProcessName) ?? currentPid;

            var attempt = _ruleAttempts.GetValueOrDefault(app.Id) + 1;
            _ruleAttempts[app.Id] = attempt;

            if (attempt == 1)
                Log.Info("ManagedApps", $"{app.DisplayName} started ({origin}) — applying window rules (state={app.WindowState}, front={app.BringToFront}, background={app.KeepInBackground}, monitor={app.TargetMonitor?.ToString() ?? "any"}).");

            await WindowController.ApplyAsync(windowPid, app.WindowState, app.BringToFront,
                app.KeepInBackground, app.TargetMonitor).ConfigureAwait(false);

            // Verify rather than assume. Confirmed live 2026-08-22: VRChat exposed a main window ~2s
            // after launch, accepted the minimise, then opened its REAL window unminimised — the
            // rule silently didn't stick and the transient minimise looked like the window blinking.
            // Retrying across a few polls lets the app settle instead of trusting one shot.
            if (WindowController.IsStateSatisfied(windowPid, app.WindowState))
            {
                _rulesAppliedForPid[app.Id] = currentPid;
                _ruleAttempts.Remove(app.Id);
                if (attempt > 1)
                    Log.Info("ManagedApps", $"{app.DisplayName}: window rules took effect after {attempt} attempts.");
            }
            else if (attempt >= MaxRuleAttempts)
            {
                // Give up rather than fight the app (or the user) forever.
                _rulesAppliedForPid[app.Id] = currentPid;
                _ruleAttempts.Remove(app.Id);
                var waitedSeconds = attempt * _config.Polling.ProcessPollIntervalMs * 2 / 1000;
                Log.Warn("ManagedApps", $"{app.DisplayName}: window state is still not {app.WindowState} after {attempt} attempts over {waitedSeconds}s — giving up for this instance. The app may be overriding it, or it may not honour external window changes. Raise Polling.WindowRuleMaxAttempts if it just needs longer.");
            }
            // else: deliberately leave the marker unset so the next poll retries.
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
