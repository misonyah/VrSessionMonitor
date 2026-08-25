namespace VrSessionMonitor.Config;

public enum AppLaunchMethod { Executable, SteamAppId }

/// <summary>Window state to put an app into once its main window exists.
/// Fullscreen resizes the window to fill the target monitor's full bounds, including the area
/// behind the taskbar (unlike Maximized, which respects the working area) — the window keeps its
/// title bar and border, it is not true borderless chrome removal. It deliberately does NOT drive
/// an app's own internal fullscreen mode (VRChat takes a launch arg for that; most apps have their
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

    /// <summary>
    /// Launch this app with __COMPAT_LAYER=RunAsInvoker, so Windows skips the UAC consent prompt
    /// its manifest would otherwise trigger.
    ///
    /// Only meaningful for an app whose manifest asks for elevation. It works for
    /// requestedExecutionLevel="highestAvailable" — which means "prefer elevation, but run fine
    /// without it" — and sr_runtime is the confirmed case: 515 launches since 2026-07-16 with no
    /// prompt and no loss of function.
    ///
    /// It is NOT a way to make an app that genuinely requires admin work unattended. For
    /// requestedExecutionLevel="requireAdministrator", forcing invoker level makes the app start
    /// without the rights it needs and fail at whatever it needed them for, which is worse than the
    /// prompt. Leave this off unless the app is known to tolerate running unelevated.
    ///
    /// Nullable on purpose: null means "never set", so a config written before this property
    /// existed keeps the built-in default for its app rather than deserializing to false and
    /// silently switching the UAC prompts back on. Only an explicit true/false overrides.
    /// </summary>
    public bool? SuppressUacPrompt { get; set; }
}
