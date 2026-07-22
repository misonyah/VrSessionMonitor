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

    private DateTime? _bothRunningSinceUtc;
    private bool _alreadyCheckedThisWindow;
    private int _consecutiveStuckRestarts;
    private DateTime? _giveUpUntilUtc;

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
            CheckStuckSession();
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

    /// <summary>
    /// Detects the exact stuck-session bug found live on 2026-07-21 — vrserver/vrcompositor
    /// running as OS processes but never producing real log output — and auto-restarts SteamVR.
    /// Only evaluates once per "both processes running" window (tracked via
    /// _alreadyCheckedThisWindow) so it doesn't re-fire every poll after the grace period elapses.
    /// </summary>
    private void CheckStuckSession()
    {
        if (!_config.SteamVrStuckSession.Enabled) return;

        if (!_last.VrServerRunning || !_last.VrCompositorRunning)
        {
            _bothRunningSinceUtc = null;
            _alreadyCheckedThisWindow = false;
            return;
        }

        _bothRunningSinceUtc ??= DateTime.UtcNow;
        if (_alreadyCheckedThisWindow) return;

        var elapsed = DateTime.UtcNow - _bothRunningSinceUtc.Value;
        if (elapsed < TimeSpan.FromMilliseconds(_config.SteamVrStuckSession.GracePeriodMs)) return;

        _alreadyCheckedThisWindow = true;

        if (_giveUpUntilUtc.HasValue && DateTime.UtcNow < _giveUpUntilUtc.Value)
        {
            Log.Debug("SteamVR", "Stuck-session check skipped — still within give-up cooldown from a previous failed recovery.");
            return;
        }

        if (!IsSessionStuck())
        {
            _consecutiveStuckRestarts = 0;
            return;
        }

        if (_consecutiveStuckRestarts >= _config.SteamVrStuckSession.GiveUpAfterAttempts)
        {
            Log.Error("SteamVR", $"SteamVR stuck-session recovery failed {_consecutiveStuckRestarts} times in a row — giving up for {_config.SteamVrStuckSession.GiveUpCooldownMs / 1000}s instead of restarting it forever.");
            _giveUpUntilUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(_config.SteamVrStuckSession.GiveUpCooldownMs);
            _consecutiveStuckRestarts = 0;
            return;
        }

        RestartSteamVr();
        _consecutiveStuckRestarts++;
        _bothRunningSinceUtc = null; // a new grace-period window starts once the fresh instance comes up
        _alreadyCheckedThisWindow = false;
    }

    /// <summary>vrserver.exe/vrcompositor.exe are considered stuck if either hasn't written to its
    /// own log file at all since vrcompositor started — the exact signature found live (0-byte
    /// vrserver.txt, vrcompositor.txt untouched since the previous day despite the process
    /// supposedly starting fresh).</summary>
    private bool IsSessionStuck()
    {
        try
        {
            using var compositorProc = Process.GetProcessesByName("vrcompositor").FirstOrDefault();
            if (compositorProc is null) return false; // shouldn't happen given the caller's guard, but don't act on a guess

            var sinceStart = compositorProc.StartTime;
            var producingOutput = LogFileHasOutputSince("vrcompositor.txt", sinceStart)
                                && LogFileHasOutputSince("vrserver.txt", sinceStart);
            return !producingOutput;
        }
        catch (Exception ex)
        {
            Log.Debug("SteamVR", $"Stuck-session check threw, assuming healthy rather than restarting on a guess: {ex.Message}");
            return false;
        }
    }

    private bool LogFileHasOutputSince(string fileName, DateTime sinceLocalTime)
    {
        var path = Path.Combine(_config.Paths.SteamVrLogDirectory, fileName);
        if (!File.Exists(path)) return false;

        var info = new FileInfo(path);
        return info.Length > 0 && info.LastWriteTime >= sinceLocalTime;
    }

    private void RestartSteamVr()
    {
        Log.Warn("SteamVR", "SteamVR session appears stuck (vrserver/vrcompositor running but producing no log output) — restarting it.");
        SteamVrNotifier.TryNotify(_config, "Restarting stuck SteamVR session");

        foreach (var name in new[] { "vrserver", "vrmonitor", "vrcompositor" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Warn("SteamVR", $"Killing {name}.exe threw: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true });
            Log.Info("SteamVR", "Triggered a fresh SteamVR launch via steam://rungameid/250820.");
        }
        catch (Exception ex)
        {
            Log.Error("SteamVR", "Failed to relaunch SteamVR via the steam:// protocol", ex);
        }
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
