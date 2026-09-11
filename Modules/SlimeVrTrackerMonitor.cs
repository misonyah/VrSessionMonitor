using System.Net.NetworkInformation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class TrackerStatus
{
    public required TrackerConfig Tracker { get; init; }
    public bool IsOnline { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// ICMP-pings every known SlimeVR tracker board directly, independent of whether the SlimeVR
/// server is running. Trackers are ESP-based and answer ping as soon as they're powered on and
/// joined to WiFi, so this catches "tracker is dead/off" before you ever launch the server.
/// </summary>
public sealed class SlimeVrTrackerMonitor : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly Dictionary<string, TrackerStatus> _status = new();
    private readonly Func<string, Task<bool>> _ping;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public IReadOnlyDictionary<string, TrackerStatus> Status => _status;

    /// <param name="pingOverride">Injectable reachability check, same seam as HeadsetMonitor's -
    /// the debounce logic is only testable if failures can be scripted rather than waiting on a
    /// real board to drop real packets.</param>
    public SlimeVrTrackerMonitor(MonitorConfig config, Func<string, Task<bool>>? pingOverride = null)
    {
        _config = config;
        _ping = pingOverride ?? PingAsync;
        foreach (var t in config.Trackers)
            _status[t.Ip] = new TrackerStatus { Tracker = t };
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("SlimeVrTrackers", $"Started. Tracking {_config.Trackers.Count} boards, interval={_config.Polling.TrackerCheckIntervalMs}ms");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("SlimeVrTrackers", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await CheckAllAsync().ConfigureAwait(false);
            try { await Task.Delay(_config.Polling.TrackerCheckIntervalMs, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Pings every tracker in parallel and returns the fresh snapshot. Used both by the
    /// background loop and as an explicit pre-flight check before starting the SlimeVR server.</summary>
    public async Task<IReadOnlyDictionary<string, TrackerStatus>> CheckAllAsync()
    {
        var tasks = _config.Trackers.Select(CheckOneAsync);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return _status;
    }

    private async Task<bool> PingAsync(string ip)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(ip, _config.Polling.TrackerPingTimeoutMs).ConfigureAwait(false);
        return reply.Status == IPStatus.Success;
    }

    private async Task CheckOneAsync(TrackerConfig tracker)
    {
        bool online;
        try
        {
            online = await _ping(tracker.Ip).ConfigureAwait(false);
            Log.Trace("SlimeVrTrackers", $"{tracker.Name,-16} {tracker.Ip,-15} -> {(online ? "OK" : "unreachable")}");
        }
        catch (Exception ex)
        {
            online = false;
            Log.Debug("SlimeVrTrackers", $"{tracker.Name} ({tracker.Ip}) ping threw: {ex.Message}");
        }

        var status = _status[tracker.Ip];
        var wasOnline = status.IsOnline;
        status.ConsecutiveFailures = online ? 0 : status.ConsecutiveFailures + 1;

        // Debounced online->offline only (see PollingConfig.TrackerOfflineDebounceFailures).
        // ConsecutiveFailures was already being counted here but never consulted, so one dropped
        // ping declared a live tracker offline and the next good ping undid it - the flapping
        // that gets reported after a session. A single success still restores it immediately.
        var failureThreshold = Math.Max(1, _config.Polling.TrackerOfflineDebounceFailures);
        if (online)
        {
            status.IsOnline = true;
            status.LastSeenUtc = DateTime.UtcNow;
        }
        else if (status.ConsecutiveFailures >= failureThreshold)
        {
            status.IsOnline = false;
        }
        else if (wasOnline)
        {
            Log.Trace("SlimeVrTrackers", $"{tracker.Name} ping failure {status.ConsecutiveFailures}/{failureThreshold} - not yet declaring offline.");
        }

        if (wasOnline && !status.IsOnline)
        {
            var extNote = tracker.HasExtension ? " (has an extension sensor — if only the extension half is missing in SlimeVR, a physical power-cycle may be needed; see firmware notes)" : "";
            Log.Warn("SlimeVrTrackers", $"Tracker '{tracker.Name}' ({tracker.Ip}) went OFFLINE.{extNote}");
        }
        else if (!wasOnline && status.IsOnline)
        {
            Log.Info("SlimeVrTrackers", $"Tracker '{tracker.Name}' ({tracker.Ip}) came back ONLINE.");
        }
    }

    /// <summary>How many trackers are up, for the status view's state dot.</summary>
    public int OnlineCount => _status.Values.Count(s => s.IsOnline);

    /// <summary>How many trackers are configured at all.</summary>
    public int TotalCount => _status.Count;

    /// <summary>Human-readable summary for logs/tray tooltip, e.g. "8/8 trackers online".</summary>
    public string Summarize()
    {
        var online = _status.Values.Count(s => s.IsOnline);
        var total = _status.Count;
        var down = _status.Values.Where(s => !s.IsOnline).Select(s => s.Tracker.Name).ToList();
        return down.Count == 0
            ? $"{online}/{total} trackers online"
            : $"{online}/{total} trackers online (down: {string.Join(", ", down)})";
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
