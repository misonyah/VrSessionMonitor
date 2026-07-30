using System.Text.Json;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Tracks per-parameter change history for a set of OSCQuery-sourced values and reports whether a
/// majority of them have gone stale — NOT whether the whole bundle is bit-identical to the last
/// check. Confirmed live 2026-07-30 why that distinction matters: JawOpen/MouthClosed/LipSuckLower
/// sat frozen for 15s+ (the actual visible "mouth isn't moving" symptom) while JawX/MouthX kept
/// jittering with tiny sensor noise the whole time. A whole-bundle-equality check ("are ALL of
/// these identical to last time?") would read that as "still changing, nothing to see here" and
/// never catch it. Per-parameter tracking with a majority vote catches a real partial freeze
/// without false-triggering on a single channel that's just legitimately never used (e.g.
/// TongueOut sitting at 0 all session because the parameter never sticks their tongue out).
/// </summary>
public sealed class OscFreshnessTracker
{
    private readonly Dictionary<string, (JsonElement Value, DateTime SinceUtc)> _state = new();

    /// <summary>Feeds in a fresh snapshot and returns true if at least half (minimum 2) of the
    /// currently-present parameters have each individually stayed bit-identical for at least
    /// <paramref name="staleThreshold"/>.</summary>
    public bool Update(Dictionary<string, JsonElement> current, DateTime now, TimeSpan staleThreshold)
    {
        var frozenCount = 0;

        foreach (var (key, value) in current)
        {
            var rawText = value.GetRawText();
            if (_state.TryGetValue(key, out var prev) && prev.Value.GetRawText() == rawText)
            {
                if (now - prev.SinceUtc >= staleThreshold)
                    frozenCount++;
                // else: still within the grace period for this particular streak — leave SinceUtc as-is.
            }
            else
            {
                _state[key] = (value, now);
            }
        }

        // Drop tracked keys that vanished from this snapshot (avatar swapped, parameter renamed).
        foreach (var staleKey in _state.Keys.Where(k => !current.ContainsKey(k)).ToList())
            _state.Remove(staleKey);

        if (current.Count == 0) return false;
        var requiredFrozen = Math.Max(2, (current.Count + 1) / 2);
        return frozenCount >= requiredFrozen;
    }

    public void Reset() => _state.Clear();
}
