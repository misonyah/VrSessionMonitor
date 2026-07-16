using System.Diagnostics;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class SteamVrStatus
{
    public bool VrServerRunning { get; set; }
    public bool VrMonitorRunning { get; set; }
    public bool VrCompositorRunning { get; set; }
}

/// <summary>
/// Process-presence based SteamVR check. VD's OpenVR driver registers vrserver.exe just like a
/// real headset runtime, so this doubles as a sanity check that the VD -> OpenVR bridge came up.
/// </summary>
public sealed class SteamVrMonitor : IDisposable
{
    private readonly MonitorConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private SteamVrStatus _last = new();

    public SteamVrStatus Current => _last;

    public SteamVrMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("SteamVR", "Started process-presence monitoring for vrserver/vrmonitor/vrcompositor.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("SteamVR", "Stopped.");
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

    public SteamVrStatus CheckOnce()
    {
        var status = new SteamVrStatus
        {
            VrServerRunning = IsRunning("vrserver"),
            VrMonitorRunning = IsRunning("vrmonitor"),
            VrCompositorRunning = IsRunning("vrcompositor"),
        };

        if (status.VrServerRunning != _last.VrServerRunning)
            Log.Info("SteamVR", $"vrserver.exe {(status.VrServerRunning ? "started" : "stopped")}");
        if (status.VrMonitorRunning != _last.VrMonitorRunning)
            Log.Info("SteamVR", $"vrmonitor.exe {(status.VrMonitorRunning ? "started" : "stopped")}");
        if (status.VrCompositorRunning != _last.VrCompositorRunning)
            Log.Info("SteamVR", $"vrcompositor.exe {(status.VrCompositorRunning ? "started" : "stopped")}");

        Log.Trace("SteamVR", $"vrserver={status.VrServerRunning} vrmonitor={status.VrMonitorRunning} vrcompositor={status.VrCompositorRunning}");

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
            Log.Debug("SteamVR", $"GetProcessesByName({processName}) threw: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
