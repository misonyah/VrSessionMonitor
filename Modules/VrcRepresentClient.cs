using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

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
