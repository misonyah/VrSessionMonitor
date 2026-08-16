using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

public sealed class WindowsServiceController : IServiceController
{
    public bool Exists(string serviceName)
    {
        try { using var sc = new ServiceController(serviceName); _ = sc.Status; return true; }
        catch { return false; }
    }

    public bool IsRunning(string serviceName)
    {
        try { using var sc = new ServiceController(serviceName); return sc.Status == ServiceControllerStatus.Running; }
        catch { return false; }
    }

    public async Task StopAsync(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return;
            sc.Stop();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Optimizations", $"Stopping service '{serviceName}' failed: {ex.Message}");
        }
    }

    public async Task StartAsync(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return;
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Optimizations", $"Starting service '{serviceName}' failed: {ex.Message}");
        }
    }

    /// <summary>Same sc-sdset SDDL-grant technique as SRanipalServicePermissions.cs, generalized to
    /// take any service name rather than hardcoding SRanipalService.</summary>
    public async Task<bool> GrantControlPermissionAsync(string serviceName)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return false;

        var currentSddl = await RunScAsync($"sdshow {serviceName}").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(currentSddl)) return false;
        currentSddl = currentSddl.Trim();
        if (currentSddl.Contains(sid, StringComparison.OrdinalIgnoreCase)) return true;

        var newAce = $"(A;;RPWPDTLOCRRC;;;{sid})";
        var insertAt = currentSddl.IndexOf("S:", StringComparison.Ordinal);
        var newSddl = insertAt >= 0 ? currentSddl.Insert(insertAt, newAce) : currentSddl + newAce;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = $"sdset {serviceName} \"{newSddl}\"",
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("Optimizations", $"Elevation prompt for '{serviceName}' control grant was declined.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Optimizations", $"Requesting control-permission grant for '{serviceName}' failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<string?> RunScAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "sc.exe", Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
