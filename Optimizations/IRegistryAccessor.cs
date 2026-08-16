using Microsoft.Win32;

namespace VrSessionMonitor.Optimizations;

public enum OptRegistryHive { LocalMachine, CurrentUser }

/// <summary>Testability seam over Microsoft.Win32.Registry reads/writes — mirrors IProcessLauncher's
/// role for process operations. RegistryAccessor implements it against the real registry;
/// FakeRegistryAccessor (test project) is an in-memory stand-in.</summary>
public interface IRegistryAccessor
{
    /// <summary>Returns the raw value, or null if the key or value doesn't exist.</summary>
    object? GetValue(OptRegistryHive hive, string subKeyPath, string valueName);
    void SetValue(OptRegistryHive hive, string subKeyPath, string valueName, object value, RegistryValueKind kind);
    /// <summary>No-op if the key or value doesn't exist.</summary>
    void DeleteValue(OptRegistryHive hive, string subKeyPath, string valueName);
}
