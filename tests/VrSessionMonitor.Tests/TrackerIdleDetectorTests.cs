using System;
using System.Collections.Generic;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class TrackerIdleDetectorTests
{
    private static TrackerRotation Still(int id) => new(id, 0, 0, 0, 1); // identity quaternion
    // A quaternion rotated `deg` about X from identity.
    private static TrackerRotation Rot(int id, double deg)
    {
        var half = deg * Math.PI / 180.0 / 2.0;
        return new(id, (float)Math.Sin(half), 0, 0, (float)Math.Cos(half));
    }

    [Fact]
    public void Empty_snapshot_is_idle_immediately()
    {
        var d = new TrackerIdleDetector(2.0, 120000);
        d.Observe(new List<TrackerRotation>(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(d.IsIdle);
    }

    [Fact]
    public void Still_trackers_become_idle_after_the_window()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var d = new TrackerIdleDetector(2.0, 120000);
        var snap = new List<TrackerRotation> { Still(0), Still(1) };
        d.Observe(snap, t);
        Assert.False(d.IsIdle);                       // window not elapsed yet
        d.Observe(snap, t.AddMilliseconds(119000));
        Assert.False(d.IsIdle);
        d.Observe(snap, t.AddMilliseconds(120001));
        Assert.True(d.IsIdle);                        // still for > window
    }

    [Fact]
    public void A_moving_tracker_resets_the_window()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var d = new TrackerIdleDetector(2.0, 120000);
        d.Observe(new List<TrackerRotation> { Still(0) }, t);
        d.Observe(new List<TrackerRotation> { Still(0) }, t.AddMilliseconds(120001));
        Assert.True(d.IsIdle);
        // Now it moves 10 degrees -> not idle, window restarts.
        d.Observe(new List<TrackerRotation> { Rot(0, 10) }, t.AddMilliseconds(121000));
        Assert.False(d.IsIdle);
        d.Observe(new List<TrackerRotation> { Rot(0, 10) }, t.AddMilliseconds(121000 + 120001));
        Assert.True(d.IsIdle);                        // still again at the new pose for the window
    }

    [Fact]
    public void One_moving_tracker_among_still_ones_is_not_idle()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var d = new TrackerIdleDetector(2.0, 120000);
        d.Observe(new List<TrackerRotation> { Still(0), Still(1) }, t);
        d.Observe(new List<TrackerRotation> { Still(0), Rot(1, 20) }, t.AddMilliseconds(120001));
        Assert.False(d.IsIdle);
    }

    [Fact]
    public void Tiny_noise_under_threshold_still_counts_as_idle()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var d = new TrackerIdleDetector(2.0, 120000);
        d.Observe(new List<TrackerRotation> { Rot(0, 0.0) }, t);
        d.Observe(new List<TrackerRotation> { Rot(0, 1.0) }, t.AddMilliseconds(60000));  // 1° < 2° threshold
        d.Observe(new List<TrackerRotation> { Rot(0, 0.5) }, t.AddMilliseconds(120001));
        Assert.True(d.IsIdle);
    }
}
