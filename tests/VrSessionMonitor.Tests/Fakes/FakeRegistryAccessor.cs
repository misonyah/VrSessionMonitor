using System.Collections.Generic;
using Microsoft.Win32;
using VrSessionMonitor.Optimizations;

namespace VrSessionMonitor.Tests.Fakes;

/// <summary>In-memory IRegistryAccessor for tests. Values are keyed by (hive, subKeyPath, valueName)
/// so tests can seed pre-existing values and assert on what got written/deleted.</summary>
public sealed class FakeRegistryAccessor : IRegistryAccessor
{
    private readonly Dictionary<(OptRegistryHive, string, string), object> _values = new();
    public readonly List<(OptRegistryHive Hive, string SubKeyPath, string ValueName, object Value, RegistryValueKind Kind)> SetCalls = new();
    public readonly List<(OptRegistryHive Hive, string SubKeyPath, string ValueName)> DeleteCalls = new();

    public void Seed(OptRegistryHive hive, string subKeyPath, string valueName, object value) =>
        _values[(hive, subKeyPath, valueName)] = value;

    public object? GetValue(OptRegistryHive hive, string subKeyPath, string valueName) =>
        _values.TryGetValue((hive, subKeyPath, valueName), out var v) ? v : null;

    public void SetValue(OptRegistryHive hive, string subKeyPath, string valueName, object value, RegistryValueKind kind)
    {
        _values[(hive, subKeyPath, valueName)] = value;
        SetCalls.Add((hive, subKeyPath, valueName, value, kind));
    }

    public void DeleteValue(OptRegistryHive hive, string subKeyPath, string valueName)
    {
        _values.Remove((hive, subKeyPath, valueName));
        DeleteCalls.Add((hive, subKeyPath, valueName));
    }
}
