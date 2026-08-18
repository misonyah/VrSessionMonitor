using System;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class EyeCameraRestartGiveUpPolicyTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private const int Ceiling = 3;

    [Fact]
    public void Not_backed_off_before_any_attempts()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();
        Assert.Null(policy.GiveUpRemaining("left_eye", T0));
    }

    [Fact]
    public void Attempts_below_the_ceiling_do_not_trip_it()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();

        Assert.False(policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown));
        Assert.False(policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown));
        Assert.Null(policy.GiveUpRemaining("left_eye", T0));
    }

    [Fact]
    public void Reaching_the_ceiling_trips_the_backoff()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();

        policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);
        policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);
        Assert.True(policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown)); // 3rd trips it

        Assert.Equal(Cooldown, policy.GiveUpRemaining("left_eye", T0));
    }

    [Fact]
    public void Backoff_expires_after_the_cooldown()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();
        for (var i = 0; i < Ceiling; i++) policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);

        Assert.NotNull(policy.GiveUpRemaining("left_eye", T0 + TimeSpan.FromMinutes(9)));
        Assert.Null(policy.GiveUpRemaining("left_eye", T0 + TimeSpan.FromMinutes(10)));
    }

    /// <summary>The camera streaming again is the real success signal — it must clear the failure
    /// count so a later, unrelated failure gets the full ladder again rather than tripping early.</summary>
    [Fact]
    public void Success_resets_the_failure_count()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();
        policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);
        policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);

        policy.RecordSuccess("left_eye");

        Assert.False(policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown)); // counting restarted
        Assert.Null(policy.GiveUpRemaining("left_eye", T0));
    }

    [Fact]
    public void Success_clears_an_active_backoff()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();
        for (var i = 0; i < Ceiling; i++) policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);
        Assert.NotNull(policy.GiveUpRemaining("left_eye", T0));

        policy.RecordSuccess("left_eye");

        Assert.Null(policy.GiveUpRemaining("left_eye", T0));
    }

    /// <summary>A genuine offline->online transition is new hardware evidence (someone power-cycled
    /// or reseated the camera), so it clears the backoff even mid-cooldown — same reasoning the
    /// existing bypassCooldown path already uses.</summary>
    [Fact]
    public void Reset_clears_an_active_backoff()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();
        for (var i = 0; i < Ceiling; i++) policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);

        policy.Reset("left_eye");

        Assert.Null(policy.GiveUpRemaining("left_eye", T0));
    }

    [Fact]
    public void Cameras_are_tracked_independently()
    {
        var policy = new EyeCameraRestartGiveUpPolicy();
        for (var i = 0; i < Ceiling; i++) policy.RecordAttempt("left_eye", T0, Ceiling, Cooldown);

        Assert.NotNull(policy.GiveUpRemaining("left_eye", T0));
        Assert.Null(policy.GiveUpRemaining("right_eye", T0));
    }

    /// <summary>0 keeps the pre-2026-08-18 behavior (retry forever) as an explicit escape hatch.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_ceiling_never_trips(int ceiling)
    {
        var policy = new EyeCameraRestartGiveUpPolicy();

        for (var i = 0; i < 50; i++)
            Assert.False(policy.RecordAttempt("left_eye", T0, ceiling, Cooldown));

        Assert.Null(policy.GiveUpRemaining("left_eye", T0));
    }
}
