using System.Text.RegularExpressions;
using Microsoft.Win32;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed record SteamAppInfo(string AppId, string Name, string InstallDir, string LibraryPath = "")
{
    /// <summary>Best guess at the app's main executable name (no .exe): the largest .exe in the
    /// install folder's root that isn't a crash handler, installer or redistributable. Null if none.</summary>
    public string? GuessProcessName()
    {
        try
        {
            var dir = Path.Combine(LibraryPath, "steamapps", "common", InstallDir);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "*.exe")
                .Select(f => new FileInfo(f))
                .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(f.Name, "crash|unins|setup|redist|install|helper|update", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .OrderByDescending(f => f.Length)
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .FirstOrDefault();
        }
        catch { return null; }
    }
}

/// <summary>Installed Steam apps, read offline from Steam's own files: steamapps\libraryfolders.vdf
/// lists every library, and each library's steamapps\appmanifest_&lt;id&gt;.acf describes one app.</summary>
public static partial class SteamLibrary
{
    public static string? DefaultSteamRoot()
    {
        var p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return string.IsNullOrWhiteSpace(p) ? null : p.Replace('/', '\\');
    }

    public static IReadOnlyList<string> ParseLibraryFolders(string vdf) =>
        PathRe().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static SteamAppInfo? ParseAppManifest(string acf)
    {
        var id = Field(acf, "appid");
        var name = Field(acf, "name");
        return id is null || name is null ? null : new SteamAppInfo(id, name, Field(acf, "installdir") ?? "");
    }

    public static IReadOnlyList<SteamAppInfo> LoadInstalled(string steamRoot)
    {
        var apps = new Dictionary<string, SteamAppInfo>(StringComparer.Ordinal);
        foreach (var lib in Libraries(steamRoot))
        {
            var dir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(dir)) continue;
            foreach (var acf in Directory.EnumerateFiles(dir, "appmanifest_*.acf"))
            {
                try
                {
                    if (ParseAppManifest(File.ReadAllText(acf)) is { } app) apps.TryAdd(app.AppId, app with { LibraryPath = lib });
                }
                catch (Exception ex) { Log.Debug("SteamLibrary", $"Skipped {acf}: {ex.Message}"); }
            }
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsInstalled(string steamRoot, string appId) =>
        Libraries(steamRoot).Any(lib => File.Exists(Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf")));

    /// <summary>True when the app is installed in any Steam library on this machine; false when it
    /// isn't, or Steam itself can't be found.</summary>
    public static bool IsInstalledHere(string appId) =>
        DefaultSteamRoot() is { } root && IsInstalled(root, appId);

    private static IEnumerable<string> Libraries(string steamRoot)
    {
        var list = new List<string> { steamRoot };
        try
        {
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) list.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));
        }
        catch (Exception ex) { Log.Debug("SteamLibrary", $"Reading libraryfolders.vdf failed: {ex.Message}"); }
        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Field(string text, string key)
    {
        var m = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)] private static partial Regex PathRe();
}

/// <summary>Managed apps VrSessionMonitor starts once SteamVR is fully up — the job SteamVR's own
/// "startup overlay apps" did, now owned here so it follows this app's settings.</summary>
public static class SteamVrStartApps
{
    public static IReadOnlyList<ManagedApp> Select(IEnumerable<ManagedApp> apps, Func<string, bool> isRunning) =>
        apps.Where(a => a.Enabled && a.StartWithSteamVr
                        && !string.IsNullOrWhiteSpace(a.Target)
                        && (string.IsNullOrWhiteSpace(a.ProcessName) || !isRunning(a.ProcessName)))
            .OrderBy(a => a.Order)
            .ToList();
}
