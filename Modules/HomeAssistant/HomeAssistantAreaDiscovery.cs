using System.Text.Json;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
public sealed record AreaInfo(string AreaId, string Name);
public sealed record LightInfo(string EntityId, string DisplayName);

/// <summary>
/// Resolves Home Assistant's area -> light.* entity map over an already-connected
/// HomeAssistantClient, using HA's registry list commands (config/area_registry/list,
/// config/entity_registry/list, config/device_registry/list) rather than template rendering —
/// simple request/response, no streaming-result parsing needed. An entity's area comes either
/// directly from its own registry entry, or (if unset) from its device's area — a light entity
/// is commonly assigned an area only via the device it belongs to.
/// </summary>
public sealed class HomeAssistantAreaDiscovery
{
    private readonly HomeAssistantClient _client;

    public HomeAssistantAreaDiscovery(HomeAssistantClient client)
    {
        _client = client;
    }

    public async Task<List<AreaInfo>> DiscoverAreasAsync(CancellationToken token = default)
    {
        var result = await _client.SendRegistryCommandAsync("config/area_registry/list", token).ConfigureAwait(false);
        var areas = new List<AreaInfo>();
        foreach (var el in result.EnumerateArray())
        {
            var areaId = el.GetProperty("area_id").GetString();
            var name = el.TryGetProperty("name", out var n) ? n.GetString() : areaId;
            if (areaId is not null) areas.Add(new AreaInfo(areaId, name ?? areaId));
        }

        Log.Info("HomeAssistant", $"Discovered {areas.Count} area(s).");
        return areas;
    }

    public async Task<List<LightInfo>> DiscoverLightsInAreaAsync(string areaId, CancellationToken token = default)
    {
        var entitiesTask = _client.SendRegistryCommandAsync("config/entity_registry/list", token);
        var devicesTask = _client.SendRegistryCommandAsync("config/device_registry/list", token);
        await Task.WhenAll(entitiesTask, devicesTask).ConfigureAwait(false);

        var deviceAreas = new Dictionary<string, string?>();
        // Prefers name_by_user (a user's own rename in HA's UI) over the device's default name,
        // same precedence HA's own frontend uses when it doesn't have a more specific entity name.
        var deviceNames = new Dictionary<string, string?>();
        foreach (var dev in devicesTask.Result.EnumerateArray())
        {
            var id = dev.GetProperty("id").GetString();
            var devAreaId = dev.TryGetProperty("area_id", out var a) && a.ValueKind != JsonValueKind.Null ? a.GetString() : null;
            var devName = dev.TryGetProperty("name_by_user", out var nbu) ? nbu.GetString() : null;
            if (string.IsNullOrWhiteSpace(devName))
                devName = dev.TryGetProperty("name", out var dn) ? dn.GetString() : null;
            if (id is null) continue;
            deviceAreas[id] = devAreaId;
            deviceNames[id] = devName;
        }

        var lights = new List<LightInfo>();
        foreach (var entity in entitiesTask.Result.EnumerateArray())
        {
            var entityId = entity.GetProperty("entity_id").GetString();
            if (entityId is null || !entityId.StartsWith("light.", StringComparison.Ordinal)) continue;

            string? deviceId = entity.TryGetProperty("device_id", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() : null;

            string? entityAreaId = entity.TryGetProperty("area_id", out var ea) && ea.ValueKind != JsonValueKind.Null ? ea.GetString() : null;
            if (entityAreaId is null && deviceId is not null)
                deviceAreas.TryGetValue(deviceId, out entityAreaId);

            if (entityAreaId != areaId) continue;

            // Same precedence Home Assistant's own frontend uses to label an entity: a user's own
            // rename of the entity first, then the integration's default entity name, then falling
            // back to the owning device's name (many light entities have no name of their own at
            // all — they're just "the light on this device" — so the device name is often the only
            // human-meaningful label available), and only the raw entity_id as a last resort.
            // Blank/whitespace strings (not just JSON null) are treated as "missing" at each step —
            // HA can return "" for an unset name rather than omitting the field or using null,
            // which would otherwise win an empty string over a real name further down the chain.
            string? displayName = entity.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = entity.TryGetProperty("original_name", out var on) ? on.GetString() : null;
            if (string.IsNullOrWhiteSpace(displayName) && deviceId is not null)
                deviceNames.TryGetValue(deviceId, out displayName);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = entityId;

            lights.Add(new LightInfo(entityId, displayName));
        }

        Log.Info("HomeAssistant", $"Found {lights.Count} light(s) in area '{areaId}'.");
        return lights;
    }
}
#endif
