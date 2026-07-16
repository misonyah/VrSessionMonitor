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
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public IReadOnlyDictionary<string, TrackerStatus> Status => _status;

    public SlimeVrTrackerMonitor(MonitorConfig config)
    {
        _config = config;
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

    private async Task CheckOneAsync(TrackerConfig tracker)
    {
        using var ping = new Ping();
        bool online;
        try
        {
            var reply = await ping.SendPingAsync(tracker.Ip, _config.Polling.TrackerPingTimeoutMs).ConfigureAwait(false);
            online = reply.Status == IPStatus.Success;
            Log.Trace("SlimeVrTrackers", $"{tracker.Name,-16} {tracker.Ip,-15} -> {(online ? $"OK {reply.RoundtripTime}ms" : reply.Status.ToString())}");
        }
        catch (Exception ex)
        {
            online = false;
            Log.Debug("SlimeVrTrackers", $"{tracker.Name} ({tracker.Ip}) ping threw: {ex.Message}");
        }

        var status = _status[tracker.Ip];
        var wasOnline = status.IsOnline;
        status.IsOnline = online;
        if (online) status.LastSeenUtc = DateTime.UtcNow;
        status.ConsecutiveFailures = online ? 0 : status.ConsecutiveFailures + 1;

        if (wasOnline && !online)
        {
            var extNote = tracker.HasExtension ? " (has an extension sensor — if only the extension half is missing in SlimeVR, a physical power-cycle may be needed; see firmware notes)" : "";
            Log.Warn("SlimeVrTrackers", $"Tracker '{tracker.Name}' ({tracker.Ip}) went OFFLINE.{extNote}");
        }
        else if (!wasOnline && online)
        {
            Log.Info("SlimeVrTrackers", $"Tracker '{tracker.Name}' ({tracker.Ip}) came back ONLINE.");
        }
    }

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
