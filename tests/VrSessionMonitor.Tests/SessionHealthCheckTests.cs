using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Both thresholds come from a real diagnosis on 2026-09-02: memory exhaustion produced VRChat
/// stutter while CPU and GPU sat idle, and wide-open avatar limits held a 17-player world at 20 FPS
/// with the GPU at 64%. These pin the conditions so the warning fires for those cases and stays
/// quiet otherwise — a warning that cries wolf gets ignored exactly when it matters.
/// </summary>
public class SessionHealthCheckTests
{
    [Fact]
    public void A_healthy_machine_produces_no_memory_warning()
    {
        Assert.Null(SessionHealthCheck.CheckMemory(freeGb: 22, pagefileGb: 3.5));
    }

    [Fact]
    public void Low_free_memory_warns()
    {
        // The state measured while VRChat was stuttering.
        var w = SessionHealthCheck.CheckMemory(freeGb: 3.2, pagefileGb: 29.5);

        Assert.NotNull(w);
        Assert.Contains("3.2", w!.Summary);
        Assert.Contains("29.5", w.Summary);
    }

    [Fact]
    public void Heavy_pagefile_use_warns_even_with_free_memory()
    {
        // Memory freed up but a lot is still parked on disk — worth saying, since the pressure
        // that put it there may return.
        var w = SessionHealthCheck.CheckMemory(freeGb: 12, pagefileGb: 15.2);

        Assert.NotNull(w);
        Assert.Contains("pagefile", w!.Summary);
    }

    [Fact]
    public void Wide_open_avatar_limits_warn()
    {
        // Exactly the settings found on this account.
        var w = SessionHealthCheck.CheckAvatarLimits(minimumRatingToDisplay: 5, maxAvatars: 31);

        Assert.NotNull(w);
        Assert.Contains("31", w!.Summary);
        Assert.Contains("main thread", w.Detail);
    }

    [Fact]
    public void Sensible_avatar_limits_stay_quiet()
    {
        Assert.Null(SessionHealthCheck.CheckAvatarLimits(minimumRatingToDisplay: 2, maxAvatars: 12));
    }

    [Fact]
    public void Unreadable_settings_produce_no_warning()
    {
        // VRChat may never have run, so the registry keys may not exist. Absence is not a problem
        // to report.
        Assert.Null(SessionHealthCheck.CheckAvatarLimits(null, null));
    }

    [Fact]
    public void Either_avatar_condition_alone_is_enough_to_warn()
    {
        Assert.NotNull(SessionHealthCheck.CheckAvatarLimits(5, 10));   // shows everything
        Assert.NotNull(SessionHealthCheck.CheckAvatarLimits(2, 31));   // too many at once
    }

    [Fact]
    public void Pages_we_froze_ourselves_are_not_reported_as_paging()
    {
        // The exact shape of the complaint: freezing pushes those apps' RAM to the pagefile, so the
        // figure climbs precisely because the fix worked - while a frozen process never faults its
        // pages back in, so they cost the session nothing.
        Assert.Null(SessionHealthCheck.CheckMemory(freeGb: 22, pagefileGb: 18, frozenGb: 12));
    }

    [Fact]
    public void Paging_by_apps_still_running_is_still_reported()
    {
        // Only part of the pagefile is ours; the rest is live paging and still worth saying.
        var w = SessionHealthCheck.CheckMemory(freeGb: 22, pagefileGb: 30, frozenGb: 12);
        Assert.NotNull(w);
        Assert.Contains("18", w!.Summary);
        Assert.Contains("still running", w.Summary);
    }

    [Fact]
    public void A_frozen_figure_never_drives_the_number_below_zero()
    {
        Assert.Null(SessionHealthCheck.CheckMemory(freeGb: 22, pagefileGb: 4, frozenGb: 40));
    }

    [Fact]
    public void Low_free_memory_still_warns_regardless_of_the_discount()
    {
        // Discounting is only about the pagefile half; genuinely tight RAM is unaffected.
        var w = SessionHealthCheck.CheckMemory(freeGb: 3.2, pagefileGb: 20, frozenGb: 20);
        Assert.NotNull(w);
        Assert.Contains("3.2 GB of RAM free", w!.Summary);
    }
}
