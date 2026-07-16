using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class MetaHeadsetCandidate
{
    public required string Ip { get; init; }
    public required string Mac { get; init; }
}

/// <summary>
/// Finds a Meta/Oculus headset on the local network by MAC OUI prefix, for auto-filling
/// NetworkConfig.HeadsetIp instead of requiring manual entry.
///
/// OUI prefixes verified 2026-07-17 via maclookup.app / netify.ai (IEEE-sourced vendor
/// registries), not guessed. 2C:26:17 (registered to "Oculus VR, LLC") is independently
/// confirmed real — it matches this rig's own Quest 2's actual MAC (2C:26:17:98:4B:D6), found
/// during the original 2026-07-15 headset investigation. The rest are "Meta Platforms, Inc." /
/// "Facebook, Inc." registrations, included because headsets released after the 2021 Meta
/// rebrand plausibly use a newer block instead of the original Oculus VR LLC one — this part is
/// UNVERIFIED against an actual newer headset, since this rig only has a Quest 2.
///
/// No native ARP-table API (GetIpNetTable) is used here on purpose — that needs unsafe P/Invoke
/// marshaling of a variable-length struct array, real memory-safety risk for a best-effort
/// helper. `arp -a` needs no elevation and its DATA rows (IP / MAC / type) are stable across
/// locales even though header text isn't, so this matches on shape (regex) rather than parsing
/// headers.
/// </summary>
public static class HeadsetDiscovery
{
    private static readonly HashSet<string> MetaOuiPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "2C:26:17", // Oculus VR, LLC — confirmed real, matches this rig's actual Quest 2
        "48:57:DD", "A4:0E:2B", // Facebook, Inc.
        "78:C4:FA", "48:05:60", "88:25:08", "94:F9:29", "B4:17:A8", "80:F3:EF",
        "C0:DD:8A", "CC:A1:74", "D4:D6:59", "D0:B3:C2", "84:57:F7", "50:99:03", // Meta Platforms, Inc.
    };

    private static readonly Regex ArpRowPattern = new(
        @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})",
        RegexOptions.Compiled);

    /// <summary>Ping-sweeps the local subnet (populating Windows' ARP cache), then reads
    /// `arp -a` and returns every entry whose MAC matches a known Meta/Oculus OUI prefix.
    /// Best-effort: returns an empty list on any failure, never throws. Can return more than one
    /// candidate (multiple Meta devices on the network) — the caller decides what to do with
    /// that rather than this method guessing.</summary>
    public static async Task<List<MetaHeadsetCandidate>> DiscoverAsync(CancellationToken ct = default)
    {
        try
        {
            var hosts = GetLocalSubnetHosts();
            if (hosts.Count == 0)
            {
                Log.Warn("HeadsetDiscovery", "Could not determine a local IPv4 /23-or-smaller subnet to scan.");
                return new List<MetaHeadsetCandidate>();
            }

            await PingSweepAsync(hosts, ct).ConfigureAwait(false);
            var candidates = ReadArpTableForMetaDevices();
            Log.Info("HeadsetDiscovery", $"Swept {hosts.Count} host(s), found {candidates.Count} Meta/Oculus MAC match(es).");
            return candidates;
        }
        catch (Exception ex)
        {
            Log.Warn("HeadsetDiscovery", $"Headset discovery threw: {ex.Message}");
            return new List<MetaHeadsetCandidate>();
        }
    }

    /// <summary>Only the first NIC that's up, non-loopback, and has a subnet of /23 or smaller
    /// (at most ~510 hosts) — deliberately refuses to sweep anything bigger so a weird VM/VPN
    /// adapter reporting a huge subnet can't turn a tray-menu click into a multi-minute scan.</summary>
    private static List<string> GetLocalSubnetHosts()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.IPv4Mask is null) continue;

                var ipBytes = addr.Address.GetAddressBytes();
                var maskBytes = addr.IPv4Mask.GetAddressBytes();
                var maskBits = maskBytes.Sum(b => System.Numerics.BitOperations.PopCount(b));
                if (maskBits < 23) continue; // bigger than a /23 — skip, too large to sweep quickly

                var networkBytes = new byte[4];
                for (var i = 0; i < 4; i++) networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
                var networkValue = BitConverter.ToUInt32(networkBytes.Reverse().ToArray(), 0);

                var hostBits = 32 - maskBits;
                var hostCount = (1u << hostBits) - 2; // exclude network + broadcast addresses

                var hosts = new List<string>();
                for (uint h = 1; h <= hostCount; h++)
                {
                    var hostBytes = BitConverter.GetBytes(networkValue + h).Reverse().ToArray();
                    hosts.Add(new IPAddress(hostBytes).ToString());
                }
                return hosts; // first qualifying interface is enough
            }
        }

        return new List<string>();
    }

    private static async Task PingSweepAsync(List<string> hosts, CancellationToken ct)
    {
        var tasks = hosts.Select(async ip =>
        {
            try
            {
                using var ping = new Ping();
                await ping.SendPingAsync(ip, 300).ConfigureAwait(false);
            }
            catch
            {
                // Expected for the vast majority of addresses in the swept range — most hosts in
                // any given subnet simply don't exist. Nothing to log per-address here.
            }
        });

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Debug("HeadsetDiscovery", "Ping sweep didn't finish within 15s — proceeding with whatever ARP entries exist so far.");
        }
    }

    private static List<MetaHeadsetCandidate> ReadArpTableForMetaDevices()
    {
        var results = new List<MetaHeadsetCandidate>();
        string output;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "arp",
                    Arguments = "-a",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log.Warn("HeadsetDiscovery", $"Running 'arp -a' threw: {ex.Message}");
            return results;
        }

        foreach (Match m in ArpRowPattern.Matches(output))
        {
            var mac = m.Groups[2].Value.Replace('-', ':').ToUpperInvariant();
            if (MetaOuiPrefixes.Contains(mac[..8]))
                results.Add(new MetaHeadsetCandidate { Ip = m.Groups[1].Value, Mac = mac });
        }

        return results;
    }
}
