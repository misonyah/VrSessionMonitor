using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Frame;

/// <summary>While a SteamLink headset with the LinkStatus part is active, polls its link status over
/// SSH every 30 s (one call in flight at most) plus the local hotspot-keeper log, keeps a status line
/// for the UI, and raises one-shot in-headset warnings. Never blocks anything else.</summary>
public sealed class FrameLinkMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly MonitorConfig _config;
    private readonly IHeadsetMonitor _headset;
    private readonly ISshRunner _ssh;
    private readonly IHotspotKeeper _keeper;
    private readonly Func<string, bool> _notify;
    private readonly Func<DateTime> _clock;
    private readonly Func<bool> _steamVrRunning;
    private FrameLinkWarnings _warnings = new();
    private int _inFlight;
    private CancellationTokenSource? _cts;

    public string StatusText { get; private set; } = "";
    public bool Healthy { get; private set; } = true;
    public FrameLinkStatus? Last { get; private set; }

    public FrameLinkMonitor(MonitorConfig config, IHeadsetMonitor headset, ISshRunner ssh, IHotspotKeeper keeper,
                            Func<string, bool> notify, Func<DateTime>? clock = null, Func<bool>? steamVrRunning = null)
    {
        _config = config; _headset = headset; _ssh = ssh; _keeper = keeper; _notify = notify;
        _clock = clock ?? (() => DateTime.UtcNow);
        _steamVrRunning = steamVrRunning ?? (() => true);
    }

    public void Start()
    {
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await PollOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Debug("FrameLink", $"poll threw: {ex.Message}"); }
                try { await Task.Delay(Interval, cts.Token).ConfigureAwait(false); } catch (TaskCanceledException) { }
            }
        });
    }

    public async Task PollOnceAsync()
    {
        var profile = HeadsetProfiles.Find(_config, _headset.ActiveHeadsetId);
        if (!_headset.IsOnline || profile is null || profile.SessionKind != HeadsetSessionKind.SteamLink
            || !profile.Part(HeadsetPartNames.LinkStatus) || string.IsNullOrWhiteSpace(profile.SshHost))
        {
            StatusText = ""; Healthy = true; Last = null;
            _warnings = new FrameLinkWarnings();   // next Frame session warns afresh
            return;
        }
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return;
        try
        {
            var keeper = string.IsNullOrWhiteSpace(profile.HotspotKeeperLog) ? null : _keeper.ReadStatus(profile.HotspotKeeperLog);
            var r = await _ssh.RunAsync(profile.SshHost, FrameLinkStatusParser.RemoteScript, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (r.TimedOut || (r.ExitCode != 0 && string.IsNullOrWhiteSpace(r.StdOut)))
            {
                var reason = r.TimedOut ? "timed out" : (r.StdErr.Trim().Split('\n').LastOrDefault() is { Length: > 0 } l ? l.Trim() : $"exit {r.ExitCode}");
                StatusText = $"SSH unavailable: {reason}" + (keeper is null ? "" : $" · hotspot: {keeper}");
                Healthy = false; Last = null;
                return;
            }

            var link = FrameLinkStatusParser.Parse(r.StdOut);
            Last = link;
            StatusText = link.Summary() + (keeper is null ? "" : $" · hotspot: {keeper}");
            Healthy = link.StreamUp && link.InternetConnected && (keeper is null || HotspotKeeperClient.IsOk(keeper));
            foreach (var w in _warnings.Evaluate(link, keeper, _clock(), Deliver))
                Log.Warn("FrameLink", w);
        }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    /// <summary>A warning waits (unattempted) until SteamVR runs; once SteamVR is up, one attempt
    /// counts as delivered even if the notifier reports failure. Retrying failures every poll made
    /// VrSessionMonitor connect/disconnect to SteamVR every ~30 s while the notifier kept failing
    /// with OpenVR error 102 (live 2026-09-27, around two SteamVR crashes).</summary>
    private bool Deliver(string message)
    {
        if (!_steamVrRunning()) return false;
        _notify(message);
        return true;
    }

    public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); }
}
