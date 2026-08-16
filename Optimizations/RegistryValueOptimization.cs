using System.Globalization;
using Microsoft.Win32;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

/// <summary>Generic registry-backed check covering 8 of the v1 checklist's 11 entries — a single
/// class parameterized by a target list (static or dynamically enumerated, e.g. per network
/// adapter) rather than one subclass per check. All targets in a group apply/revert together.
/// Note: reverting a value that lived under a subkey RegistryValueOptimization itself created
/// (e.g. a fresh Image File Execution Options\PerfOptions key) deletes only the value, not the
/// now-empty subkey — harmless, and simpler than tracking key-vs-value creation separately.</summary>
public sealed class RegistryValueOptimization : IOptimization
{
    private readonly Func<IEnumerable<RegistryValueTarget>> _targetsProvider;
    private readonly IRegistryAccessor _registry;

    public RegistryValueOptimization(
        string id, string displayName, OptimizationCategory category,
        Func<IEnumerable<RegistryValueTarget>> targetsProvider, IRegistryAccessor registry)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
        _targetsProvider = targetsProvider;
        _registry = registry;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public OptimizationCategory Category { get; }

    public Task<OptimizationStatus> CheckAsync()
    {
        var targets = _targetsProvider().ToList();
        if (targets.Count == 0) return Task.FromResult(OptimizationStatus.Unknown);

        var appliedCount = targets.Count(t => ValuesEqual(_registry.GetValue(t.Hive, t.SubKeyPath, t.ValueName), t.DesiredValue));
        var status = appliedCount == targets.Count ? OptimizationStatus.Applied
            : appliedCount == 0 ? OptimizationStatus.NotApplied
            : OptimizationStatus.Unknown; // partially applied — e.g. interrupted mid-apply
        return Task.FromResult(status);
    }

    public Task EnsureAccessGrantedAsync(OptimizationEntry entry)
    {
        // Replaced in Task 4 with the real HKLM ACL-grant call.
        entry.AccessGranted = true;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(OptimizationEntry entry)
    {
        foreach (var t in _targetsProvider())
        {
            if (!entry.CapturedOriginalValues.ContainsKey(t.StorageKey))
            {
                var existing = _registry.GetValue(t.Hive, t.SubKeyPath, t.ValueName);
                entry.CapturedOriginalValues[t.StorageKey] = existing is null ? null : Convert.ToString(existing, CultureInfo.InvariantCulture);
            }
            _registry.SetValue(t.Hive, t.SubKeyPath, t.ValueName, t.DesiredValue, t.Kind);
        }
        return Task.CompletedTask;
    }

    public Task RevertAsync(OptimizationEntry entry)
    {
        foreach (var t in _targetsProvider())
        {
            if (!entry.CapturedOriginalValues.TryGetValue(t.StorageKey, out var captured)) continue;

            if (captured is null)
                _registry.DeleteValue(t.Hive, t.SubKeyPath, t.ValueName);
            else
                _registry.SetValue(t.Hive, t.SubKeyPath, t.ValueName, ConvertBack(captured, t.Kind), t.Kind);

            entry.CapturedOriginalValues.Remove(t.StorageKey);
        }
        return Task.CompletedTask;
    }

    private static bool ValuesEqual(object? actual, object desired) =>
        actual is not null && Convert.ToString(actual, CultureInfo.InvariantCulture) == Convert.ToString(desired, CultureInfo.InvariantCulture);

    private static object ConvertBack(string captured, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(captured, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(captured, CultureInfo.InvariantCulture),
        _ => captured,
    };
}
