using System.Diagnostics;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class VrChatStatus
{
    public bool Running { get; set; }
}

/// <summary>
/// Plain process-presence watch for VRChat.exe. Added 2026-07-17 — SessionOrchestrator already
/// checked this once during the session-start flow ("already running, skip launch"), but nothing
/// polled it afterward, so VRChat exiting mid-session went completely unlogged (confirmed live:
/// the user closed VRChat then SteamVR, and only the SteamVR half showed up in the log). This
/// mirrors SteamVrMonitor exactly, just for one process instead of three.
/// </summary>
public sealed class VrChatMonitor : IDisposable
{
    private readonly MonitorConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private VrChatStatus _last = new();

    public VrChatStatus Current => _last;

    public VrChatMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("VrChat", "Started process-presence monitoring for VRChat.exe.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("VrChat", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            CheckOnce();
            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    public VrChatStatus CheckOnce()
    {
        var status = new VrChatStatus { Running = IsRunning("VRChat") };

        if (status.Running != _last.Running)
            Log.Info("VrChat", $"VRChat.exe {(status.Running ? "started" : "stopped")}");

        Log.Trace("VrChat", $"running={status.Running}");

        _last = status;
        return status;
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception ex)
        {
            Log.Debug("VrChat", $"GetProcessesByName({processName}) threw: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
