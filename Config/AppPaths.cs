using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Config;

/// <summary>
/// Where this app keeps its own data, resolved the way an installed Windows app should.
///
/// Everything used to live next to the executable, which put the live appsettings.json inside
/// bin/Debug/&lt;tfm&gt;/. That is a real hazard, not a style preference: changing the target framework
/// moves the output folder, so the config silently stops being found and a fresh default is written
/// in the new location. Caught live on 2026-08-27 during the net10.0-windows10.0.19041.0 bump —
/// a 19KB config holding nine managed apps, the Home Assistant light maps and eleven optimisation
/// entries was about to be stranded in an orphaned folder. A stale bin/Debug/net8.0-windows/ from
/// an earlier bump shows the same thing had already happened once before.
///
/// LOCAL app data, not Roaming: the config is full of machine-specific absolute paths, Steam app
/// ids and LAN addresses, none of which should follow the user to another machine. Not LocalLow
/// either — that exists for low-integrity sandboxed processes (which is why VRChat, being Unity,
/// writes there) and this app runs at normal integrity.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "VrSessionMonitor";

    /// <summary>%LOCALAPPDATA%\VrSessionMonitor</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string ConfigFilePath => Path.Combine(DataDirectory, "appsettings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>The pre-relocation config location: next to the executable.</summary>
    public static string LegacyConfigFilePath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Moves a config that still lives next to the executable into the app data directory, once.
    /// Copies rather than moves, so a user who downgrades still finds their settings where the old
    /// build expects them, and so a failure part-way through cannot lose the only copy.
    ///
    /// Returns true when a migration actually happened. Deliberately never overwrites an existing
    /// destination: once the app data copy exists it is the live one, and an older file left next
    /// to some previous build must never clobber it.
    /// </summary>
    public static bool MigrateLegacyConfigIfNeeded()
    {
        try
        {
            if (File.Exists(ConfigFilePath)) return false;      // already migrated; destination wins
            if (!File.Exists(LegacyConfigFilePath)) return false; // nothing to migrate — fresh install

            EnsureCreated();
            File.Copy(LegacyConfigFilePath, ConfigFilePath);
            return true;
        }
        catch (Exception ex)
        {
            // Never let a migration problem stop the app starting — it will simply fall back to
            // writing a fresh config, which is recoverable, whereas failing to launch is not.
            Log.Warn("AppPaths", $"Could not migrate the existing config from {LegacyConfigFilePath}: {ex.Message}");
            return false;
        }
    }
}
