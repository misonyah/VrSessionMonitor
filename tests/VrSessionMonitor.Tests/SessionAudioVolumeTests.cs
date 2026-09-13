using System;
using System.Collections.Generic;
using System.Linq;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules.Audio;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Volume and mute are separate pieces of state, and conflating them is the bug these guard
/// against: a device muted on purpose must stay muted when its level is raised. Raising the level
/// of a muted device is silent and harmless; unmuting one that was deliberately silenced is not.
///
/// The muting of other devices has the same shape as freezing processes — it changes something the
/// user can see, so only what this service muted is ever restored, and it is always restored.
/// </summary>
public class SessionAudioVolumeTests
{
    private static readonly AudioDevice Vd = new("{vd}", "Speakers (Virtual Desktop Audio)");
    private static readonly AudioDevice Sennheiser = new("{senn}", "Headphones (Sennheiser Profile)");
    private static readonly AudioDevice Tv = new("{tv}", "LG TV");

    private sealed class FakeController : IAudioDeviceController
    {
        public List<AudioDevice> Devices = new() { Vd, Sennheiser, Tv };
        public AudioDevice? Default = Sennheiser;
        public readonly Dictionary<string, float> Volume = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> Muted = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> MuteWrites = new();

        public IReadOnlyList<AudioDevice> GetPlaybackDevices() => Devices;
        public AudioDevice? GetDefaultPlaybackDevice() => Default;

        public bool SetDefaultPlaybackDevice(string deviceId)
        {
            Default = Devices.FirstOrDefault(d => d.Id == deviceId);
            return Default is not null;
        }

        public float GetVolumeScalar(string deviceId) => Volume.TryGetValue(deviceId, out var v) ? v : 0.5f;
        public bool SetVolumeScalar(string deviceId, float scalar) { Volume[deviceId] = scalar; return true; }
        public bool? GetMute(string deviceId) => Muted.TryGetValue(deviceId, out var m) ? m : false;

        public bool SetMute(string deviceId, bool mute)
        {
            Muted[deviceId] = mute;
            MuteWrites.Add($"{deviceId}={mute}");
            return true;
        }
    }

    private static MonitorConfig Config(bool muteOthers = false)
    {
        var config = new MonitorConfig();
        config.Audio.Enabled = true;
        config.Audio.VrDeviceId = Vd.Id;
        config.Audio.AwayDeviceId = Sennheiser.Id;
        config.Audio.SwitchDelaySeconds = 0;
        config.Audio.SetVrDeviceToFullVolume = true;
        config.Audio.MuteOtherDevicesInVr = muteOthers;
        return config;
    }

    [Fact]
    public void Entering_the_headset_raises_its_volume_to_full()
    {
        var fake = new FakeController();
        fake.Volume[Vd.Id] = 0.30f;
        var svc = new SessionAudioService(Config(), fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.Equal(1.0f, fake.Volume[Vd.Id], 2);
    }

    [Fact]
    public void Raising_the_volume_never_touches_mute()
    {
        // The explicit requirement: full volume WITHOUT unmuting.
        var fake = new FakeController();
        fake.Muted[Vd.Id] = true;
        fake.Volume[Vd.Id] = 0.20f;
        var svc = new SessionAudioService(Config(), fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.Equal(1.0f, fake.Volume[Vd.Id], 2);
        Assert.True(fake.Muted[Vd.Id]);              // still muted
        Assert.DoesNotContain(fake.MuteWrites, w => w.StartsWith(Vd.Id)); // never even written
    }

    [Fact]
    public void A_volume_already_at_the_target_is_not_rewritten()
    {
        // A no-op write still raises a system volume-change notification that other software reacts
        // to, so identical values must not be written.
        var fake = new FakeController();
        fake.Volume[Vd.Id] = 1.0f;
        var svc = new SessionAudioService(Config(), fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.Equal(1.0f, fake.Volume[Vd.Id], 2);
    }

    [Fact]
    public void A_custom_volume_percentage_is_honoured()
    {
        var fake = new FakeController();
        var config = Config();
        config.Audio.VrDeviceVolumePercent = 80;
        var svc = new SessionAudioService(config, fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.Equal(0.80f, fake.Volume[Vd.Id], 2);
    }

    [Fact]
    public void Volume_is_left_alone_when_the_setting_is_off()
    {
        var fake = new FakeController();
        fake.Volume[Vd.Id] = 0.30f;
        var config = Config();
        config.Audio.SetVrDeviceToFullVolume = false;
        var svc = new SessionAudioService(config, fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.Equal(0.30f, fake.Volume[Vd.Id], 2);
    }

    [Fact]
    public void Other_devices_are_muted_only_when_asked()
    {
        var fake = new FakeController();
        var svc = new SessionAudioService(Config(muteOthers: false), fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.Empty(fake.MuteWrites);
    }

    [Fact]
    public void Other_devices_are_muted_but_the_headset_is_not()
    {
        var fake = new FakeController();
        var svc = new SessionAudioService(Config(muteOthers: true), fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.True(fake.Muted[Sennheiser.Id]);
        Assert.True(fake.Muted[Tv.Id]);
        Assert.False(fake.Muted.TryGetValue(Vd.Id, out var vd) && vd); // never the one in use
    }

    [Fact]
    public void Leaving_the_headset_unmutes_what_we_muted()
    {
        var fake = new FakeController();
        var svc = new SessionAudioService(Config(muteOthers: true), fake);
        svc.ApplyContext(ListeningContext.InHeadset);

        svc.ApplyContext(ListeningContext.Away);

        Assert.False(fake.Muted[Tv.Id]);
    }

    [Fact]
    public void A_device_the_user_had_already_muted_is_left_muted()
    {
        // We never muted it, so unmuting it would be overriding a choice the user made.
        var fake = new FakeController();
        fake.Muted[Tv.Id] = true;
        var svc = new SessionAudioService(Config(muteOthers: true), fake);

        svc.ApplyContext(ListeningContext.InHeadset);
        svc.ApplyContext(ListeningContext.Away);

        Assert.True(fake.Muted[Tv.Id]);
    }

    [Fact]
    public void Shutdown_unmutes_anything_still_muted()
    {
        // Leaving speakers silent with nothing running to explain why is worse than the problem it
        // was solving.
        var fake = new FakeController();
        var svc = new SessionAudioService(Config(muteOthers: true), fake);
        svc.ApplyContext(ListeningContext.InHeadset);

        svc.Dispose();

        Assert.False(fake.Muted[Sennheiser.Id]);
        Assert.False(fake.Muted[Tv.Id]);
    }

    [Fact]
    public void Nothing_happens_at_all_when_audio_control_is_disabled()
    {
        var fake = new FakeController();
        fake.Volume[Vd.Id] = 0.30f;
        var config = Config(muteOthers: true);
        config.Audio.Enabled = false;
        var svc = new SessionAudioService(config, fake);

        svc.ApplyContext(ListeningContext.InHeadset);

        Assert.Equal(0.30f, fake.Volume[Vd.Id], 2);
        Assert.Empty(fake.MuteWrites);
    }
}
