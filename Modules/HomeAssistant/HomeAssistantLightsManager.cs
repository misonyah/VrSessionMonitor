using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
/// <summary>
/// Orchestrates the three trigger events from docs/superpowers/specs/2026-07-26-home-assistant-lights-design.md.
/// Headset On/Off come directly from the existing HeadsetMonitor.StateChanged event — this class
/// adds no new headset-presence detection of its own. AFK (HMD proximity OR VRChat OSC) is wired
/// in by Task 5/6 via OnHmdPresenceChanged/OnOscAfkChanged.
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
        _ = e.IsOnline ? ApplyHeadsetOnActionsAsync() : ApplyActionsAsync(_config.HomeAssistant.HeadsetOffActions);
    }

    /// <summary>present=false means the HMD proximity sensor reports the headset off-face.</summary>
    public void OnHmdPresenceChanged(bool present) => SetAfkSource(isHmdSource: true, value: !present);

    /// <summary>afk=true means VRChat's own /avatar/parameters/AFK OSC parameter is set.</summary>
    public void OnOscAfkChanged(bool afk) => SetAfkSource(isHmdSource: false, value: afk);

    private void SetAfkSource(bool isHmdSource, bool value)
    {
        if (isHmdSource) _hmdAfk = value; else _oscAfk = value;

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
            var ok = await _client.CallServiceAsync("light", service, entityId).ConfigureAwait(false);
            Log.Info("HomeAssistant", $"{entityId} -> {service}: {(ok ? "ok" : "failed")}");
        }
    }

    public void Dispose()
    {
        _headset.StateChanged -= OnHeadsetStateChanged;
    }
}
#endif
