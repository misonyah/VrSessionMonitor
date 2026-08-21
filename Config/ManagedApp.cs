namespace VrSessionMonitor.Config;

public enum AppLaunchMethod { Executable, SteamAppId }

/// <summary>Window state to put an app into once its main window exists.
/// Fullscreen is borderless-maximised onto the target monitor — it deliberately does NOT drive an
/// app's own internal fullscreen mode (VRChat takes a launch arg for that; most apps have their
/// own toggle). Unchanged leaves the window exactly as the app opened it.</summary>
public enum AppWindowState { Unchanged, Normal, Minimized, Maximized, Fullscreen }

/// <summary>
/// One app VrSessionMonitor knows how to launch and/or manage the window of. See
/// docs/superpowers/specs/2026-08-21-managed-apps-window-control-design.md.
///
/// This is the configuration and window layer only — the bespoke recovery managers
/// (face-tracking auto-heal, SlimeVR auto-stop, VRChat restart, eye-camera give-up) keep their own
/// logic and simply read <see cref="Enabled"/> from here.
/// </summary>
public sealed class ManagedApp
{
    /// <summary>Stable persistence key (e.g. "vrchat"). NEVER rename once shipped — same rule as
    /// IOptimization.Id, since config entries are keyed on it.</summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Whether this app participates in auto-start/stop. Seeded from the old per-app
    /// toggles (Auto-start VRChat, Auto-start/stop VRCOSC, ...) during migration.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Launch ordering; lower goes first. User-reorderable in the Settings UI later.</summary>
    public int Order { get; set; }

    public AppLaunchMethod LaunchMethod { get; set; } = AppLaunchMethod.Executable;

    /// <summary>Executable path, or the Steam app id when LaunchMethod is SteamAppId.</summary>
    public string Target { get; set; } = "";

    /// <summary>Process name (no .exe) used for presence detection, matching the strings the
    /// existing monitors already use — e.g. "VRChat", "sr_runtime", "Baballonia.Desktop".</summary>
    public string ProcessName { get; set; } = "";

    public AppWindowState WindowState { get; set; } = AppWindowState.Unchanged;

    /// <summary>Bring the window to the foreground after launch — for apps needing manual
    /// attention, e.g. SlimeVR's calibration.</summary>
    public bool BringToFront { get; set; }

    /// <summary>Push the window behind others without activating it. Mutually exclusive with
    /// BringToFront; if both are set, BringToFront wins (an explicit request to see it beats an
    /// explicit request to hide it).</summary>
    public bool KeepInBackground { get; set; }

    /// <summary>1-based monitor index to move the window onto, or null to leave it where it opens.</summary>
    public int? TargetMonitor { get; set; }

    /// <summary>Apply the window rules even when the app was started outside VrSessionMonitor.
    /// This is what makes "minimize VRChat even when I start it myself" work.</summary>
    public bool ApplyWindowRulesWhenStartedManually { get; set; } = true;
}
