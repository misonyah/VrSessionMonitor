using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// One-time setup: grants the current Windows user Start/Stop rights on the SRanipalService
/// Windows Service via its security descriptor (SDDL), so FaceTrackingMonitor's automated
/// recovery (see SRanipalServiceConfig) can actually start it without this whole app needing to
/// run elevated. This is the standard, sanctioned way to delegate control of one specific service
/// to a non-admin account — not a UAC bypass: applying the SDDL change itself still requires one
/// genuine elevated confirmation (the user approves a single UAC prompt for `sc.exe`), after which
/// the grant persists in the service's own ACL and every future check/start from this unelevated
/// app just works, with no further prompts.
/// </summary>
public static class SRanipalServicePermissions
{
    public static async Task EnsureStartStopPermissionAsync(MonitorConfig config)
    {
        if (!config.SRanipalService.Enabled || !config.SRanipalService.GrantStartStopPermissionOnStartup) return;

        var serviceName = config.SRanipalService.ServiceName;
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
        {
            Log.Debug("SRanipalPermissions", "Could not resolve current user's SID — skipping permission check.");
            return;
        }

        var currentSddl = await RunScAsync($"sdshow {serviceName}").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(currentSddl))
        {
            Log.Debug("SRanipalPermissions", $"Could not read '{serviceName}' security descriptor (service missing, or 'sc' failed) — skipping.");
            return;
        }

        currentSddl = currentSddl.Trim();

        if (currentSddl.Contains(sid, StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug("SRanipalPermissions", $"Current user already has an explicit ACE on '{serviceName}' — nothing to grant.");
            return;
        }

        Log.Info("SRanipalPermissions", $"Current user has no explicit permissions on '{serviceName}' yet — requesting elevation once to grant Start/Stop rights.");

        // RP=Start WP=Stop DT=PauseContinue LO=Interrogate CR=UserDefinedControl RC=ReadControl —
        // the standard minimal grant for "let this non-admin account control this one service",
        // deliberately excluding WD/WO (WRITE_DAC/WRITE_OWNER) so the granted account can't further
        // reassign the service's own permissions.
        var newAce = $"(A;;RPWPDTLOCRRC;;;{sid})";
        var insertAt = currentSddl.IndexOf("S:", StringComparison.Ordinal);
        var newSddl = insertAt >= 0 ? currentSddl.Insert(insertAt, newAce) : currentSddl + newAce;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"sdset {serviceName} \"{newSddl}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Log.Warn("SRanipalPermissions", "Failed to start elevated 'sc sdset' process.");
                return;
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);

            if (proc.ExitCode == 0)
                Log.Info("SRanipalPermissions", $"Granted Start/Stop rights on '{serviceName}' to the current user — future recovery attempts won't need elevation.");
            else
                Log.Warn("SRanipalPermissions", $"'sc sdset {serviceName}' exited with code {proc.ExitCode} — permission grant may not have applied.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — user clicked "No" on the UAC prompt
        {
            Log.Warn("SRanipalPermissions", $"Elevation prompt was declined — '{serviceName}' auto-recovery will keep needing elevation until this is granted. Will ask again next startup.");
        }
        catch (Exception ex)
        {
            Log.Warn("SRanipalPermissions", $"Requesting the elevated permission grant for '{serviceName}' failed: {ex.Message}");
        }
    }

    private static async Task<string?> RunScAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Log.Debug("SRanipalPermissions", $"Running 'sc.exe {arguments}' threw: {ex.Message}");
            return null;
        }
    }
}
