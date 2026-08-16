// Optimizations/PowercfgRunner.cs
using System.ComponentModel;
using System.Diagnostics;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

/// <summary>Real powercfg.exe invocation. Kept separate from PowerPlanOptimization/
/// UsbSelectiveSuspendOptimization so those classes take plain delegates and are unit-testable
/// without shelling out.</summary>
public static class PowercfgRunner
{
    public static async Task<string> RunAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe", Arguments = arguments,
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return output;
        }
        catch (Exception ex)
        {
            Log.Debug("Optimizations", $"'powercfg.exe {arguments}' threw: {ex.Message}");
            return "";
        }
    }

    public static async Task<bool> RunElevatedAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe", Arguments = arguments,
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("Optimizations", "Elevation prompt for powercfg was declined.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Optimizations", $"Elevated 'powercfg.exe {arguments}' failed: {ex.Message}");
            return false;
        }
    }
}
