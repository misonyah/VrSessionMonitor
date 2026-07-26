using System.Text.Json;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
public sealed record AreaInfo(string AreaId, string Name);

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

    public async Task<List<string>> DiscoverLightsInAreaAsync(string areaId, CancellationToken token = default)
    {
        var entitiesTask = _client.SendRegistryCommandAsync("config/entity_registry/list", token);
        var devicesTask = _client.SendRegistryCommandAsync("config/device_registry/list", token);
        await Task.WhenAll(entitiesTask, devicesTask).ConfigureAwait(false);

        var deviceAreas = new Dictionary<string, string?>();
        foreach (var dev in devicesTask.Result.EnumerateArray())
        {
            var id = dev.GetProperty("id").GetString();
            var devAreaId = dev.TryGetProperty("area_id", out var a) && a.ValueKind != JsonValueKind.Null ? a.GetString() : null;
            if (id is not null) deviceAreas[id] = devAreaId;
        }

        var lights = new List<string>();
        foreach (var entity in entitiesTask.Result.EnumerateArray())
        {
            var entityId = entity.GetProperty("entity_id").GetString();
            if (entityId is null || !entityId.StartsWith("light.", StringComparison.Ordinal)) continue;

            string? entityAreaId = entity.TryGetProperty("area_id", out var ea) && ea.ValueKind != JsonValueKind.Null ? ea.GetString() : null;
            if (entityAreaId is null && entity.TryGetProperty("device_id", out var d) && d.ValueKind != JsonValueKind.Null)
            {
                var deviceId = d.GetString();
                if (deviceId is not null) deviceAreas.TryGetValue(deviceId, out entityAreaId);
            }

            if (entityAreaId == areaId) lights.Add(entityId);
        }

        Log.Info("HomeAssistant", $"Found {lights.Count} light(s) in area '{areaId}'.");
        return lights;
    }
}
#endif
