using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace VrSessionMonitor.Config;

/// <summary>How a headset's session is brought up. VirtualDesktop = the original Quest flow
/// (VD Streamer, stream gate, VRChat auto-launch). SteamLink = Steam Frame: SteamVR + tools only.</summary>
public enum HeadsetSessionKind { VirtualDesktop, SteamLink }

/// <summary>A command run on the headset over SSH at session start (SteamLink profiles).</summary>
public sealed class HeadsetCommand
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public static class HeadsetPartNames
{
    public const string Adb = "Adb";
    public const string VirtualHere = "VirtualHere";
    public const string SRanipal = "SRanipal";
    public const string ViveFaceTracker = "ViveFaceTracker";
    public const string HotspotKeeper = "HotspotKeeper";
    public const string LinkStatus = "LinkStatus";
}

/// <summary>One headset the user owns. The active one decides the session flow and which
/// headset-specific parts run; shared parts (SlimeVR, overlay, VRCOSC, optimizations...) ignore it.</summary>
public sealed class HeadsetProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Hosts/IPs pinged to detect this headset, tried in order.</summary>
    public List<string> DetectHosts { get; set; } = new();
    /// <summary>Stored as text so a hand-edited unknown value falls back to VirtualDesktop instead of
    /// the configuration binder silently dropping the whole profile (verified: it does).</summary>
    [ConfigurationKeyName("SessionKind")]
    [JsonPropertyName("SessionKind")]
    public string SessionKindName { get; set; } = nameof(HeadsetSessionKind.VirtualDesktop);

    // The binder also tries to fill get-only properties from a matching key and throws on a bad
    // enum string, dropping the profile — so point this one at a key that never exists.
    [JsonIgnore]
    [ConfigurationKeyName("__derived_SessionKind")]
    public HeadsetSessionKind SessionKind =>
        Enum.TryParse<HeadsetSessionKind>(SessionKindName, ignoreCase: true, out var k) ? k : HeadsetSessionKind.VirtualDesktop;
    /// <summary>ssh host alias (from ~/.ssh/config) for headset-side commands and link status.</summary>
    public string SshHost { get; set; } = "";
    /// <summary>Windows scheduled task that keeps the PC hotspot up (empty = not used).</summary>
    public string HotspotKeeperTask { get; set; } = "";
    /// <summary>Log file whose last line reports the keeper's outcome (empty = not used).</summary>
    public string HotspotKeeperLog { get; set; } = "";
    public Dictionary<string, bool> Parts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<HeadsetCommand> HeadsetCommands { get; set; } = new();

    public bool Part(string name, bool defaultValue = false) =>
        Parts.TryGetValue(name, out var v) ? v : defaultValue;
}

public static class HeadsetProfiles
{
    public const string QuestId = "quest2";
    public const string FrameId = "frame";

    /// <summary>The profiles in effect. An empty list (config written before profiles existed, or
    /// a test that builds MonitorConfig directly) means "just the Quest, from Network.*" — so every
    /// pre-profile code path keeps its exact behaviour.</summary>
    public static IReadOnlyList<HeadsetProfile> Effective(MonitorConfig c) =>
        c.Headsets.Count > 0 ? c.Headsets : new[] { QuestFromLegacy(c) };

    public static HeadsetProfile? Find(MonitorConfig c, string? id) =>
        id is null ? null : Effective(c).FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsDetectable(HeadsetProfile p) => p.DetectHosts.Any(h => !string.IsNullOrWhiteSpace(h));

    /// <summary>Adds the built-in profiles not yet offered to this install (ledger: SeededHeadsetIds),
    /// so a profile the user deleted is not re-added. Same pattern as ManagedAppDefaults.</summary>
    public static void SeedDefaults(MonitorConfig c)
    {
        foreach (var p in new[] { QuestFromLegacy(c), FrameDefault() })
        {
            if (c.SeededHeadsetIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase)) continue;
            c.SeededHeadsetIds.Add(p.Id);
            if (!c.Headsets.Any(h => string.Equals(h.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
                c.Headsets.Add(p);
        }
    }

    /// <summary>The Settings tab's "Headset IP" fields, auto-detect and AdbController still use
    /// Network.HeadsetIp/Secondary for the Quest. Call after changing them so detection (which reads
    /// the quest2 profile) follows.</summary>
    public static void SyncQuestFromNetwork(MonitorConfig c)
    {
        var quest = c.Headsets.FirstOrDefault(h => string.Equals(h.Id, QuestId, StringComparison.OrdinalIgnoreCase));
        if (quest is null) return;
        quest.DetectHosts = new[] { c.Network.HeadsetIp, c.Network.HeadsetIpSecondary }
            .Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
    }

    private static HeadsetProfile QuestFromLegacy(MonitorConfig c) => new()
    {
        Id = QuestId,
        DisplayName = string.IsNullOrWhiteSpace(c.Network.HeadsetName) ? "Quest 2" : c.Network.HeadsetName,
        DetectHosts = new[] { c.Network.HeadsetIp, c.Network.HeadsetIpSecondary }
            .Where(h => !string.IsNullOrWhiteSpace(h)).ToList(),
        SessionKindName = nameof(HeadsetSessionKind.VirtualDesktop),
        Parts = new(StringComparer.OrdinalIgnoreCase)
        {
            [HeadsetPartNames.Adb] = true, [HeadsetPartNames.VirtualHere] = true,
            [HeadsetPartNames.SRanipal] = true, [HeadsetPartNames.ViveFaceTracker] = true,
        },
    };

    private static HeadsetProfile FrameDefault() => new()
    {
        Id = FrameId,
        DisplayName = "Steam Frame",
        DetectHosts = new() { "frame" },
        SessionKindName = nameof(HeadsetSessionKind.SteamLink),
        SshHost = "frame",
        Parts = new(StringComparer.OrdinalIgnoreCase)
        {
            [HeadsetPartNames.HotspotKeeper] = true, [HeadsetPartNames.LinkStatus] = true,
        },
    };
}
