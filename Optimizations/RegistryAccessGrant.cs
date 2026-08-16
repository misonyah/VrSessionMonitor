using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

/// <summary>One-time elevated grant of registry write access to specific HKLM keys, mirroring
/// SRanipalServicePermissions.cs's sc-sdset pattern but via PowerShell Set-Acl for the registry.
/// HKCU keys need no grant at all (already owned by the current user) — callers should only pass
/// HKLM subkey paths here.</summary>
public static class RegistryAccessGrant
{
    public static async Task<bool> GrantWriteAccessAsync(IEnumerable<string> hklmSubKeyPaths)
    {
        var paths = hklmSubKeyPaths.Distinct().ToList();
        if (paths.Count == 0) return true;

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
        {
            Log.Debug("Optimizations", "Could not resolve current user's SID — skipping registry access grant.");
            return false;
        }

        var script = string.Join(" ; ", paths.Select(p =>
            $"$k = 'HKLM:\\{p}'; " +
            "if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }; " +
            "$acl = Get-Acl $k; " +
            $"$id = New-Object System.Security.Principal.SecurityIdentifier('{sid}'); " +
            "$rule = New-Object System.Security.AccessControl.RegistryAccessRule($id,'SetValue,QueryValues,EnumerateSubKeys,ReadKey','None','None','Allow'); " +
            "$acl.AddAccessRule($rule); " +
            "Set-Acl $k $acl"));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Log.Warn("Optimizations", "Failed to start elevated registry-ACL-grant process.");
                return false;
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);

            if (proc.ExitCode == 0)
            {
                Log.Info("Optimizations", $"Granted current user write access to {paths.Count} registry key(s) — future applies/reverts won't need elevation.");
                return true;
            }

            Log.Warn("Optimizations", $"Registry ACL grant exited with code {proc.ExitCode}.");
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — UAC declined
        {
            Log.Warn("Optimizations", "Elevation prompt for registry access was declined.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Optimizations", $"Requesting registry access grant failed: {ex.Message}");
            return false;
        }
    }
}
