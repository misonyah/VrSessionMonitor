namespace VrSessionMonitor.Modules;

/// <summary>
/// Per-camera failure ceiling for EyeTrackingMonitor's Stop+Start auto-restart — the safety valve
/// FaceTrackingAutoFixConfig and SteamVrStuckSessionConfig already had and this path was missing.
///
/// Confirmed live 2026-08-17/18: an eye camera whose firmware had failed (emitting an error code
/// as its frame instead of an image — a real hardware fault, not a stuck connection) stayed
/// reachable on the network but never streamed once across a 2h11m session. With no ceiling, the
/// UI automation fired 170 restart attempts and logged 171 errors, burying the genuine
/// face-tracking stall warnings in the same log. Clicking Baballonia's buttons cannot repair
/// hardware, so past some number of attempts the useful move is to stop and say so.
///
/// Pure decision logic with no clock or I/O of its own (callers pass nowUtc), matching this
/// codebase's other testable policy classes — see RepresentPolicy and TrackerIdleDetector.
/// </summary>
public sealed class EyeCameraRestartGiveUpPolicy
{
    private readonly Dictionary<string, int> _consecutiveAttempts = new();
    private readonly Dictionary<string, DateTime> _giveUpUntilUtc = new();

    /// <summary>How long this camera stays backed off, or null if restarts are allowed right now.
    /// An elapsed backoff clears itself here, so the next attempt starts a fresh ladder.</summary>
    public TimeSpan? GiveUpRemaining(string cameraName, DateTime nowUtc)
    {
        if (!_giveUpUntilUtc.TryGetValue(cameraName, out var until)) return null;

        var remaining = until - nowUtc;
        if (remaining > TimeSpan.Zero) return remaining;

        _giveUpUntilUtc.Remove(cameraName);
        return null;
    }

    /// <summary>Records a restart attempt that has not (yet) produced a streaming camera. Returns
    /// true if this attempt hit <paramref name="giveUpAfterAttempts"/> and tripped the backoff, so
    /// the caller can log that loudly exactly once rather than on every subsequent skip.
    /// A non-positive ceiling disables the valve entirely (retry forever — the pre-2026-08-18
    /// behavior), kept as an explicit escape hatch.</summary>
    public bool RecordAttempt(string cameraName, DateTime nowUtc, int giveUpAfterAttempts, TimeSpan giveUpCooldown)
    {
        if (giveUpAfterAttempts <= 0) return false;

        var attempts = _consecutiveAttempts.GetValueOrDefault(cameraName) + 1;
        if (attempts < giveUpAfterAttempts)
        {
            _consecutiveAttempts[cameraName] = attempts;
            return false;
        }

        // Ceiling reached: back off and start the next window from a clean count.
        _consecutiveAttempts.Remove(cameraName);
        _giveUpUntilUtc[cameraName] = nowUtc + giveUpCooldown;
        return true;
    }

    /// <summary>The camera is streaming again — the only real success signal, since a restart
    /// "succeeding" only means the UI clicks were delivered, not that frames followed.</summary>
    public void RecordSuccess(string cameraName) => Reset(cameraName);

    /// <summary>Drops all failure state for a camera. Called on a genuine offline-to-online
    /// transition: a power-cycle or reseat is new hardware evidence, and shouldn't stay blocked by
    /// a backoff earned by the previous, possibly different, fault.</summary>
    public void Reset(string cameraName)
    {
        _consecutiveAttempts.Remove(cameraName);
        _giveUpUntilUtc.Remove(cameraName);
    }
}
