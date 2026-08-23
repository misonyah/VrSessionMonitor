using System.Diagnostics;
using System.Windows.Automation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Owns VRCOSC.exe's start/stop lifecycle, driven purely by VrChatMonitor's VRChat.exe
/// presence — launches VRCOSC the moment VRChat is seen running, and closes the whole process
/// (not just whatever "stop running" means inside VRCOSC's own UI) after VRChat has been gone
/// for VrcOscLifecycleConfig.ShutdownDelayMs. Replaces the manual launch/kill previously done by
/// hand in vd.cmd ("start VRCOSC" tasklist check) and kill.cmd ("Taskkill VRCOSC.exe").
///
/// VRCOSC has its own internal "start with VRChat" setting, but that only governs whether its
/// modules begin doing work once VRCOSC is already open — it has no bearing on whether the
/// VRCOSC.exe process itself is running, which is what this class manages instead.
/// </summary>
public sealed class VrcOscLifecycleManager : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly VrChatMonitor _vrChat;
    private readonly ProcessLauncher _launcher = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private DateTime? _vrChatGoneSinceUtc;
    private DateTime? _lastUpdatePromptClickUtc;

    public VrcOscLifecycleManager(MonitorConfig config, VrChatMonitor vrChat)
    {
        _config = config;
        _vrChat = vrChat;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("VrcOscLifecycle", "Started VRCOSC presence-based start/stop management.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("VrcOscLifecycle", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("VrcOscLifecycle", $"Check cycle threw: {ex.Message}");
            }

            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task CheckOnceAsync()
    {
        if (!(_config.GetApp("vrcosc")?.Enabled ?? _config.VrcOscLifecycle.Enabled)) return;

        // VRCOSC's own "Update Available" dialog blocks on a manual Yes/No — auto-accept it (when
        // enabled) so an update prompt doesn't sit there stalling the session. Only scans when
        // VRCOSC is actually running.
        if (_config.VrcOscLifecycle.AutoAcceptUpdatePrompt && ProcessLauncher.IsRunning("VRCOSC"))
            TryAcceptUpdatePromptIfPresent();

        if (_vrChat.Current.Running)
        {
            _vrChatGoneSinceUtc = null;

            if (!ProcessLauncher.IsRunning("VRCOSC"))
            {
                Log.Info("VrcOscLifecycle", "VRChat detected and VRCOSC isn't running — launching it.");
                var result = await _launcher.EnsureRunningAsync(
                    "VRCOSC", _config.LaunchTargetFor("vrcosc", _config.Paths.VrcOscExe), null,
                    _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);

                if (!result.Success && !result.AlreadyRunning)
                    Log.Warn("VrcOscLifecycle", $"VRCOSC launch did not confirm success: {result.Error}");
            }

            return;
        }

        // VRChat isn't running.
        if (!ProcessLauncher.IsRunning("VRCOSC"))
        {
            _vrChatGoneSinceUtc = null; // nothing running, nothing to shut down
            return;
        }

        var now = DateTime.UtcNow;
        _vrChatGoneSinceUtc ??= now;

        var elapsed = now - _vrChatGoneSinceUtc.Value;
        var threshold = TimeSpan.FromMilliseconds(_config.VrcOscLifecycle.ShutdownDelayMs);
        if (elapsed < threshold) return;

        Log.Warn("VrcOscLifecycle", $"VRChat has been gone for {elapsed.TotalSeconds:F0}s — closing VRCOSC.exe.");
        try
        {
            foreach (var proc in Process.GetProcessesByName("VRCOSC"))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("VrcOscLifecycle", "Killing VRCOSC.exe threw", ex);
        }

        _vrChatGoneSinceUtc = null;
    }

    /// <summary>Auto-clicks "Yes" on VRCOSC's own "Update Available" dialog (VolcanicArts' updater)
    /// so it applies the update instead of blocking on a manual click. UI Automation, same approach
    /// as BaballoniaAutomation — never throws (any failure just logs and is retried on the next
    /// poll), and cooldown-guarded so a dialog it can't dismiss isn't hammered every cycle. Matches
    /// ONLY the update dialog (a top-level window whose title mentions both "VRCOSC" and "Update"
    /// AND that actually has a "Yes" button), so it can't misfire on VRCOSC's main window or any
    /// unrelated window. NOTE: UI Automation isn't safe for truly concurrent access (see
    /// BaballoniaAutomation) — a rare overlap with a Baballonia camera restart would just throw and
    /// get swallowed here, then retry next poll.</summary>
    private void TryAcceptUpdatePromptIfPresent()
    {
        var now = DateTime.UtcNow;
        if (_lastUpdatePromptClickUtc is DateTime last && now - last < TimeSpan.FromSeconds(30))
            return;

        try
        {
            foreach (AutomationElement win in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                string name;
                try { name = win.Current.Name ?? ""; }
                catch { continue; } // a window that vanished mid-scan, or one we can't read — skip

                // Cheap filter first: only a yes/no dialog has a "Yes" button — VRCOSC's main window
                // ("VRCOSC <version>") has none, confirmed via a live UI Automation probe, so it can't
                // misfire there. Then confirm it's actually the update prompt by requiring BOTH
                // "VRCOSC" and "update" in the title or body text, so we never click Yes on an
                // unrelated confirmation dialog. Matching the body text (the prompt reads "A new update
                // is available for VRCOSC!") rather than the exact title makes this robust to whatever
                // the dialog's title bar actually says.
                var yes = win.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Yes")));
                if (yes is null) continue;

                if (!MentionsVrcoscUpdate(win, name)) continue;

                _lastUpdatePromptClickUtc = now;
                Log.Info("VrcOscLifecycle", $"VRCOSC update prompt detected ('{name}') — auto-clicking 'Yes' to apply the update.");
                SteamVrNotifier.TryNotify(_config, "VRCOSC updating");
                Invoke(yes);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("VrcOscLifecycle", $"VRCOSC update-prompt check threw: {ex.Message}");
        }
    }

    /// <summary>True when this window's title or any of its Text elements mention both "VRCOSC" and
    /// "update" — the confirmation that a yes/no dialog really is VRCOSC's update prompt, independent
    /// of the exact title-bar text.</summary>
    private static bool MentionsVrcoscUpdate(AutomationElement win, string title)
    {
        var hay = title;
        try
        {
            var texts = win.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement t in texts)
            {
                try { hay += " " + t.Current.Name; }
                catch { /* skip an element we can't read */ }
            }
        }
        catch { /* fall back to the title alone */ }

        return hay.Contains("VRCOSC", StringComparison.OrdinalIgnoreCase)
            && hay.Contains("update", StringComparison.OrdinalIgnoreCase);
    }

    private static void Invoke(AutomationElement element)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var pattern = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);
                pattern.Invoke();
                return;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                Log.Debug("VrcOscLifecycle", $"UI Automation Invoke() failed on attempt {attempt}/{maxAttempts}, retrying.");
                Thread.Sleep(150);
            }
        }
    }

    /// <summary>Surfaces the shutdown-after-VRChat-gone countdown for the tray, matching
    /// VrcFaceTrackingLifecycleManager.DescribePendingAction — otherwise the only way to know a
    /// close was imminent was to already be watching the log.</summary>
    public string? DescribePendingAction()
    {
        if (!(_config.GetApp("vrcosc")?.Enabled ?? _config.VrcOscLifecycle.Enabled)) return null;

        if (_vrChatGoneSinceUtc is DateTime since)
        {
            var remaining = TimeSpan.FromMilliseconds(_config.VrcOscLifecycle.ShutdownDelayMs) - (DateTime.UtcNow - since);
            if (remaining > TimeSpan.Zero)
                return $"VRCOSC shutdown in {remaining.TotalSeconds:F0}s";
        }

        return null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
