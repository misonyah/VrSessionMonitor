namespace VrSessionMonitor.Config;

/// <summary>
/// Seeds the managed-app list from the per-app settings that existed before this model
/// (Auto-start VRChat, the overlay picker, the various lifecycle Enabled flags, VRChat background
/// mode). Runs once, when ManagedApps is empty — see MonitorConfig.LoadOrCreateDefault.
///
/// The point is that upgrading changes nothing observable: whatever auto-started before still
/// auto-starts, and VRChat's background mode carries over as a real window rule instead of being
/// silently dropped.
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
                // behaviour doesn't silently change on upgrade.
                WindowState = config.SessionFlow.VrChatBackgroundMode ? AppWindowState.Minimized : AppWindowState.Unchanged,
                TargetMonitor = config.SessionFlow.VrChatBackgroundMode ? config.SessionFlow.VrChatBackgroundMonitor : null,
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
