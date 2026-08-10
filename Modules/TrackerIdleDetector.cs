using System;
using System.Collections.Generic;
using System.Linq;

namespace VrSessionMonitor.Modules;

/// <summary>A single tracker's orientation as a quaternion (x,y,z,w). Kept independent of the
/// SolarXR Quat type so the detector is pure and trivially testable.</summary>
public readonly record struct TrackerRotation(int TrackerId, float X, float Y, float Z, float W);

/// <summary>
/// Decides whether the SlimeVR trackers are "idle" (motionless) from a stream of rotation
/// snapshots. Idle = every tracker has stayed within idleThresholdDeg of where it was when the
/// still period began, continuously for idleWindowMs. An empty snapshot (SlimeVR not running, or no
/// trackers reporting) is idle immediately - nothing is in motion.
///
/// Pure: no I/O, no injected clock. Observe(rotations, nowUtc) records nowUtc as the "last
/// observed" time, and IsIdle evaluates the window against that most-recent Observe's nowUtc.
/// </summary>
public sealed class TrackerIdleDetector
{
    private readonly double _thresholdDeg;
    private readonly int _windowMs;

    private Dictionary<int, TrackerRotation> _baseline = new();
    private DateTime? _stillSinceUtc;
    private DateTime _lastObserved;
    private bool _emptyIdle;

    public TrackerIdleDetector(double idleThresholdDeg, int idleWindowMs)
    {
        _thresholdDeg = idleThresholdDeg;
        _windowMs = idleWindowMs;
    }

    public void Observe(IReadOnlyList<TrackerRotation> rotations, DateTime nowUtc)
    {
        _lastObserved = nowUtc;

        if (rotations.Count == 0)
        {
            _emptyIdle = true;
            _baseline = new();
            _stillSinceUtc = null;
            return;
        }
        _emptyIdle = false;

        var current = rotations.ToDictionary(r => r.TrackerId, r => r);

        var movedOrNew = _stillSinceUtc is null
            || current.Count != _baseline.Count
            || current.Any(kv => !_baseline.TryGetValue(kv.Key, out var b)
                                 || AngleDeg(kv.Value, b) > _thresholdDeg);

        if (movedOrNew)
        {
            _baseline = current;      // new resting pose (or first observation)
            _stillSinceUtc = nowUtc;  // window restarts
        }
    }

    /// <summary>True once nothing has moved beyond the threshold for the whole window (or when no
    /// trackers are reporting at all), evaluated against the last Observe's nowUtc.</summary>
    public bool IsIdle =>
        _emptyIdle ||
        (_stillSinceUtc is DateTime since && (_lastObserved - since).TotalMilliseconds >= _windowMs);

    /// <summary>Angle between two unit quaternions in degrees, double-cover-safe (|dot|).</summary>
    private static double AngleDeg(TrackerRotation a, TrackerRotation b)
    {
        double dot = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        if (dot > 1.0) dot = 1.0;
        return 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
    }
}
