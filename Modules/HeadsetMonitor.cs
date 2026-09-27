using System.Net.NetworkInformation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class HeadsetStateChangedEventArgs : EventArgs
{
    public bool IsOnline { get; init; }
    public string Ip { get; init; } = "";
    /// <summary>Profile id of the headset that is now online (also set on a switch while online);
    /// "" for the offline edge.</summary>
    public string HeadsetId { get; init; } = "";
}

/// <summary>
/// Pings the headset's fixed LAN IP on an interval. Port-38830 detection (as used by the old
/// vrc.cmd) is NOT used as the primary signal — live testing on 2026-07-15 showed it fires on
/// an outbound WAN connection from VD Streamer to Virtual Desktop's cloud service before the
/// headset ever connects, which would trigger a launch prematurely.
/// </summary>
public sealed class HeadsetMonitor : IHeadsetMonitor, IDisposable
{
    private readonly MonitorConfig _config;
    private readonly Ping _ping = new();
    private readonly Func<string, Task<bool>> _ping2;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _lastKnownOnline;
    private int _consecutiveFailures;
    private string _lastKnownHeadsetId = "";

    public event EventHandler<HeadsetStateChangedEventArgs>? StateChanged;
    public bool IsOnline { get; private set; }
    /// <summary>Whichever configured IP actually answered the last successful ping (primary or
    /// secondary). Meaningless when IsOnline is false.</summary>
    public string RespondingIp { get; private set; } = "";
    public string ActiveHeadsetId { get; private set; } = "";

    public HeadsetMonitor(MonitorConfig config, Func<string, Task<bool>>? pingOverride = null)
    {
        _config = config;
        _ping2 = pingOverride ?? PingAsync;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        var target = string.Join(" | ", HeadsetProfiles.Effective(_config).Select(p => $"{p.Id}: {string.Join(" or ", p.DetectHosts)}"));
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
        var (online, ip, headsetId) = await ProbeProfilesAsync().ConfigureAwait(false);
        if (online)
        {
            RespondingIp = ip;
            ActiveHeadsetId = headsetId;
        }

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
        if (!online) ActiveHeadsetId = "";

        var switched = online && _lastKnownOnline && headsetId != _lastKnownHeadsetId;
        if (online != _lastKnownOnline || switched)
        {
            Log.Info("HeadsetMonitor", switched
                ? $"Headset switched: {_lastKnownHeadsetId} -> {headsetId}"
                : $"State transition: {(_lastKnownOnline ? "online" : "offline")} -> {(online ? "online" : "offline")}{(online ? $" ({headsetId})" : "")}");
            _lastKnownOnline = online;
            _lastKnownHeadsetId = online ? headsetId : "";
            _consecutiveFailures = 0;
            StateChanged?.Invoke(this, new HeadsetStateChangedEventArgs { IsOnline = online, Ip = ip, HeadsetId = online ? headsetId : "" });
        }

        return online;
    }

    /// <summary>Pings profiles in priority order: the pinned one only if set; otherwise the last
    /// active one first, then the rest in list order. Within a profile, hosts in order. First
    /// answer wins, so a Quest and a Frame both on the LAN resolve to the one last used.
    /// Ping.SendPingAsync resolves hostnames (e.g. "frame") itself; a resolution failure is caught
    /// in PingAsync and simply reads as "no answer".</summary>
    private async Task<(bool online, string ip, string headsetId)> ProbeProfilesAsync()
    {
        var all = HeadsetProfiles.Effective(_config).Where(HeadsetProfiles.IsDetectable).ToList();
        IEnumerable<HeadsetProfile> order;
        var pinned = HeadsetProfiles.Find(_config, string.IsNullOrWhiteSpace(_config.PinnedHeadset) ? null : _config.PinnedHeadset);
        var current = _lastKnownOnline ? HeadsetProfiles.Find(_config, _lastKnownHeadsetId) : null;
        if (pinned is not null)
            order = new[] { pinned };
        else if (current is not null)
            // While a headset is active only it is probed: one missed ping must count toward the
            // offline debounce, not hand the session to another headset that happens to be on the
            // LAN (that killed Quest face tracking mid-session). A switch goes offline -> online.
            order = new[] { current };
        else
        {
            var preferred = string.IsNullOrEmpty(_lastKnownHeadsetId) ? _config.LastActiveHeadset : _lastKnownHeadsetId;
            order = all.OrderBy(p => string.Equals(p.Id, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }

        foreach (var p in order)
            foreach (var host in p.DetectHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
                if (await _ping2(host).ConfigureAwait(false))
                    return (true, host, p.Id);

        return (false, "", "");
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
