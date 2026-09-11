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
/// mode), and hand back the vrchat.cloud cookies. Any failure to read/parse (no VRCX, no row, no
/// vrchat.cloud cookies, format drift) → null, and the caller skips represent while leaving OSC
/// toggles untouched. Note we do NOT detect an expired cookie here — an expired session cookie is
/// still returned non-null; expiry only surfaces later as a 401 at the API layer, at which point
/// represent is skipped.
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

    /// <summary>
    /// The groups you belong to, straight out of VRCX's own cache - no VRChat API call, no live
    /// session, works with VRCX closed.
    ///
    /// VRCX keeps them in `configs` under key `config:vrcx_currentusergroups_<your user id>`, whose
    /// value is a JSON array of group objects (id, name, iconUrl, ownerId, roleIds, roles).
    /// Matched with LIKE on the prefix rather than composing the key from a user id, so this needs
    /// no knowledge of who is logged in - and there is only ever one such row per account.
    ///
    /// Note the cached objects carry no shortCode, unlike GET users/{id}/groups, so ShortCode is
    /// always null here. That only costs the short-code autocomplete alias; picking a group by name
    /// still resolves to the right grp_ id, which is what the Automation tab stores.
    ///
    /// Best-effort like everything else here: no VRCX, no row, or format drift yields an empty
    /// list, and the caller falls back to whatever other suggestion sources it has.
    /// </summary>
    public IReadOnlyList<VrcGroupInfo> TryLoadCachedGroups()
    {
        try
        {
            if (!File.Exists(_dbPath)) { Log.Debug("VrcxSession", $"VRCX DB not found at {_dbPath}."); return Array.Empty<VrcGroupInfo>(); }

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM configs WHERE key LIKE 'config:vrcx_currentusergroups_%' LIMIT 1";
            var value = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(value)) { Log.Debug("VrcxSession", "No cached group list in VRCX DB."); return Array.Empty<VrcGroupInfo>(); }

            return ParseCachedGroups(value);
        }
        catch (Exception ex)
        {
            Log.Warn("VrcxSession", $"Failed to read cached VRCX groups: {ex.Message}");
            return Array.Empty<VrcGroupInfo>();
        }
    }

    /// <summary>Pure: VRCX's cached-groups JSON → the groups. Empty on any failure, and entries
    /// missing an id or name are skipped rather than surfacing as blank dropdown rows.</summary>
    public static IReadOnlyList<VrcGroupInfo> ParseCachedGroups(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<VrcGroupInfo>();

            var groups = new List<VrcGroupInfo>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                groups.Add(new VrcGroupInfo(id, name, null));
            }
            return groups;
        }
        catch (Exception ex)
        {
            Log.Debug("VrcxSession", $"Could not parse cached VRCX groups: {ex.Message}");
            return Array.Empty<VrcGroupInfo>();
        }
    }

    /// <summary>Pure: Base64(JSON CookieCollection) → the vrchat.cloud cookies. Empty on any failure.</summary>
    public static CookieCollection ExtractVrchatCookies(string base64Value)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Value);
            var all = JsonSerializer.Deserialize<CookieCollection>(bytes);
            if (all is null) return new CookieCollection();
            // Accumulate into a local list first, and only build the returned collection once the
            // loop has completed without throwing. Adding straight into the result would let a
            // mid-loop throw hand back a PARTIAL non-empty set, contradicting the "empty on any
            // failure" contract.
            var matched = new List<Cookie>();
            foreach (Cookie c in all)
            {
                if (!string.IsNullOrEmpty(c.Domain) && c.Domain.Contains("vrchat.cloud", StringComparison.OrdinalIgnoreCase))
                    matched.Add(c);
            }
            var result = new CookieCollection();
            foreach (var c in matched) result.Add(c);
            return result;
        }
        catch { /* garbage/format drift → empty */ return new CookieCollection(); }
    }
}
