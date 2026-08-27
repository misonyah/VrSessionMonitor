using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Bluetooth;

/// <summary>
/// Starts managed apps when a tracked Bluetooth device appears — "my toy is switched on" becomes
/// "Intiface and OSCGoesBrrr are already running", instead of starting them by hand every time.
///
/// Three deliberate restraints:
///
/// It only ever STARTS. Nothing is stopped when the device disappears. A device that stops
/// advertising for a moment (battery saving, out of range, behind a body) is routine, and killing
/// an app someone is actively using is far worse than leaving one running.
///
/// It starts each app once per appearance, not on every advertisement, and EnsureRunningAsync
/// already no-ops when the process exists — so a device that flaps cannot spawn duplicates.
///
/// It never launches an app that is disabled in config. The Bluetooth trigger is a convenience on
/// top of the managed-app model, not a way around its switches.
/// </summary>
public sealed class BluetoothTriggeredLauncher
{
    private readonly MonitorConfig _config;
    private readonly IProcessLauncher _launcher;

    public BluetoothTriggeredLauncher(MonitorConfig config, IProcessLauncher launcher)
    {
        _config = config;
        _launcher = launcher;
    }

    public async Task OnTrackedDeviceAppearedAsync(BluetoothDeviceConfig device, BleDeviceSighting sighting)
    {
        if (device.StartAppIds.Count == 0) return;

        var label = device.DisplayName is { Length: > 0 } ? device.DisplayName : device.Address;

        foreach (var appId in device.StartAppIds)
        {
            var app = _config.GetApp(appId);
            if (app is null)
            {
                Log.Warn("Bluetooth", $"{label} appeared and is configured to start '{appId}', but there is no managed app with that id.");
                continue;
            }

            if (!app.Enabled)
            {
                Log.Debug("Bluetooth", $"{label} appeared, but {app.DisplayName} is disabled — not starting it.");
                continue;
            }

            var target = _config.LaunchTargetFor(appId, "");
            if (string.IsNullOrWhiteSpace(target))
            {
                Log.Warn("Bluetooth", $"{label} appeared and should start {app.DisplayName}, but that app has no launch target configured.");
                continue;
            }

            if (app.LaunchMethod == AppLaunchMethod.SteamAppId)
            {
                // steam:// launches go through the orchestrator's URI path, not the process
                // launcher. Rather than half-implement it here, say so plainly.
                Log.Warn("Bluetooth", $"{label} appeared and should start {app.DisplayName}, but Steam-launched apps are not supported by the Bluetooth trigger yet.");
                continue;
            }

            if (_launcher.IsRunning(app.ProcessName))
            {
                Log.Debug("Bluetooth", $"{label} appeared; {app.DisplayName} is already running.");
                continue;
            }

            Log.Info("Bluetooth", $"{label} appeared — starting {app.DisplayName}.");
            try
            {
                var result = await _launcher.EnsureRunningAsync(
                    app.ProcessName, target, null,
                    _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs,
                    suppressUacPrompt: _config.SuppressUacFor(appId, builtInDefault: false)).ConfigureAwait(false);

                if (!result.Success)
                    Log.Warn("Bluetooth", $"Starting {app.DisplayName} for {label} did not confirm success: {result.Error}");
            }
            catch (Exception ex)
            {
                // A failed convenience launch must never take down the scan that triggered it.
                Log.Error("Bluetooth", $"Starting {app.DisplayName} for {label} threw", ex);
            }
        }
    }
}
