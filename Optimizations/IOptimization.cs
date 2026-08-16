using VrSessionMonitor.Config;

namespace VrSessionMonitor.Optimizations;

/// <summary>One checkable, optionally apply/revert-able VR/Windows performance tweak (see
/// docs/superpowers/specs/2026-08-16-optimizations-tab-design.md). Implementations must never
/// throw out of CheckAsync — a missing key/service reads as NotApplied, not an exception.</summary>
public interface IOptimization
{
    /// <summary>Stable identity — also the persistence key in OptimizationsConfig.Entries. Never
    /// rename once shipped.</summary>
    string Id { get; }
    string DisplayName { get; }
    OptimizationCategory Category { get; }

    Task<OptimizationStatus> CheckAsync();
    /// <summary>Idempotent — no-ops if entry.AccessGranted is already true. On success, sets
    /// entry.AccessGranted = true; on failure/decline, leaves it false and logs why.</summary>
    Task EnsureAccessGrantedAsync(OptimizationEntry entry);
    Task ApplyAsync(OptimizationEntry entry);
    Task RevertAsync(OptimizationEntry entry);
}
