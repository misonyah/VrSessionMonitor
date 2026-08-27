using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>Current heart rate state, as far as this app can know it.</summary>
public sealed record HeartrateState(bool Connected, int Bpm, DateTime? LastUpdateUtc)
{
    public static readonly HeartrateState Unknown = new(false, 0, null);
}

/// <summary>
/// Tracks heart rate from VRCOSC's Bluetooth Heartrate module, over OSC.
///
/// OSC rather than Bluetooth, for a reason that is not a preference: the heart rate VALUE lives
/// behind GATT characteristic 0x2A37 and a BLE peripheral accepts exactly one connection, so
/// reading it directly would take the strap away from VRCOSC. Confirmed by dumping a real strap's
/// advertisement on 2026-08-28 — it carries only flags, its service UUIDs and its name, no
/// measurement. VRCOSC already holds the connection, so it is the only sensible source.
///
/// Parameter names are configurable because they are user-defined in VRCOSC's own module settings,
/// where they default to HR (connected), HRValue (BPM) and HRPercent (normalised). Hard-coding
/// them would break silently the moment someone renamed one.
///
/// BATTERY IS DELIBERATELY ABSENT. Every route to it is closed: the strap does not advertise it
/// (only that it offers service 180F), it is not paired in Windows so the OS cannot read it, and
/// VRCOSC's module does not publish it — its DLL contains no battery code at all. What remains is
/// a GATT read of characteristic 0x2A19, which needs the connection VRCOSC is holding.
/// </summary>
public sealed class HeartrateMonitor
{
    private readonly MonitorConfig _config;
    private readonly Func<DateTime> _clock;
    private int _bpm;
    private bool _connected;
    private DateTime? _lastUpdateUtc;
    private readonly object _lock = new();

    public HeartrateMonitor(MonitorConfig config, Func<DateTime>? clock = null)
    {
        _config = config;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public HeartrateState Current
    {
        get { lock (_lock) return new HeartrateState(_connected, _bpm, _lastUpdateUtc); }
    }

    /// <summary>The parameter names to subscribe to, skipping any left blank in config.</summary>
    public IEnumerable<string> ParameterNames
    {
        get
        {
            if (_config.Heartrate.ConnectedParameter is { Length: > 0 } c) yield return c;
            if (_config.Heartrate.BpmParameter is { Length: > 0 } b) yield return b;
        }
    }

    /// <summary>Parameter names come from VRCOSC's settings, where case is the user's choice.</summary>
    private static bool Matches(string name, string configured) =>
        configured is { Length: > 0 } && string.Equals(name, configured, StringComparison.OrdinalIgnoreCase);

    /// <summary>Handles one OSC parameter. Address is the full /avatar/parameters/... form.</summary>
    public void OnParameter(string address, object? value)
    {
        var name = address[(address.LastIndexOf('/') + 1)..];

        if (Matches(name, _config.Heartrate.ConnectedParameter))
        {
            var connected = value is true;
            lock (_lock)
            {
                if (_connected == connected) return;
                _connected = connected;
                _lastUpdateUtc = _clock();
            }

            Log.Info("Heartrate", connected
                ? "VRCOSC reports the heart rate monitor is connected."
                : "VRCOSC reports the heart rate monitor has disconnected.");
            return;
        }

        if (!Matches(name, _config.Heartrate.BpmParameter)) return;

        // VRChat parameters arrive as int or float depending on how the sender declared them, so
        // accept either rather than assuming — a float BPM silently ignored would look exactly
        // like a monitor that never reports.
        var bpm = value switch
        {
            int i => i,
            float f => (int)Math.Round(f),
            double d => (int)Math.Round(d),
            _ => -1,
        };
        if (bpm < 0) return;

        lock (_lock)
        {
            _bpm = bpm;
            _lastUpdateUtc = _clock();
        }
    }

    /// <summary>
    /// Whether the reading is recent enough to show. VRCOSC only sends on change, and a strap that
    /// dies mid-session simply stops sending — without a staleness check the last value would sit
    /// on screen indefinitely, which is worse than showing nothing because it looks live.
    /// </summary>
    public bool IsFresh
    {
        get
        {
            lock (_lock)
                return _lastUpdateUtc is DateTime t
                       && (_clock() - t) < TimeSpan.FromSeconds(Math.Max(5, _config.Heartrate.StaleAfterSeconds));
        }
    }

    /// <summary>One line for the status view and tray tooltip.</summary>
    public string Summarize()
    {
        var state = Current;

        if (state.LastUpdateUtc is null) return "no data from VRCOSC yet";
        if (!IsFresh)
            return state.Bpm > 0
                ? $"{state.Bpm} bpm (stale — nothing received for over {_config.Heartrate.StaleAfterSeconds}s)"
                : "stale — nothing received recently";
        if (!state.Connected) return "monitor disconnected";
        return state.Bpm > 0 ? $"{state.Bpm} bpm" : "connected, waiting for a reading";
    }
}
