using System.Collections.Generic;
using VrSessionMonitor.Modules.Audio;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// The interesting behaviour is entirely in what happens when the wanted device is NOT available.
/// That is the normal case here, not an edge one: Virtual Desktop's endpoint only exists while it
/// is streaming, so the VR device is absent whenever the headset is not connected. Switching audio
/// to an arbitrary endpoint is worse than leaving it alone, so "do nothing" has to be the default
/// answer whenever the right one is unknown.
/// </summary>
public class AudioSwitchPolicyTests
{
    private static readonly AudioDevice Vd = new("{vd}", "Speakers (Virtual Desktop Audio)");
    private static readonly AudioDevice Sennheiser = new("{senn}", "Headphones (Sennheiser Profile)");
    private static readonly AudioDevice Tv = new("{tv}", "LG TV SSCR2 (NVIDIA High Definition Audio)");

    private static List<AudioDevice> All(params AudioDevice[] d) => new(d);
    private static readonly string[] NoBlocks = System.Array.Empty<string>();

    [Fact]
    public void In_headset_selects_the_vr_device()
    {
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.InHeadset, All(Vd, Sennheiser), Vd.Id, Sennheiser.Id, NoBlocks, current: Sennheiser);

        Assert.Equal(Vd.Id, chosen!.Id);
    }

    [Fact]
    public void Away_selects_the_normal_headphones()
    {
        // The whole point of the feature: take the headset off, hear audio in your headphones.
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Vd, Sennheiser), Vd.Id, Sennheiser.Id, NoBlocks, current: Vd);

        Assert.Equal(Sennheiser.Id, chosen!.Id);
    }

    [Fact]
    public void Nothing_happens_when_the_right_device_is_already_default()
    {
        // Returning it anyway would mean setting the default device on every single poll.
        Assert.Null(AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Vd, Sennheiser), Vd.Id, Sennheiser.Id, NoBlocks, current: Sennheiser));
    }

    [Fact]
    public void A_missing_vr_device_falls_back_to_the_away_device()
    {
        // Virtual Desktop's endpoint disappears when it is not streaming, so this is routine.
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.InHeadset, All(Sennheiser, Tv), Vd.Id, Sennheiser.Id, NoBlocks, current: Tv);

        Assert.Equal(Sennheiser.Id, chosen!.Id);
    }

    [Fact]
    public void A_blocked_device_is_never_chosen_even_when_configured()
    {
        // If the user blocked it, blocking wins over their own device selection — the block is the
        // more specific statement of intent.
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Vd, Sennheiser), Vd.Id, Sennheiser.Id,
            new[] { Sennheiser.Id }, current: Vd);

        Assert.NotEqual(Sennheiser.Id, chosen?.Id);
    }

    [Fact]
    public void A_blocked_device_that_windows_made_default_is_moved_off()
    {
        // Windows re-picks a default on its own whenever hardware arrives. "Never use this as
        // default" has to be enforced against that, not only against our own choices.
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Tv, Sennheiser), vrDeviceId: null, awayDeviceId: null,
            neverDefaultIds: new[] { Tv.Id }, current: Tv);

        Assert.NotNull(chosen);
        Assert.NotEqual(Tv.Id, chosen!.Id);
    }

    [Fact]
    public void An_unconfigured_allowed_default_is_left_alone()
    {
        // With nothing configured and nothing blocked, the user's own choice stands. Overriding it
        // would make the feature actively hostile.
        Assert.Null(AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Tv, Sennheiser), vrDeviceId: null, awayDeviceId: null,
            neverDefaultIds: NoBlocks, current: Tv));
    }

    [Fact]
    public void Nothing_is_chosen_when_no_devices_exist()
    {
        Assert.Null(AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(), Vd.Id, Sennheiser.Id, NoBlocks, current: null));
    }

    [Fact]
    public void Everything_blocked_yields_nothing_rather_than_a_bad_choice()
    {
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Tv), null, null, new[] { Tv.Id }, current: Tv);

        Assert.Null(chosen);
    }

    [Fact]
    public void A_configured_device_that_no_longer_exists_is_ignored()
    {
        // Endpoint ids change when hardware is replaced; a stale id must not stop the other rule
        // from working.
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Sennheiser), "{a-device-that-was-removed}", Sennheiser.Id,
            NoBlocks, current: null);

        Assert.Equal(Sennheiser.Id, chosen!.Id);
    }

    [Fact]
    public void Device_ids_match_case_insensitively()
    {
        var chosen = AudioSwitchPolicy.ChooseDefault(
            ListeningContext.Away, All(Sennheiser), null, Sennheiser.Id.ToUpperInvariant(),
            NoBlocks, current: null);

        Assert.Equal(Sennheiser.Id, chosen!.Id);
    }

    [Fact]
    public void Blocked_detection_reports_a_blocked_current_default()
    {
        Assert.True(AudioSwitchPolicy.CurrentDefaultIsBlocked(Tv, new[] { Tv.Id }));
        Assert.False(AudioSwitchPolicy.CurrentDefaultIsBlocked(Sennheiser, new[] { Tv.Id }));
        Assert.False(AudioSwitchPolicy.CurrentDefaultIsBlocked(null, new[] { Tv.Id }));
    }
}
