using System.Diagnostics;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Best-effort wireless ADB integration. Whether wireless debugging is even paired/enabled on
/// the Quest 2 was unconfirmed as of 2026-07-15 (no device answered on :5555 during testing —
/// headset was off). Every method here degrades to "log and return false" on any failure; ADB
/// must never block or fail the rest of the session-start flow.
/// </summary>
public sealed class AdbController
{
    private readonly MonitorConfig _config;

    public AdbController(MonitorConfig config)
    {
        _config = config;
    }

    private string Target => $"{_config.Network.HeadsetIp}:{_config.Network.AdbPort}";

    public async Task<bool> TryConnectAsync()
    {
        if (!_config.Adb.Enabled)
        {
            Log.Debug("Adb", "ADB integration disabled in config.");
            return false;
        }

        var (ok, output) = await RunAdbAsync($"connect {Target}").ConfigureAwait(false);
        if (!ok)
        {
            Log.Warn("Adb", $"adb connect {Target} failed to execute.");
            return false;
        }

        var connected = output.Contains("connected to", StringComparison.OrdinalIgnoreCase);
        if (connected)
            Log.Info("Adb", $"Connected to headset over wireless ADB at {Target}.");
        else
            Log.Warn("Adb", $"adb connect did not report success. Output: {output.Trim()}");

        return connected;
    }

    public async Task<bool> TryLaunchVirtualDesktopAppAsync()
    {
        if (!_config.Adb.Enabled || !_config.Adb.AutoLaunchVirtualDesktopApp)
        {
            Log.Debug("Adb", "Remote VD app launch disabled in config.");
            return false;
        }

        var pkg = _config.Adb.VirtualDesktopPackageName;
        var (ok, output) = await RunAdbAsync($"-s {Target} shell monkey -p {pkg} -c android.intent.category.LAUNCHER 1").ConfigureAwait(false);

        if (ok && output.Contains("Events injected", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Adb", $"Launched Virtual Desktop app on headset (package {pkg}).");
            return true;
        }

        Log.Warn("Adb", $"Could not confirm Virtual Desktop app launch on headset. Output: {output.Trim()}");
        return false;
    }

    public async Task<int?> TryGetBatteryPercentAsync()
    {
        if (!_config.Adb.Enabled) return null;

        var (ok, output) = await RunAdbAsync($"-s {Target} shell dumpsys battery").ConfigureAwait(false);
        if (!ok)
        {
            Log.Debug("Adb", "Battery check failed to execute.");
            return null;
        }

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("level:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(':', 2);
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var pct))
                {
                    Log.Info("Adb", $"Headset battery: {pct}%");
                    return pct;
                }
            }
        }

        Log.Debug("Adb", $"Could not parse battery level from dumpsys output.");
        return null;
    }

    private async Task<(bool Ok, string Output)> RunAdbAsync(string arguments)
    {
        if (!File.Exists(_config.Paths.AdbExe))
        {
            Log.Warn("Adb", $"adb.exe not found at {_config.Paths.AdbExe}");
            return (false, "");
        }

        Log.Trace("Adb", $"Running: adb {arguments}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.Paths.AdbExe,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                Log.Warn("Adb", "Process.Start returned null for adb.");
                return (false, "");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            string stdout, stderr;
            try
            {
                stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Adb", $"adb {arguments} timed out after 8s, killing process.");
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "");
            }

            var combined = stdout + stderr;
            Log.Trace("Adb", $"adb {arguments} -> exit={process.ExitCode}, output={combined.Trim()}");
            return (true, combined);
        }
        catch (Exception ex)
        {
            Log.Warn("Adb", $"adb {arguments} threw: {ex.Message}");
            return (false, "");
        }
    }
}
