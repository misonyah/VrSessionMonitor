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
    private readonly Dictionary<string, int> _rulesAppliedForPid = new(StringComparer.OrdinalIgnoreCase);
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
        foreach (var app in _config.ManagedApps)
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
                continue;
            }

            var hasRules = app.WindowState != AppWindowState.Unchanged || app.BringToFront
                           || app.KeepInBackground || app.TargetMonitor is not null;
            if (!hasRules)
            {
                _rulesAppliedForPid[app.Id] = currentPid;
                continue;
            }

            // Mark before awaiting so a slow window-wait can't cause a second concurrent attempt.
            _rulesAppliedForPid[app.Id] = currentPid;

            Log.Info("ManagedApps", $"{app.DisplayName} started ({origin}) — applying window rules (state={app.WindowState}, front={app.BringToFront}, background={app.KeepInBackground}, monitor={app.TargetMonitor?.ToString() ?? "any"}).");
            await WindowController.ApplyAsync(currentPid, app.WindowState, app.BringToFront,
                app.KeepInBackground, app.TargetMonitor).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
