using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Audio;

/// <summary>
/// Moves the default playback device to follow where the user's ears are: the headset while it is
/// on, their normal headphones once it comes off.
///
/// Driven by the AFK signal rather than by whether a VR session is running, because those are
/// different things — the session keeps running while the headset sits on the desk, and that is
/// exactly the moment audio should move. It uses its OWN hold rather than the Home Assistant one:
/// lights hold AFK for 30 seconds so a glance at the SteamVR dashboard does not change the room,
/// but 30 seconds of silence after lifting the headset would be infuriating. A short hold still
/// prevents flapping when the headset is briefly moved.
///
/// The decision lives in AudioSwitchPolicy, which is pure and tested. This class only holds the
/// timer and talks to COM, because setting a default device needs an undocumented interface that
/// cannot be exercised in a test.
/// </summary>
public sealed class SessionAudioService : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly IAudioDeviceController _controller;
    private readonly Func<DateTime> _clock;

    private readonly object _lock = new();
    private ListeningContext _appliedContext = ListeningContext.Away;
    private ListeningContext? _pendingContext;
    private DateTime _pendingSince;
    private System.Threading.Timer? _holdTimer;

    public SessionAudioService(MonitorConfig config, IAudioDeviceController controller, Func<DateTime>? clock = null)
    {
        _config = config;
        _controller = controller;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raw AFK signal. True means the user is away from the headset.</summary>
    public void OnAfkChanged(bool isAfk) =>
        RequestContext(isAfk ? ListeningContext.Away : ListeningContext.InHeadset);

    /// <summary>A session ending always means away, with no hold — the headset is off for certain
    /// and there is nothing to debounce.</summary>
    public void OnSessionEnded() => ApplyContext(ListeningContext.Away);

    private void RequestContext(ListeningContext context)
    {
        if (!_config.Audio.Enabled) return;

        lock (_lock)
        {
            if (context == _appliedContext)
            {
                // Cancel a pending change that has been reversed — the headset went back on before
                // the hold elapsed, so nothing should happen at all.
                _pendingContext = null;
                StopTimerLocked();
                return;
            }

            if (_pendingContext == context) return; // already waiting on this one

            _pendingContext = context;
            _pendingSince = _clock();

            var hold = Math.Max(0, _config.Audio.SwitchDelaySeconds);
            if (hold == 0)
            {
                _pendingContext = null;
                StopTimerLocked();
                ApplyContext(context);
                return;
            }

            _holdTimer ??= new System.Threading.Timer(_ => OnHoldElapsed(), null, 500, 500);
        }
    }

    private void OnHoldElapsed()
    {
        ListeningContext toApply;

        lock (_lock)
        {
            if (_pendingContext is not ListeningContext pending) { StopTimerLocked(); return; }
            if ((_clock() - _pendingSince).TotalSeconds < _config.Audio.SwitchDelaySeconds) return;

            toApply = pending;
            _pendingContext = null;
            StopTimerLocked();
        }

        ApplyContext(toApply);
    }

    /// <summary>Also called directly when a device arrives or leaves, since the right answer can
    /// change without the user moving at all — plugging in headphones, or Windows grabbing a device
    /// the user blocked.</summary>
    public void ApplyContext(ListeningContext context)
    {
        if (!_config.Audio.Enabled) return;

        lock (_lock) _appliedContext = context;

        try
        {
            var available = _controller.GetPlaybackDevices();
            var current = _controller.GetDefaultPlaybackDevice();

            var wanted = AudioSwitchPolicy.ChooseDefault(
                context, available,
                _config.Audio.VrDeviceId, _config.Audio.AwayDeviceId,
                _config.Audio.NeverDefaultDeviceIds, current);

            if (wanted is null) return; // already right, or nothing safe to switch to

            if (_controller.SetDefaultPlaybackDevice(wanted.Id))
                Log.Info("Audio", $"{(context == ListeningContext.InHeadset ? "Headset on" : "Headset off")} — default playback set to {wanted.FriendlyName}.");
            else
                Log.Warn("Audio", $"Could not switch playback to {wanted.FriendlyName}.");
        }
        catch (Exception ex)
        {
            // Audio switching is a convenience; it must never take anything else down.
            Log.Debug("Audio", $"Applying the audio context threw: {ex.Message}");
        }
    }

    /// <summary>Re-evaluates against the current context — for device arrival/removal.</summary>
    public void ReapplyCurrentContext()
    {
        ListeningContext context;
        lock (_lock) context = _appliedContext;
        ApplyContext(context);
    }

    private void StopTimerLocked()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
    }

    public void Dispose()
    {
        lock (_lock) StopTimerLocked();
    }
}
