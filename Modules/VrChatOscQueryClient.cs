using System.Diagnostics;
using System.Text.Json;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Shared helper for reading a live snapshot of VRChat's own OSCQuery /avatar/parameters tree —
/// the ground-truth signal for "is real tracking data actually reaching VRChat," as opposed to
/// every other check in this app which only observes the transport (TCP connections, process
/// presence). Confirmed live 2026-07-30: both EyeTrackingMonitor's "streaming" check and
/// FaceTrackingMonitor's "moduleConnectedToSRanipal" check can read healthy while the actual OSC
/// values sit frozen — see EyeTrackingOscFreshnessConfig's doc for the full incident.
///
/// Finds VRChat's OSCQuery HTTP port the fast way (matching vrchat-osc-mcp): parses `netstat -ano`
/// for one of VRChat's own listening TCP ports rather than running continuous mDNS discovery for
/// a check this infrequent.
/// </summary>
public static class VrChatOscQueryClient
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Fetches the live parameter tree and pulls out just the named leaves (matched by
    /// exact final path segment, regardless of the current avatar's own parameter prefix — e.g.
    /// EchoFT/v2/, VRCFT v1/v2, etc. all use the same standard leaf names). Returns null if VRChat
    /// isn't running or its OSCQuery endpoint couldn't be reached this attempt — callers should
    /// treat that as "couldn't check this cycle," not as evidence of a problem.</summary>
    public static async Task<Dictionary<string, JsonElement>?> FetchParamsAsync(IReadOnlyCollection<string> paramNames)
    {
        var vrChatPid = Process.GetProcessesByName("VRChat").FirstOrDefault()?.Id;
        if (vrChatPid is null) return null;

        var port = FindListeningTcpPort(vrChatPid.Value);
        if (port is null) return null;

        try
        {
            var json = await HttpClient.GetStringAsync($"http://127.0.0.1:{port}/avatar/parameters").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, JsonElement>();
            Walk(doc.RootElement, result, "", paramNames);
            return result;
        }
        catch (Exception ex)
        {
            Log.Debug("VrChatOscQuery", $"Fetching VRChat's OSCQuery /avatar/parameters (port {port}) threw: {ex.Message}");
            return null;
        }
    }

    private static void Walk(JsonElement node, Dictionary<string, JsonElement> results, string path, IReadOnlyCollection<string> paramNames)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        if (node.TryGetProperty("CONTENTS", out var contents))
        {
            foreach (var prop in contents.EnumerateObject())
                Walk(prop.Value, results, path + "/" + prop.Name, paramNames);
            return;
        }

        if (!node.TryGetProperty("VALUE", out var value)) return;

        var slashIdx = path.LastIndexOf('/');
        var leafName = slashIdx >= 0 ? path[(slashIdx + 1)..] : path;
        if (paramNames.Contains(leafName))
            results[leafName] = value.Clone();
    }

    /// <summary>Parses `netstat -ano` rather than P/Invoking GetExtendedTcpTable directly — this
    /// runs at most once per check interval (default 10s for both callers), so a process-spawn's
    /// overhead doesn't matter, matching this codebase's existing preference for shelling out to a
    /// well-known OS utility over hand-rolled native interop when one already does the job (see
    /// SRanipalServicePermissions' use of sc.exe for the same reason).</summary>
    private static int? FindListeningTcpPort(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p TCP",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!int.TryParse(parts[^1], out var linePid) || linePid != pid) continue;

                var colonIdx = parts[1].LastIndexOf(':');
                if (colonIdx < 0) continue;
                if (int.TryParse(parts[1][(colonIdx + 1)..], out var localPort))
                    return localPort;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("VrChatOscQuery", $"Finding VRChat's listening TCP port via netstat threw: {ex.Message}");
        }

        return null;
    }
}
