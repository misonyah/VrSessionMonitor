using Microsoft.Win32;

namespace VrSessionMonitor.Optimizations;

public sealed record RegistryValueTarget(
    OptRegistryHive Hive,
    string SubKeyPath,
    string ValueName,
    object DesiredValue,
    RegistryValueKind Kind)
{
    /// <summary>Stable key into OptimizationEntry.CapturedOriginalValues for this exact target.</summary>
    public string StorageKey => $"{Hive}\\{SubKeyPath}!{ValueName}";
}
