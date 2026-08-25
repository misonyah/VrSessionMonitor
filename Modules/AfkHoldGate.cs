namespace VrSessionMonitor.Modules;

/// <summary>
/// Holds VRChat's OSC AFK signal down for a while before believing it.
///
/// VRChat raises /avatar/parameters/AFK whenever it loses focus, which includes opening the SteamVR
/// dashboard — so the raw signal fires on every trip to the Steam menu. This machine's logs bear
/// that out: all 85 recorded AFK triggers came from the OSC source while the HMD proximity sensor
/// still reported the headset as worn, and the median AFK period was 12 seconds.
///
/// Going AFK is delayed by the hold; coming BACK is never delayed. Returning to a lit room should
/// be instant, and a late clear would leave the lights wrong for exactly as long as the hold.
///
/// Deliberately sits outside the INCLUDE_HOME_ASSISTANT guard, like LightSetting: the whole point
/// of splitting this out is that it can be tested in the default build.
/// </summary>
public sealed class AfkHoldGate
{
    private readonly TimeSpan _hold;
    private readonly Func<DateTime> _clock;
    private DateTime? _pendingSince;

    public AfkHoldGate(TimeSpan hold, Func<DateTime>? clock = null)
    {
        _hold = hold;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Whether AFK is currently believed. Only flips to true once the hold has elapsed.</summary>
    public bool IsAfk { get; private set; }

    /// <summary>True while an AFK signal is being held down and has not yet been believed — the
    /// caller uses this to decide whether it still needs to Tick().</summary>
    public bool IsPending => _pendingSince is not null && !IsAfk;

    /// <summary>Feeds in VRChat's raw signal. Returns true when IsAfk actually changed, so the
    /// caller only reacts to real transitions.</summary>
    public bool Signal(bool afk)
    {
        if (!afk)
        {
            // Clearing is immediate and also cancels any hold in progress — this is the Steam-menu
            // case: the signal went up and came back down before the hold elapsed, so nothing
            // should ever have happened.
            _pendingSince = null;
            if (!IsAfk) return false;
            IsAfk = false;
            return true;
        }

        if (IsAfk) return false; // already believed; nothing to change

        if (_hold <= TimeSpan.Zero)
        {
            IsAfk = true; // hold disabled — behave exactly as before
            return true;
        }

        _pendingSince ??= _clock(); // start the hold; a repeat signal must not restart it
        return false;
    }

    /// <summary>Call while IsPending to let the hold elapse. Returns true when IsAfk changed.</summary>
    public bool Tick()
    {
        if (IsAfk || _pendingSince is not DateTime since) return false;
        if (_clock() - since < _hold) return false;

        _pendingSince = null;
        IsAfk = true;
        return true;
    }

    /// <summary>Drops all state — used when the headset goes offline and the next session should
    /// start from a clean slate rather than inheriting a stale hold.</summary>
    public void Reset()
    {
        _pendingSince = null;
        IsAfk = false;
    }
}
