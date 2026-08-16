using VrSessionMonitor.Config;

namespace VrSessionMonitor.Optimizations;

/// <summary>Stops a group of stray services if running (v1: vmms/vboxdrv/WslService, bundled as
/// one check). RevertAsync is a deliberate permanent no-op — see the design spec's "Service
/// revert semantics": stopping a stray VM/container service on SteamVR start is one-directional
/// cleanup, not state to restore, since the user may not want it running again regardless.</summary>
public sealed class ServiceStateOptimization : IOptimization
{
    private readonly List<string> _serviceNames;
    private readonly IServiceController _services;

    public ServiceStateOptimization(string id, string displayName, IEnumerable<string> serviceNames, IServiceController services)
    {
        Id = id;
        DisplayName = displayName;
        _serviceNames = serviceNames.ToList();
        _services = services;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public OptimizationCategory Category => OptimizationCategory.Services;

    public Task<OptimizationStatus> CheckAsync()
    {
        var anyRunning = _serviceNames.Any(s => _services.Exists(s) && _services.IsRunning(s));
        return Task.FromResult(anyRunning ? OptimizationStatus.NotApplied : OptimizationStatus.Applied);
    }

    public async Task EnsureAccessGrantedAsync(OptimizationEntry entry)
    {
        if (entry.AccessGranted) return;

        var existing = _serviceNames.Where(_services.Exists).ToList();
        if (existing.Count == 0) { entry.AccessGranted = true; return; } // nothing present — nothing to grant on

        var allGranted = true;
        foreach (var name in existing)
            allGranted &= await _services.GrantControlPermissionAsync(name).ConfigureAwait(false);

        if (allGranted) entry.AccessGranted = true;
    }

    public async Task ApplyAsync(OptimizationEntry entry)
    {
        foreach (var name in _serviceNames)
            if (_services.Exists(name) && _services.IsRunning(name))
                await _services.StopAsync(name).ConfigureAwait(false);
    }

    public Task RevertAsync(OptimizationEntry entry) => Task.CompletedTask;
}
