namespace VrSessionMonitor.Modules;

/// <summary>
/// Parameter names fixed by VRChat itself rather than configured by the user, so they belong in
/// code rather than in config — unlike the heart rate names, which VRCOSC lets the user rename.
///
/// Deliberately outside the INCLUDE_OSC guard, so callers can reference a name without caring
/// whether OSC support was compiled in.
/// </summary>
public static class VrChatOscParameters
{
    /// <summary>VRChat's own AFK flag. Raised whenever VRChat loses focus, which includes opening
    /// the SteamVR dashboard — see AfkHoldGate for why that must be held down before it is
    /// believed.</summary>
    public const string Afk = "AFK";
}
