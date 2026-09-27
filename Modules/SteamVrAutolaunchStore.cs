using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed record AutolaunchEntry(string AppKey, bool Autolaunch, string FilePath);

/// <summary>SteamVR's "startup overlay apps" switches: <c>autolaunch</c> in
/// <c>&lt;Steam&gt;\config\vrappconfig\&lt;app_key&gt;.vrappconfig</c>. These start apps whenever SteamVR
/// starts, bypassing VrSessionMonitor's per-headset decisions. SteamVR rewrites the files on exit, so
/// callers must only write while vrserver is not running.</summary>
public sealed class SteamVrAutolaunchStore
{
    private readonly string _dir;
    public SteamVrAutolaunchStore(string vrappconfigDir) => _dir = vrappconfigDir;

    public static string? DefaultDir()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrWhiteSpace(steam)) return null;
        var d = Path.Combine(steam.Replace('/', '\\'), "config", "vrappconfig");
        return Directory.Exists(d) ? d : null;
    }

    public IReadOnlyList<AutolaunchEntry> ReadAll()
    {
        var list = new List<AutolaunchEntry>();
        if (!Directory.Exists(_dir)) return list;
        foreach (var f in Directory.EnumerateFiles(_dir, "*.vrappconfig"))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f));
                var auto = j?["autolaunch"]?.GetValue<bool>() ?? false;
                list.Add(new AutolaunchEntry(Path.GetFileNameWithoutExtension(f), auto, f));
            }
            catch (Exception ex) { Log.Debug("SteamVrAutolaunch", $"skip {Path.GetFileName(f)}: {ex.Message}"); }
        }
        return list;
    }

    public bool TryDisable(string appKey, out string error)
    {
        var f = Path.Combine(_dir, appKey + ".vrappconfig");
        if (!File.Exists(f)) { error = $"{appKey}: not found"; return false; }
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            j["autolaunch"] = false;
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, f, overwrite: true);
            Log.Info("SteamVrAutolaunch", $"Disabled SteamVR auto-start for {appKey}.");
            error = "";
            return true;
        }
        catch (Exception ex) { error = $"{appKey}: {ex.Message}"; return false; }
    }
}
