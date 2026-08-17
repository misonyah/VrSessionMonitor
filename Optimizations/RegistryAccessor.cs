using System.Security.AccessControl;
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

        // Scoped to SetValue rather than the coarse `writable: true` (= KEY_WRITE, which also
        // demands CreateSubKey) — see OpenForValueWrite's doc for why that distinction is
        // load-bearing here.
        using var existing = OpenForValueWrite(baseKey, subKeyPath);
        if (existing is not null)
        {
            existing.SetValue(valueName, value, kind);
            return;
        }

        // Key doesn't exist yet. Creating it genuinely needs CreateSubKey on the parent, which is
        // fine for HKCU checks (user owns the hive) and for HKLM keys RegistryAccessGrant already
        // pre-created during its elevated grant.
        using var created = baseKey.CreateSubKey(subKeyPath, writable: true);
        created.SetValue(valueName, value, kind);
    }

    public void DeleteValue(OptRegistryHive hive, string subKeyPath, string valueName)
    {
        using var baseKey = OpenBaseKey(hive);
        using var key = OpenForValueWrite(baseKey, subKeyPath);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Opens an existing key for value writes using precisely the right this needs
    /// (<see cref="RegistryRights.SetValue"/>, which also covers deleting a value), or null if the
    /// key doesn't exist.
    ///
    /// Deliberately NOT `writable: true`: that requests KEY_WRITE, which is
    /// KEY_SET_VALUE | KEY_CREATE_SUB_KEY | STANDARD_RIGHTS_WRITE. RegistryAccessGrant grants
    /// least-privilege rights (SetValue + read rights, no CreateSubKey — see its own comment), so
    /// a `writable: true` open is denied outright on every granted HKLM key even though writing
    /// the value itself is permitted. Confirmed live 2026-08-17: every Auto apply AND revert
    /// failed with "Access to the registry key ... is denied" / "Requested registry access is not
    /// allowed" while the ACE sat correctly on the key the whole time. See
    /// RegistryAccessorNarrowAceTests for the regression test that reproduces it.
    /// </summary>
    private static RegistryKey? OpenForValueWrite(RegistryKey baseKey, string subKeyPath) =>
        baseKey.OpenSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.SetValue);

    private static RegistryKey OpenBaseKey(OptRegistryHive hive) =>
        RegistryKey.OpenBaseKey(
            hive == OptRegistryHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            RegistryView.Default);
}
