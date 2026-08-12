using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
/// <summary>
/// Orchestrates the three trigger events from docs/superpowers/specs/2026-07-26-home-assistant-lights-design.md.
/// Headset On/Off come directly from the existing HeadsetMonitor.StateChanged event — this class
/// adds no new headset-presence detection of its own. AFK has two independent sources, both wired
/// up: HmdActivityMonitor's SteamVR proximity signal (via OnHmdPresenceChanged) and VRChat's own
/// /avatar/parameters/AFK OSC parameter (via OnOscAfkChanged). SetAfkSource ORs them together —
/// either source alone counts as AFK, and the lights only return to the Headset-On map once both
/// have cleared. AFK is ignored entirely while the headset is offline (see SetAfkSource).
/// </summary>
public sealed class HomeAssistantLightsManager : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly HomeAssistantClient _client;
    private readonly HeadsetMonitor _headset;
    private bool _hmdAfk;
    private bool _oscAfk;
    private bool _effectiveAfk;

    public HomeAssistantLightsManager(MonitorConfig config, HomeAssistantClient client, HeadsetMonitor headset)
    {
        _config = config;
        _client = client;
        _headset = headset;
    }

    public void Start()
    {
        _headset.StateChanged += OnHeadsetStateChanged;
        Log.Info("HomeAssistant", "Lights manager started.");
    }

    private void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        // A session boundary clears any AFK state, so the next session starts from "not AFK"
        // rather than inheriting whatever the lights were doing when the headset dropped.
        if (!e.IsOnline) _effectiveAfk = false;

        _ = e.IsOnline ? ApplyHeadsetOnActionsAsync() : ApplyActionsAsync(_config.HomeAssistant.HeadsetOffActions);
    }

    /// <summary>present=false means the HMD proximity sensor reports the headset off-face.</summary>
    public void OnHmdPresenceChanged(bool present) => SetAfkSource(isHmdSource: true, value: !present);

    /// <summary>afk=true means VRChat's own /avatar/parameters/AFK OSC parameter is set.</summary>
    public void OnOscAfkChanged(bool afk) => SetAfkSource(isHmdSource: false, value: afk);

    private void SetAfkSource(bool isHmdSource, bool value)
    {
        if (isHmdSource) _hmdAfk = value; else _oscAfk = value;

        // AFK means nothing once the headset is off: SteamVR lingers for 10-20s after the headset
        // drops off the network, so its HMD activity level falls to Idle and — a few polls later —
        // the AFK map would land on top of the Headset-Off map that just ran, leaving the lights
        // in the wrong state after the session has actually ended. Sources are still tracked above
        // so they're accurate when the next session starts.
        if (!_headset.IsOnline) return;

        var afk = _hmdAfk || _oscAfk;
        if (afk == _effectiveAfk) return;

        _effectiveAfk = afk;
        Log.Info("HomeAssistant", $"AFK state -> {afk} (hmd={_hmdAfk}, osc={_oscAfk})");
        _ = afk ? ApplyActionsAsync(_config.HomeAssistant.AfkActions) : ApplyHeadsetOnActionsAsync();
    }

    public Task ApplyHeadsetOnActionsAsync() => ApplyActionsAsync(_config.HomeAssistant.HeadsetOnActions);

    private async Task ApplyActionsAsync(Dictionary<string, string> actions)
    {
        if (!_config.HomeAssistant.Enabled) return;

        foreach (var (entityId, actionText) in actions)
        {
            var action = actionText.ParseOrDefault();
            if (action == LightAction.NoChange) continue;

            var service = action == LightAction.On ? "turn_on" : "turn_off";
            // Force the command through even when HA already reports the light 'on': light.turn_on
            // with no attributes gets optimized away by HA, so a Zigbee-desynced bulb that's
            // physically off (while HA's cached state says on) never relights. Sending brightness
            // guarantees a real device command; brightness_pct=100 also puts the session "on" scene
            // at full. (A light with no brightness support simply ignores it.)
            object? serviceData = action == LightAction.On ? new { brightness_pct = 100 } : null;
            var ok = await _client.CallServiceAsync("light", service, entityId, serviceData).ConfigureAwait(false);
            Log.Info("HomeAssistant", $"{entityId} -> {service}{(serviceData is null ? "" : " @100%")}: {(ok ? "ok" : "failed")}");
        }
    }

    public void Dispose()
    {
        _headset.StateChanged -= OnHeadsetStateChanged;
    }
}
#endif
