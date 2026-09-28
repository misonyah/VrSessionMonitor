using System.Text.Json;
using System.Text.RegularExpressions;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Best-effort, login-free suggestion lists for the Automation tab: group IDs you've actually
/// joined (mined from VRChat's own logs) and bool avatar OSC parameter names (from the per-avatar
/// OSC configs VRChat writes to disk). Both are read-only file scans that never throw — any I/O
/// failure yields an empty list, since these only power autocomplete convenience.
/// </summary>
public static class AutomationSuggestionSources
{
    private static readonly Regex GroupIdRegex = new(@"~group\((grp_[^)]+)\)", RegexOptions.Compiled);

    public static IReadOnlyList<string> ScanGroupIds(string logDir)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(logDir)) return Array.Empty<string>();
            foreach (var file in Directory.EnumerateFiles(logDir, "output_log_*.txt"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var m = GroupIdRegex.Match(line);
                        if (m.Success) found.Add(m.Groups[1].Value);
                    }
                }
                catch (Exception ex) { Log.Debug("AutomationSuggestions", $"Skipped log {file}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Debug("AutomationSuggestions", $"ScanGroupIds failed: {ex.Message}"); }

        var list = found.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Settable (input) parameter names of every type — bool, int, float — from the most
    /// recently written avatar OSC config, i.e. the avatar you wore last (VRChat rewrites that file
    /// when the avatar loads). Group automation can send any of those types.</summary>
    public static IReadOnlyList<string> ScanLastWornAvatarParams(string oscRootDir)
    {
        try
        {
            if (!Directory.Exists(oscRootDir)) return Array.Empty<string>();
            var newest = Directory.EnumerateFiles(oscRootDir, "*.json", SearchOption.AllDirectories)
                .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Avatars{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? Array.Empty<string>() : ReadSettableParams(newest);
        }
        catch (Exception ex)
        {
            Log.Debug("AutomationSuggestions", $"ScanLastWornAvatarParams failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Settable parameters of one specific avatar (the one being worn, from AvatarTracker).</summary>
    public static IReadOnlyList<string> ScanAvatarParams(string oscRootDir, string avatarId)
    {
        var file = AvatarTracker.FindConfigFile(oscRootDir, avatarId);
        return file is null ? Array.Empty<string>() : ReadSettableParams(file);
    }

    private static IReadOnlyList<string> ReadSettableParams(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("parameters", out var pars) || pars.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in pars.EnumerateArray())
                if (p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && p.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                    names.Add(n.GetString()!);
            return names.ToList();
        }
        catch (Exception ex)
        {
            Log.Debug("AutomationSuggestions", $"Reading {file} failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> ScanOscBoolParams(string oscRootDir)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(oscRootDir)) return Array.Empty<string>();
            // usr_*/Avatars/avtr_*.json
            foreach (var file in Directory.EnumerateFiles(oscRootDir, "*.json", SearchOption.AllDirectories))
            {
                if (!file.Contains($"{Path.DirectorySeparatorChar}Avatars{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("parameters", out var pars) || pars.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var p in pars.EnumerateArray())
                    {
                        if (!p.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                        if (IsBool(p, "input") || IsBool(p, "output"))
                            found.Add(nameEl.GetString()!);
                    }
                }
                catch (Exception ex) { Log.Debug("AutomationSuggestions", $"Skipped OSC config {file}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Debug("AutomationSuggestions", $"ScanOscBoolParams failed: {ex.Message}"); }

        var list = found.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static bool IsBool(JsonElement param, string ioName) =>
        param.TryGetProperty(ioName, out var io)
        && io.ValueKind == JsonValueKind.Object
        && io.TryGetProperty("type", out var t)
        && t.ValueKind == JsonValueKind.String
        && string.Equals(t.GetString(), "Bool", StringComparison.OrdinalIgnoreCase);
}
