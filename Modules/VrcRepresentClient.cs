using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>One of the groups you belong to, as returned by GET users/{id}/groups — enough to let
/// the Automation tab offer a name/code autocomplete that resolves to the grp_ id.</summary>
public readonly record struct VrcGroupInfo(string Id, string Name, string? ShortCode);

/// <summary>
/// Minimal VRChat API client for reading/setting your represented group, authenticated by the
/// cookies borrowed from VRCX (see VrcxSessionProvider). Endpoints confirmed against VRCX's
/// src/api/group.js (setGroupRepresentation → PUT groups/{id}/representation, getRepresentedGroup
/// → GET users/{id}/groups/represented). All calls are best-effort: failures log and return
/// null/false so the caller can skip represent without disturbing the OSC toggle.
/// </summary>
public sealed class VrcRepresentClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _cachedUserId;

    public bool HasSession { get; }

    public VrcRepresentClient(VrcxSessionProvider session, HttpMessageHandler? handlerOverride = null)
    {
        var cookies = session.TryLoadVrchatCookies();
        HasSession = cookies is { Count: > 0 };

        HttpMessageHandler handler;
        if (handlerOverride is not null)
        {
            handler = handlerOverride;
        }
        else
        {
            var container = new CookieContainer();
            if (cookies is not null)
                foreach (Cookie c in cookies) container.Add(c);
            handler = new HttpClientHandler { CookieContainer = container, UseCookies = true };
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri("https://api.vrchat.cloud/api/1/") };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VrSessionMonitor/1.0");
    }

    public async Task<string?> GetCurrentUserIdAsync()
    {
        if (_cachedUserId is not null) return _cachedUserId;
        try
        {
            using var resp = await _http.GetAsync("auth/user").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("id", out var idEl))
                _cachedUserId = idEl.GetString();
            return _cachedUserId;
        }
        catch (Exception ex) { Log.Warn("VrcRepresent", $"GetCurrentUserId failed: {ex.Message}"); return null; }
    }

    public async Task<string?> GetRepresentedGroupIdAsync()
    {
        try
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (me is null) return null;
            using var resp = await _http.GetAsync($"users/{me}/groups/represented").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            // Represented-group payload carries the group id under "groupId" (and "id"); empty when none.
            foreach (var key in new[] { "groupId", "id" })
                if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var v = el.GetString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            return null;
        }
        catch (Exception ex) { Log.Warn("VrcRepresent", $"GetRepresentedGroup failed: {ex.Message}"); return null; }
    }

    /// <summary>The groups you belong to (GET users/{id}/groups), each with its display name and
    /// short code so the Automation tab can autocomplete by name or code and store the grp_ id.
    /// Best-effort: any failure (no session, API error) yields an empty list.</summary>
    public async Task<IReadOnlyList<VrcGroupInfo>> GetMyGroupsAsync()
    {
        try
        {
            var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (me is null) return Array.Empty<VrcGroupInfo>();
            using var resp = await _http.GetAsync($"users/{me}/groups").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<VrcGroupInfo>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<VrcGroupInfo>();

            var list = new List<VrcGroupInfo>();
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                // The group id lives under "groupId" or "id" depending on the payload shape; pick
                // whichever actually holds a grp_ value so we never store a member/user id by mistake.
                string? id = null;
                foreach (var key in new[] { "groupId", "id" })
                    if (g.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String &&
                        el.GetString() is { } v && v.StartsWith("grp_", StringComparison.Ordinal))
                    { id = v; break; }
                if (id is null) continue;

                var name = g.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() ?? "" : "";
                var code = g.TryGetProperty("shortCode", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;
                list.Add(new VrcGroupInfo(id, name, code));
            }
            return list;
        }
        catch (Exception ex) { Log.Warn("VrcRepresent", $"GetMyGroups failed: {ex.Message}"); return Array.Empty<VrcGroupInfo>(); }
    }

    public async Task<bool> SetRepresentedAsync(string groupId, bool isRepresenting)
    {
        try
        {
            using var resp = await _http.PutAsJsonAsync(
                $"groups/{groupId}/representation",
                new { isRepresenting }).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                Log.Warn("VrcRepresent", $"PUT representation for {groupId} returned {(int)resp.StatusCode}.");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { Log.Warn("VrcRepresent", $"SetRepresented({groupId},{isRepresenting}) failed: {ex.Message}"); return false; }
    }

    public void Dispose() => _http.Dispose();
}
