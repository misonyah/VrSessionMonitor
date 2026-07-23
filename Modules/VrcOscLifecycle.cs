using System.Diagnostics;
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
        if (!_config.VrcOscLifecycle.Enabled) return;

        if (_vrChat.Current.Running)
        {
            _vrChatGoneSinceUtc = null;

            if (!ProcessLauncher.IsRunning("VRCOSC"))
            {
                Log.Info("VrcOscLifecycle", "VRChat detected and VRCOSC isn't running — launching it.");
                var result = await _launcher.EnsureRunningAsync(
                    "VRCOSC", _config.Paths.VrcOscExe, null,
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

    /// <summary>Surfaces the shutdown-after-VRChat-gone countdown for the tray, matching
    /// VrcFaceTrackingLifecycleManager.DescribePendingAction — otherwise the only way to know a
    /// close was imminent was to already be watching the log.</summary>
    public string? DescribePendingAction()
    {
        if (!_config.VrcOscLifecycle.Enabled) return null;

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
