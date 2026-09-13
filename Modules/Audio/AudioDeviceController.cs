using System.Runtime.InteropServices;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Audio;

/// <summary>Enumerates playback endpoints, sets the system default, and reads/writes their volume.
/// An interface so the policy can be tested without touching real audio hardware.</summary>
public interface IAudioDeviceController
{
    IReadOnlyList<AudioDevice> GetPlaybackDevices();
    AudioDevice? GetDefaultPlaybackDevice();

    /// <summary>Returns false when the change was refused; never throws.</summary>
    bool SetDefaultPlaybackDevice(string deviceId);

    /// <summary>Master volume as 0.0-1.0, or -1 when it could not be read.</summary>
    float GetVolumeScalar(string deviceId);

    /// <summary>
    /// Sets master volume as 0.0-1.0. Deliberately does NOT touch the mute flag: volume and mute
    /// are separate pieces of state, and a device muted on purpose must stay muted when its level
    /// is raised. Raising the level of a muted device is silent and harmless; unmuting one that
    /// was deliberately silenced is not.
    /// </summary>
    bool SetVolumeScalar(string deviceId, float scalar);

    /// <summary>Mute flag, or null when it could not be read.</summary>
    bool? GetMute(string deviceId);

    bool SetMute(string deviceId, bool mute);
}

/// <summary>
/// Windows audio endpoints via COM.
///
/// Enumeration uses the documented IMMDeviceEnumerator. SETTING the default does not: Windows has
/// never exposed a supported API for it, so every tool in this space — nircmd, SoundVolumeView,
/// AudioDeviceCmdlets — uses the same undocumented IPolicyConfig interface, and so does this.
///
/// That interface is not contractual. Its vtable order is fixed only by convention, and a future
/// Windows release could reorder or remove it. Everything here therefore fails soft: a refused or
/// missing interface is logged and reported as false, never thrown, so audio switching degrades to
/// doing nothing rather than taking the app down. Both roles are set (Console and Multimedia)
/// because applications choose between them, and setting only one leaves audio split across two
/// devices.
/// </summary>
public sealed class AudioDeviceController : IAudioDeviceController
{
    private const int DEVICE_STATE_ACTIVE = 0x1;
    private const int STGM_READ = 0x0;
    private const int eRender = 0;
    private const int eConsole = 0;
    private const int eMultimedia = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public short VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    /// <summary>PKEY_Device_FriendlyName — the name Windows' own sound settings shows.</summary>
    private static PropertyKey FriendlyNameKey => new()
    {
        FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        PropertyId = 14,
    };

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigComObject { }

    /// <summary>UNDOCUMENTED. Only SetDefaultEndpoint is ever called; the members before it exist
    /// purely to occupy their vtable slots in the right order, which is what makes the call land on
    /// the right function.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat();
        int GetDeviceFormat();
        int ResetDeviceFormat();
        int SetDeviceFormat();
        int GetProcessingPeriod();
        int SetProcessingPeriod();
        int GetShareMode();
        int SetShareMode();
        int GetPropertyValue();
        int SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        int SetEndpointVisibility();
    }

    public IReadOnlyList<AudioDevice> GetPlaybackDevices()
    {
        var result = new List<AudioDevice>();

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection) != 0) return result;
            if (collection.GetCount(out var count) != 0) return result;

            for (var i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                var described = Describe(device);
                if (described is not null) result.Add(described);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Audio", $"Could not enumerate playback devices: {ex.Message}");
        }

        return result;
    }

    public AudioDevice? GetDefaultPlaybackDevice()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            return enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out var device) == 0 ? Describe(device) : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Audio", $"Could not read the default playback device: {ex.Message}");
            return null;
        }
    }

    public bool SetDefaultPlaybackDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        try
        {
            var config = (IPolicyConfig)new PolicyConfigComObject();

            // Console covers system sounds and most applications; Multimedia is what games and
            // players use. Setting one without the other splits audio across two devices.
            var console = config.SetDefaultEndpoint(deviceId, eConsole);
            var multimedia = config.SetDefaultEndpoint(deviceId, eMultimedia);
            return console == 0 && multimedia == 0;
        }
        catch (Exception ex)
        {
            // The interface is undocumented; a Windows change that moves or removes it lands here.
            Log.Warn("Audio", $"Could not set the default playback device: {ex.Message}. Automatic audio switching is unavailable on this build of Windows.");
            return false;
        }
    }

    // --- volume -------------------------------------------------------------
    // IAudioEndpointVolume, unlike IPolicyConfig above, is a documented and supported interface.
    // The event-context GUID is passed as empty: it exists so an application can recognise its own
    // changes coming back through a notification callback, and nothing here subscribes to those.

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private const int CLSCTX_ALL = 23;

    /// <summary>Resolves an endpoint's volume control, or null when the device is gone or refuses.</summary>
    private static IAudioEndpointVolume? OpenVolume(string deviceId)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection) != 0) return null;
            if (collection.GetCount(out var count) != 0) return null;

            for (var i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                if (device.GetId(out var id) != 0) continue;
                if (!string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase)) continue;

                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var iface) != 0) return null;
                return iface as IAudioEndpointVolume;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Audio", $"Could not open volume control for {deviceId}: {ex.Message}");
        }

        return null;
    }

    public float GetVolumeScalar(string deviceId)
    {
        var volume = OpenVolume(deviceId);
        if (volume is null) return -1;

        try { return volume.GetMasterVolumeLevelScalar(out var level) == 0 ? level : -1; }
        catch { return -1; }
    }

    public bool SetVolumeScalar(string deviceId, float scalar)
    {
        var volume = OpenVolume(deviceId);
        if (volume is null) return false;

        try
        {
            var context = Guid.Empty;
            return volume.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0f, 1f), ref context) == 0;
        }
        catch (Exception ex)
        {
            Log.Debug("Audio", $"Could not set the volume of {deviceId}: {ex.Message}");
            return false;
        }
    }

    public bool? GetMute(string deviceId)
    {
        var volume = OpenVolume(deviceId);
        if (volume is null) return null;

        try { return volume.GetMute(out var muted) == 0 ? muted : null; }
        catch { return null; }
    }

    public bool SetMute(string deviceId, bool mute)
    {
        var volume = OpenVolume(deviceId);
        if (volume is null) return false;

        try
        {
            var context = Guid.Empty;
            return volume.SetMute(mute, ref context) == 0;
        }
        catch (Exception ex)
        {
            Log.Debug("Audio", $"Could not set the mute state of {deviceId}: {ex.Message}");
            return false;
        }
    }

    private static AudioDevice? Describe(IMMDevice device)
    {
        try
        {
            if (device.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id)) return null;

            var name = id;
            if (device.OpenPropertyStore(STGM_READ, out var store) == 0)
            {
                var key = FriendlyNameKey;
                if (store.GetValue(ref key, out var value) == 0 && value.PointerValue != IntPtr.Zero)
                    name = Marshal.PtrToStringUni(value.PointerValue) ?? id;
            }

            return new AudioDevice(id, name);
        }
        catch (Exception ex)
        {
            Log.Debug("Audio", $"Could not read an endpoint's details: {ex.Message}");
            return null;
        }
    }
}
