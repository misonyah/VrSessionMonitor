using Microsoft.Win32;

namespace VrSessionMonitor.Optimizations;

public sealed class RegistryAccessor : IRegistryAccessor
{
    public object? GetValue(OptRegistryHive hive, string subKeyPath, string valueName)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.OpenSubKey(subKeyPath);
        return key?.GetValue(valueName);
    }

    public void SetValue(OptRegistryHive hive, string subKeyPath, string valueName, object value, RegistryValueKind kind)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.CreateSubKey(subKeyPath, writable: true);
        key.SetValue(valueName, value, kind);
    }

    public void DeleteValue(OptRegistryHive hive, string subKeyPath, string valueName)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = baseKey.OpenSubKey(subKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static RegistryKey OpenBaseKey(OptRegistryHive hive) =>
        RegistryKey.OpenBaseKey(
            hive == OptRegistryHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            RegistryView.Default);
}
