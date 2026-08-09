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
    private int _consecutiveFailures;

    public event EventHandler<HeadsetStateChangedEventArgs>? StateChanged;
    public bool IsOnline { get; private set; }
    /// <summary>Whichever configured IP actually answered the last successful ping (primary or
    /// secondary). Meaningless when IsOnline is false.</summary>
    public string RespondingIp { get; private set; } = "";

    public HeadsetMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        var target = string.IsNullOrWhiteSpace(_config.Network.HeadsetIpSecondary)
            ? _config.Network.HeadsetIp
            : $"{_config.Network.HeadsetIp} or {_config.Network.HeadsetIpSecondary}";
        Log.Info("HeadsetMonitor", $"Started. Target={target} ({_config.Network.HeadsetName}), " +
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
        var online = await PingAsync(ip).ConfigureAwait(false);

        // Only try the secondary address if the primary one failed — a headset that can be on
        // either your normal WiFi or the PC's own hotspot, each with a different reserved IP.
        var secondaryIp = _config.Network.HeadsetIpSecondary;
        if (!online && !string.IsNullOrWhiteSpace(secondaryIp))
        {
            online = await PingAsync(secondaryIp).ConfigureAwait(false);
            if (online)
                ip = secondaryIp;
        }

        if (online)
            RespondingIp = ip;

        // Debounced: a single failed ping doesn't immediately declare the headset offline (see
        // HeadsetOfflineDebounceFailures' doc) — only the online-to-offline direction waits;
        // recovering back online still happens on the very first successful ping.
        if (online)
        {
            _consecutiveFailures = 0;
        }
        else if (_lastKnownOnline)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures < _config.Polling.HeadsetOfflineDebounceFailures)
            {
                Log.Trace("HeadsetMonitor", $"Ping failure {_consecutiveFailures}/{_config.Polling.HeadsetOfflineDebounceFailures} — not yet declaring offline.");
                IsOnline = true;
                return true;
            }
        }

        IsOnline = online;

        if (online != _lastKnownOnline)
        {
            Log.Info("HeadsetMonitor", $"State transition: {(_lastKnownOnline ? "online" : "offline")} -> {(online ? "online" : "offline")}");
            _lastKnownOnline = online;
            _consecutiveFailures = 0;
            StateChanged?.Invoke(this, new HeadsetStateChangedEventArgs { IsOnline = online, Ip = ip });
        }

        return online;
    }

    private async Task<bool> PingAsync(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return false;

        try
        {
            var reply = await _ping.SendPingAsync(ip, _config.Polling.HeadsetPingTimeoutMs).ConfigureAwait(false);
            var online = reply.Status == IPStatus.Success;
            Log.Trace("HeadsetMonitor", online
                ? $"Ping {ip} OK, roundtrip={reply.RoundtripTime}ms"
                : $"Ping {ip} failed, status={reply.Status}");
            return online;
        }
        catch (Exception ex)
        {
            Log.Debug("HeadsetMonitor", $"Ping {ip} threw: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _ping.Dispose();
    }
}
