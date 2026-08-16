using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

/// <summary>Owns the registered v1 check list and their persisted OptimizationEntry state. Exposes
/// HandleSteamVrRunningChangedAsync as a plain method (rather than subscribing to
/// SteamVrMonitor.FullyRunningChanged internally) so it's directly callable from tests without a
/// real SteamVrMonitor — TrayApplicationContext does the actual event wiring.</summary>
public sealed class OptimizationsManager
{
    private readonly MonitorConfig _config;
    private readonly string _configPath;
    private readonly List<IOptimization> _optimizations;

    public OptimizationsManager(MonitorConfig config, string configPath, List<IOptimization> optimizations)
    {
        _config = config;
        _configPath = configPath;
        _optimizations = optimizations;

        foreach (var opt in _optimizations)
            if (!_config.Optimizations.Entries.ContainsKey(opt.Id))
                _config.Optimizations.Entries[opt.Id] = new OptimizationEntry();
    }

    public IReadOnlyList<IOptimization> Optimizations => _optimizations;

    public OptimizationEntry GetEntry(string id) => _config.Optimizations.Entries[id];

    public async Task<OptimizationStatus> CheckAsync(IOptimization optimization)
    {
        try { return await optimization.CheckAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Warn("Optimizations", $"CheckAsync for '{optimization.Id}' threw: {ex.Message}");
            return OptimizationStatus.Unknown;
        }
    }

    /// <summary>Sets a check's mode from the UI dropdown. Moving to Manual/Auto requests the grant
    /// first; if it fails, the mode falls back to Off (so the UI never shows a mode that can't
    /// actually work) and this throws so the UI can surface an inline error.</summary>
    public async Task SetModeAsync(IOptimization optimization, OptimizationMode mode)
    {
        var entry = GetEntry(optimization.Id);

        if (mode == OptimizationMode.Off)
        {
            entry.Mode = OptimizationMode.Off;
            _config.Save(_configPath);
            return;
        }

        await optimization.EnsureAccessGrantedAsync(entry).ConfigureAwait(false);
        if (!entry.AccessGranted)
        {
            entry.Mode = OptimizationMode.Off;
            _config.Save(_configPath);
            throw new InvalidOperationException($"Could not obtain the access needed for '{optimization.DisplayName}'.");
        }

        entry.Mode = mode;
        _config.Save(_configPath);
    }

    public async Task ApplyManualAsync(IOptimization optimization)
    {
        var entry = GetEntry(optimization.Id);
        await optimization.EnsureAccessGrantedAsync(entry).ConfigureAwait(false);
        if (!entry.AccessGranted)
        {
            Log.Warn("Optimizations", $"Skipping manual apply for '{optimization.Id}' — access not granted.");
            return;
        }
        await optimization.ApplyAsync(entry).ConfigureAwait(false);
        _config.Save(_configPath);
    }

    public async Task HandleSteamVrRunningChangedAsync(bool running)
    {
        foreach (var opt in _optimizations)
        {
            var entry = GetEntry(opt.Id);
            if (entry.Mode != OptimizationMode.Auto) continue;

            try
            {
                if (running)
                {
                    await opt.EnsureAccessGrantedAsync(entry).ConfigureAwait(false);
                    if (!entry.AccessGranted) continue;

                    var status = await CheckAsync(opt).ConfigureAwait(false);
                    if (status != OptimizationStatus.Applied)
                        await opt.ApplyAsync(entry).ConfigureAwait(false);
                }
                else
                {
                    await opt.RevertAsync(entry).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Optimizations", $"Auto {(running ? "apply" : "revert")} for '{opt.Id}' failed: {ex.Message}");
            }
        }
        _config.Save(_configPath);
    }
}
