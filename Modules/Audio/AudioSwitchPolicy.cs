namespace VrSessionMonitor.Modules.Audio;

/// <summary>A playback endpoint as Windows reports it.</summary>
public sealed record AudioDevice(string Id, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}

/// <summary>Where the user's ears are, which is what decides the output device.</summary>
public enum ListeningContext
{
    /// <summary>Headset on and in use — audio belongs in the headset.</summary>
    InHeadset,

    /// <summary>Headset off, or no session — audio belongs in the normal headphones.</summary>
    Away,
}

/// <summary>
/// Chooses which playback device should be the system default.
///
/// Pure, and separated from the COM layer, because the interesting behaviour is entirely in the
/// decision: what to do when the preferred device does not exist, when the user has blocked the
/// one Windows picked, and when nothing configured is present. Setting a default device needs
/// undocumented COM that cannot be exercised in a test, so none of that logic lives there.
///
/// The device that matters most is the one most likely to be missing: Virtual Desktop's audio
/// endpoint only exists while VD is streaming, so "the VR device" is absent whenever the headset
/// is not connected. Falling back rather than failing is the normal path here, not an edge case.
/// </summary>
public static class AudioSwitchPolicy
{
    /// <summary>
    /// The device that should be default, or null to leave the current one alone.
    ///
    /// Null is returned rather than a guess whenever the answer is unknown — switching audio to an
    /// arbitrary endpoint is worse than leaving it where the user last put it.
    /// </summary>
    public static AudioDevice? ChooseDefault(
        ListeningContext context,
        IReadOnlyList<AudioDevice> available,
        string? vrDeviceId,
        string? awayDeviceId,
        IReadOnlyCollection<string> neverDefaultIds,
        AudioDevice? current)
    {
        if (available.Count == 0) return null;

        var blocked = new HashSet<string>(neverDefaultIds, StringComparer.OrdinalIgnoreCase);

        var wantedId = context == ListeningContext.InHeadset ? vrDeviceId : awayDeviceId;
        var wanted = Find(available, wantedId);

        // The configured device is present and allowed — the ordinary case.
        if (wanted is not null && !blocked.Contains(wanted.Id))
            return SameAsCurrent(wanted, current) ? null : wanted;

        // The wanted device is missing (VD's endpoint disappears when not streaming) or blocked.
        // Fall back to the other configured device before considering anything else, since the user
        // named both and one of them is still a deliberate choice.
        var otherId = context == ListeningContext.InHeadset ? awayDeviceId : vrDeviceId;
        var other = Find(available, otherId);
        if (other is not null && !blocked.Contains(other.Id))
            return SameAsCurrent(other, current) ? null : other;

        // Nothing configured is usable. Only act if the CURRENT default is one the user blocked —
        // that is the "never use this as default" rule, and it has to be enforced even when we have
        // nowhere good to go. Otherwise leave well alone.
        if (current is not null && blocked.Contains(current.Id))
        {
            var firstAllowed = available.FirstOrDefault(d => !blocked.Contains(d.Id));
            return firstAllowed; // may be null if everything is blocked
        }

        return null;
    }

    /// <summary>Whether a device the user blocked has become the default — Windows re-picks one on
    /// its own when hardware arrives, so this is checked independently of any context change.</summary>
    public static bool CurrentDefaultIsBlocked(AudioDevice? current, IReadOnlyCollection<string> neverDefaultIds) =>
        current is not null && neverDefaultIds.Contains(current.Id, StringComparer.OrdinalIgnoreCase);

    private static AudioDevice? Find(IReadOnlyList<AudioDevice> available, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : available.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool SameAsCurrent(AudioDevice wanted, AudioDevice? current) =>
        current is not null && string.Equals(wanted.Id, current.Id, StringComparison.OrdinalIgnoreCase);
}
