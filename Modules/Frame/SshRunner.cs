using System.Diagnostics;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Frame;

public sealed record SshResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface ISshRunner
{
    Task<SshResult> RunAsync(string host, string command, TimeSpan timeout);
}

/// <summary>Runs one command on a headset via the Windows OpenSSH client. Key auth only
/// (BatchMode=yes): a password prompt can never block a background poll. Host details (user, key,
/// host-key alias) come from ~/.ssh/config, so this only needs the alias.</summary>
public sealed class SshRunner : ISshRunner
{
    public async Task<SshResult> RunAsync(string host, string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("ssh.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", host, command })
            psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { return new SshResult(-1, "", $"ssh not startable: {ex.Message}", false); }

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Log.Debug("Ssh", $"ssh {host} timed out after {timeout.TotalSeconds:F0}s");
            return new SshResult(-1, "", "timed out", true);
        }
        return new SshResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
    }
}
