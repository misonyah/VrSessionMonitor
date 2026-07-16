using System.Net.NetworkInformation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class HeadsetStateChangedEventArgs : EventArgs
{
    public bool IsOnline { get; init; }
    public string Ip { get; init; } = "";
}

/// <summary>
/// Pings the headset's fixed LAN IP on an interval. Port-38830 detection (as used by the old
/// vrc.cmd) is NOT used as the primary signal — live testing on 2026-07-15 showed it fires on
/// an outbound WAN connection from VD Streamer to Virtual Desktop's cloud service before the
/// headset ever connects, which would trigger a launch prematurely.
/// </summary>
public sealed class HeadsetMonitor : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly Ping _ping = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _lastKnownOnline;

    public event EventHandler<HeadsetStateChangedEventArgs>? StateChanged;
    public bool IsOnline { get; private set; }

    public HeadsetMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("HeadsetMonitor", $"Started. Target={_config.Network.HeadsetIp} ({_config.Network.HeadsetName}), " +
                                    $"interval={_config.Polling.HeadsetPingIntervalMs}ms, timeout={_config.Polling.HeadsetPingTimeoutMs}ms");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("HeadsetMonitor", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await CheckOnceAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(_config.Polling.HeadsetPingIntervalMs, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task<bool> CheckOnceAsync()
    {
        var ip = _config.Network.HeadsetIp;
        bool online;
        try
        {
            var reply = await _ping.SendPingAsync(ip, _config.Polling.HeadsetPingTimeoutMs).ConfigureAwait(false);
            online = reply.Status == IPStatus.Success;
            Log.Trace("HeadsetMonitor", online
                ? $"Ping {ip} OK, roundtrip={reply.RoundtripTime}ms"
                : $"Ping {ip} failed, status={reply.Status}");
        }
        catch (Exception ex)
        {
            online = false;
            Log.Debug("HeadsetMonitor", $"Ping {ip} threw: {ex.Message}");
        }

        IsOnline = online;

        if (online != _lastKnownOnline)
        {
            Log.Info("HeadsetMonitor", $"State transition: {(_lastKnownOnline ? "online" : "offline")} -> {(online ? "online" : "offline")}");
            _lastKnownOnline = online;
            StateChanged?.Invoke(this, new HeadsetStateChangedEventArgs { IsOnline = online, Ip = ip });
        }

        return online;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _ping.Dispose();
    }
}
