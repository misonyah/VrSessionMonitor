namespace VrSessionMonitor.Config;

/// <summary>
/// Seeds the managed-app list from the per-app settings that existed before this model
/// (Auto-start VRChat, the overlay picker, the various lifecycle Enabled flags, VRChat background
/// mode). Runs once, when ManagedApps is empty — see MonitorConfig.LoadOrCreateDefault.
///
/// The point is to preserve which apps auto-start, not to leave every observable behaviour
/// unchanged — some apps (e.g. SlimeVR, which gains BringToFront here) pick up new default window
/// rules that didn't exist before this model. What must not regress is that whatever auto-started
/// before still auto-starts, and VRChat's background mode carries over as "start minimized"
/// instead of being silently dropped.
/// </summary>
public static class ManagedAppDefaults
{
    public static List<ManagedApp> SeedFrom(MonitorConfig config)
    {
        var order = 0;
        var apps = new List<ManagedApp>
        {
            new()
            {
                Id = "vrchat",
                DisplayName = "VRChat",
                Enabled = config.SessionFlow.AutoLaunchVrChat,
                Order = order++,
                LaunchMethod = AppLaunchMethod.SteamAppId,
                Target = config.Paths.VrChatSteamAppId,
                ProcessName = "VRChat",
                // Background mode was the only pre-existing window setting; carry it across so the
                // behaviour doesn't silently change on upgrade. TargetMonitor is deliberately NOT
                // seeded from VrChatBackgroundMonitor: the old background-mode code never relocated
                // the window (VrChatBackgroundMonitor was unused for that purpose), and MoveToMonitor
                // preserves the window's current size rather than resizing it — seeding a monitor
                // here would just move VRChat's deliberately-small background window to a monitor it
                // never used to move to, which is a real behaviour change, not upgrade-safe carryover.
                WindowState = config.SessionFlow.VrChatBackgroundMode ? AppWindowState.Minimized : AppWindowState.Unchanged,
                TargetMonitor = null,
            },
            new()
            {
                Id = "slimevr",
                DisplayName = "SlimeVR",
                Enabled = config.SlimeVrLifecycle.Enabled,
                Order = order++,
                Target = config.Paths.SlimeVrExe,
                ProcessName = "slimevr",
                // Needs manual checking/calibration, so it should be visible rather than buried.
                BringToFront = true,
            },
            new()
            {
                Id = "vrcosc",
                DisplayName = "VRCOSC",
                Enabled = config.VrcOscLifecycle.Enabled,
                Order = order++,
                Target = config.Paths.VrcOscExe,
                ProcessName = "VRCOSC",
            },
            new()
            {
                Id = "vrcfacetracking",
                DisplayName = "VRCFaceTracking",
                Enabled = config.VrcFaceTrackingLifecycle.Enabled,
                Order = order++,
                Target = config.Paths.VrcFaceTrackingExe,
                ProcessName = "VRCFaceTracking",
            },
            new()
            {
                Id = "baballonia",
                DisplayName = "Baballonia",
                Enabled = config.BaballoniaLifecycle.Enabled,
                Order = order++,
                Target = config.Paths.BaballoniaExe,
                ProcessName = "Baballonia.Desktop",
            },
            new()
            {
                Id = "sranipal",
                DisplayName = "SRanipal (sr_runtime)",
                Enabled = config.VirtualHereSRanipalLifecycle.Enabled,
                Order = order++,
                Target = config.Paths.SRanipalExe,
                ProcessName = "sr_runtime",
                // Carries across the hardcoded suppressUacPrompt:true that its three launch sites
                // used before this became a setting. sr_runtime's manifest asks for
                // "highestAvailable", so it prompts on an admin-capable account but runs fine
                // unelevated — fatal for an unattended relaunch, since nothing is there to click.
                SuppressUacPrompt = true,
            },
            new()
            {
                Id = "virtualhere",
                DisplayName = "VirtualHere Client",
                Enabled = config.VirtualHereSRanipalLifecycle.Enabled,
                Order = order++,
                Target = config.Paths.VirtualHereClientExe,
                ProcessName = "vhui64",
            },
            new()
            {
                Id = "ovrtoolkit",
                DisplayName = "OVR Toolkit",
                Enabled = config.SessionFlow.VrOverlay == VrOverlayChoice.OvrToolkit,
                Order = order++,
                LaunchMethod = AppLaunchMethod.SteamAppId,
                Target = config.Paths.OvrToolkitSteamAppId,
                ProcessName = "OVR Toolkit",
            },
            new()
            {
                Id = "xsoverlay",
                DisplayName = "XSOverlay",
                Enabled = config.SessionFlow.VrOverlay == VrOverlayChoice.XSOverlay,
                Order = order++,
                LaunchMethod = AppLaunchMethod.SteamAppId,
                Target = config.Paths.XSOverlaySteamAppId,
                ProcessName = "XSOverlay",
            },
        };

        return apps;
    }
}
