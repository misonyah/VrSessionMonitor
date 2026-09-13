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

    /// <summary>Devices THIS service muted, so restoring never unmutes one the user silenced.</summary>
    private readonly Dictionary<string, string> _mutedByUs = new(StringComparer.OrdinalIgnoreCase);

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
    public void OnSessionEnded()
    {
        ApplyContext(ListeningContext.Away);
        RestoreMutedDevices(); // belt and braces: ApplyContext returns early when disabled
    }

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

            if (wanted is not null)
            {
                if (_controller.SetDefaultPlaybackDevice(wanted.Id))
                    Log.Info("Audio", $"{(context == ListeningContext.InHeadset ? "Headset on" : "Headset off")} — default playback set to {wanted.FriendlyName}.");
                else
                    Log.Warn("Audio", $"Could not switch playback to {wanted.FriendlyName}.");
            }

            // Volume and room-silencing apply to whichever device is now in use, so they run even
            // when the default was already correct and nothing was switched.
            var inUse = wanted ?? current;
            if (context == ListeningContext.InHeadset) ApplyHeadsetAudio(inUse, available);
            else RestoreMutedDevices();
        }
        catch (Exception ex)
        {
            // Audio switching is a convenience; it must never take anything else down.
            Log.Debug("Audio", $"Applying the audio context threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes the headset device clearly audible, and optionally silences the rest of the room.
    ///
    /// The volume change sets the LEVEL only. Mute is separate state that the user may have set
    /// deliberately, and raising the level of a muted device is silent and harmless, whereas
    /// unmuting one that was deliberately silenced is not.
    /// </summary>
    private void ApplyHeadsetAudio(AudioDevice? headset, IReadOnlyList<AudioDevice> available)
    {
        if (headset is not null && _config.Audio.SetVrDeviceToFullVolume)
        {
            var wanted = Math.Clamp(_config.Audio.VrDeviceVolumePercent, 0, 100) / 100f;
            var current = _controller.GetVolumeScalar(headset.Id);

            // Only write when it differs: a no-op write still raises a system volume-change
            // notification, which other software reacts to.
            if (current < 0 || Math.Abs(current - wanted) > 0.01f)
            {
                if (_controller.SetVolumeScalar(headset.Id, wanted))
                    Log.Info("Audio", $"{headset.FriendlyName} volume set to {wanted * 100:0}% (mute left as it was).");
                else
                    Log.Debug("Audio", $"Could not set the volume of {headset.FriendlyName}.");
            }
        }

        if (!_config.Audio.MuteOtherDevicesInVr) return;

        foreach (var device in available)
        {
            if (headset is not null && string.Equals(device.Id, headset.Id, StringComparison.OrdinalIgnoreCase)) continue;

            var wasMuted = _controller.GetMute(device.Id);
            if (wasMuted is null || wasMuted.Value) continue; // unreadable, or already silent

            if (!_controller.SetMute(device.Id, true)) continue;

            // Remember only what WE muted, so restoring never unmutes something the user silenced
            // themselves.
            lock (_lock) _mutedByUs[device.Id] = device.FriendlyName;
            Log.Info("Audio", $"Muted {device.FriendlyName} for the session so audio stays in the headset.");
        }
    }

    /// <summary>
    /// Unmutes only the devices this service muted.
    ///
    /// Runs on the way out of the headset and on shutdown. Leaving speakers silent with nothing
    /// left running to explain why is the same failure as leaving a process frozen, and is far
    /// more confusing than the problem it was solving.
    /// </summary>
    public void RestoreMutedDevices()
    {
        List<KeyValuePair<string, string>> toRestore;
        lock (_lock)
        {
            if (_mutedByUs.Count == 0) return;
            toRestore = _mutedByUs.ToList();
            _mutedByUs.Clear();
        }

        var restored = 0;
        var failed = new List<string>();
        foreach (var (id, name) in toRestore)
        {
            if (_controller.SetMute(id, false)) restored++;
            else failed.Add(name);
        }

        if (restored > 0) Log.Info("Audio", $"Unmuted {restored} device(s) now the headset is off.");
        if (failed.Count > 0)
            Log.Error("Audio", $"FAILED to unmute: {string.Join(", ", failed)}. These are still silent — unmute them in Windows' sound settings.");
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
        // Unmute before anything else: there is nothing left running that could do it afterwards.
        try { RestoreMutedDevices(); } catch { /* never block shutdown */ }
        lock (_lock) StopTimerLocked();
    }
}
