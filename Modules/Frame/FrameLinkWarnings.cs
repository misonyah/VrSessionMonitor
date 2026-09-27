namespace VrSessionMonitor.Modules.Frame;

/// <summary>Turns Frame link snapshots into in-headset warnings, each delivered once per occurrence
/// and re-armed when its condition clears. A warning only counts as fired once it was actually
/// delivered (the SteamVR notification can fail while SteamVR isn't up yet).</summary>
public sealed class FrameLinkWarnings
{
    private static readonly TimeSpan InternetGrace = TimeSpan.FromSeconds(60);
    private DateTime? _internetDownSince;
    private readonly HashSet<string> _fired = new();

    /// <summary>Returns the warnings delivered by this call. <paramref name="deliver"/> shows one
    /// message and reports success; null = always succeeds.</summary>
    public IReadOnlyList<string> Evaluate(FrameLinkStatus? link, string? keeperStatus, DateTime now, Func<string, bool>? deliver = null)
    {
        deliver ??= _ => true;
        var msgs = new List<string>();

        var internetDown = link is not null && !link.InternetConnected;
        if (!internetDown) _internetDownSince = null; else _internetDownSince ??= now;
        Check("internet", internetDown && now - _internetDownSince >= InternetGrace,
            "Frame internet radio is searching — it shares the stream chip and causes latency spikes.", msgs, deliver);

        Check("keeper", keeperStatus is not null && !HotspotKeeperClient.IsOk(keeperStatus),
            $"PC hotspot is not up on its intended Wi-Fi adapter: {keeperStatus}", msgs, deliver);

        var degraded = link is { StreamUp: true } && (link.Band != "6 GHz" || link.WidthMhz is < 160);
        Check("degraded", degraded, $"Frame stream link degraded: {link?.Band} {link?.WidthMhz} MHz", msgs, deliver);

        return msgs;
    }

    private void Check(string key, bool condition, string message, List<string> msgs, Func<string, bool> deliver)
    {
        if (!condition) { _fired.Remove(key); return; }
        if (_fired.Contains(key)) return;
        if (!deliver(message)) return;
        _fired.Add(key);
        msgs.Add(message);
    }
}
