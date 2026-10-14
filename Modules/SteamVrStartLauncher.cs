using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>Starts the managed apps marked "Start when SteamVR starts" once SteamVR is fully running
/// (see SteamVrStartApps for which ones). Steam apps go through steam://rungameid, the same
/// elevation-safe path the overlays use; executables through the normal launcher.</summary>
public static class SteamVrStartLauncher
{
    public static async Task LaunchDueAppsAsync(MonitorConfig config, IProcessLauncher launcher, Action<string> launchUri)
    {
        foreach (var app in SteamVrStartApps.Select(config.ManagedApps.ToArray(), launcher.IsRunning))
        {
            try
            {
                if (app.LaunchMethod == AppLaunchMethod.SteamAppId)
                {
                    Log.Info("SteamVrStart", $"SteamVR is up — starting {app.DisplayName} via steam://rungameid/{app.Target}.");
                    launchUri($"steam://rungameid/{app.Target}");
                }
                else
                {
                    Log.Info("SteamVrStart", $"SteamVR is up — starting {app.DisplayName}.");
                    await launcher.EnsureRunningAsync(app.ProcessName, app.Target, null,
                        config.Polling.ProcessLaunchTimeoutMs, config.Polling.ProcessPollIntervalMs,
                        suppressUacPrompt: app.SuppressUacPrompt ?? false).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SteamVrStart", $"Starting {app.DisplayName} failed: {ex.Message}");
            }
        }
    }
}
