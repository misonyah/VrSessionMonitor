using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Borrows VRCX's already-authenticated VRChat session instead of storing any credentials here.
/// VRCX persists its cookies in %APPDATA%\VRCX\VRCX.sqlite3 (table `cookies`, row key='default',
/// value = Base64(JSON CookieCollection)) — confirmed against VRCX's own Dotnet/WebApi.cs
/// Load/SaveCookies. We read that one row, read-only and shared (VRCX keeps the DB open in WAL
/// mode), and hand back the vrchat.cloud cookies. Any failure (no VRCX, no row, expired) → null,
/// and the caller skips represent while leaving OSC toggles untouched.
/// </summary>
public sealed class VrcxSessionProvider
{
    private readonly string _dbPath;

    public VrcxSessionProvider(string? dbPathOverride = null)
    {
        _dbPath = dbPathOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCX", "VRCX.sqlite3");
    }

    public CookieCollection? TryLoadVrchatCookies()
    {
        try
        {
            if (!File.Exists(_dbPath)) { Log.Debug("VrcxSession", $"VRCX DB not found at {_dbPath}."); return null; }

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM cookies WHERE key = 'default' LIMIT 1";
            var value = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(value)) { Log.Debug("VrcxSession", "No 'default' cookie row in VRCX DB."); return null; }

            var cookies = ExtractVrchatCookies(value);
            if (cookies.Count == 0) { Log.Debug("VrcxSession", "No vrchat.cloud cookies in VRCX store."); return null; }
            return cookies;
        }
        catch (Exception ex)
        {
            Log.Warn("VrcxSession", $"Failed to read VRCX session cookies: {ex.Message}");
            return null;
        }
    }

    /// <summary>Pure: Base64(JSON CookieCollection) → the vrchat.cloud cookies. Empty on any failure.</summary>
    public static CookieCollection ExtractVrchatCookies(string base64Value)
    {
        var result = new CookieCollection();
        try
        {
            var bytes = Convert.FromBase64String(base64Value);
            var all = JsonSerializer.Deserialize<CookieCollection>(bytes);
            if (all is null) return result;
            foreach (Cookie c in all)
            {
                if (!string.IsNullOrEmpty(c.Domain) && c.Domain.Contains("vrchat.cloud", StringComparison.OrdinalIgnoreCase))
                    result.Add(c);
            }
        }
        catch { /* garbage/format drift → empty */ }
        return result;
    }
}
