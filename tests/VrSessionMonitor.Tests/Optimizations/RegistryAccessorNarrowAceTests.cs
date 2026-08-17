using System;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using VrSessionMonitor.Optimizations;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

/// <summary>
/// Regression test for the bug found live on 2026-08-17: every Auto apply AND revert failed with
/// "Access to the registry key ... is denied" / "Requested registry access is not allowed", even
/// though RegistryAccessGrant had successfully written its ACE (verified present on the real keys
/// as "MACHINE\user | SetValue, ReadKey | Allow").
///
/// Root cause: RegistryAccessGrant deliberately grants least-privilege rights
/// (SetValue + read rights, NOT CreateSubKey — narrowed on purpose, see its own comment), but
/// RegistryAccessor opened keys with the coarse `writable: true` flag, which requests KEY_WRITE
/// (= KEY_SET_VALUE | KEY_CREATE_SUB_KEY | STANDARD_RIGHTS_WRITE). The CreateSubKey right is
/// missing from the grant, so the OPEN itself was denied before a single value could be written.
///
/// Why no existing test caught it: every other test in this suite uses FakeRegistryAccessor, which
/// has no ACL concept at all. A naive real-registry test would ALSO have passed, because a plain
/// HKCU key is fully owned by the current user and `writable: true` succeeds there. Reproducing
/// this requires a key whose DACL grants the current user exactly what RegistryAccessGrant grants
/// and nothing more — which is what this test builds. No elevation needed: you can rewrite the
/// DACL on a key you own.
/// </summary>
public class RegistryAccessorNarrowAceTests : IDisposable
{
    // Deliberately under HKCU (user-owned, so no elevation is needed to rewrite its DACL) and
    // uniquely named per run so concurrent/leftover runs can't collide.
    private readonly string _scratchPath = $@"Software\VrSessionMonitorTests\NarrowAce_{Guid.NewGuid():N}";

    private readonly RegistryAccessor _accessor = new();

    public RegistryAccessorNarrowAceTests()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = baseKey.CreateSubKey(_scratchPath, writable: true);

        // Seed a pre-existing value so the revert-path assertion has something real to delete.
        key.SetValue("Seeded", 1, RegistryValueKind.DWord);

        // Replace the DACL with exactly what RegistryAccessGrant grants and nothing else:
        // SetValue + ReadKey, no inheritance, no CreateSubKey, no FullControl. Protection with
        // preserveInheritance:false is what actually strips the inherited "you own HKCU, do
        // anything" ACEs — without it the inherited rights mask the bug and the test passes
        // against the unfixed code.
        var sid = WindowsIdentity.GetCurrent().User!;
        var security = key.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new RegistryAccessRule(
            sid,
            RegistryRights.SetValue | RegistryRights.ReadKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        key.SetAccessControl(security);
    }

    [Fact]
    public void SetValue_succeeds_under_a_SetValue_only_ACE()
    {
        _accessor.SetValue(OptRegistryHive.CurrentUser, _scratchPath, "Written", 42, RegistryValueKind.DWord);

        Assert.Equal(42, _accessor.GetValue(OptRegistryHive.CurrentUser, _scratchPath, "Written"));
    }

    [Fact]
    public void DeleteValue_succeeds_under_a_SetValue_only_ACE()
    {
        _accessor.DeleteValue(OptRegistryHive.CurrentUser, _scratchPath, "Seeded");

        Assert.Null(_accessor.GetValue(OptRegistryHive.CurrentUser, _scratchPath, "Seeded"));
    }

    /// <summary>The full apply-then-revert round trip an Auto-mode check performs across a SteamVR
    /// start/stop pair — the exact sequence that failed live on 2026-08-17.</summary>
    [Fact]
    public void Apply_then_revert_round_trip_succeeds_under_a_SetValue_only_ACE()
    {
        _accessor.SetValue(OptRegistryHive.CurrentUser, _scratchPath, "Seeded", 99, RegistryValueKind.DWord);
        Assert.Equal(99, _accessor.GetValue(OptRegistryHive.CurrentUser, _scratchPath, "Seeded"));

        _accessor.SetValue(OptRegistryHive.CurrentUser, _scratchPath, "Seeded", 1, RegistryValueKind.DWord);
        Assert.Equal(1, _accessor.GetValue(OptRegistryHive.CurrentUser, _scratchPath, "Seeded"));
    }

    public void Dispose()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            // The narrow ACE we set doesn't include Delete, so re-grant ourselves full control
            // (we still own the key, so WriteDAC is available) before removing the scratch tree.
            using (var key = baseKey.OpenSubKey(_scratchPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership | RegistryRights.ChangePermissions))
            {
                if (key is not null)
                {
                    var security = key.GetAccessControl();
                    security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
                    security.AddAccessRule(new RegistryAccessRule(
                        WindowsIdentity.GetCurrent().User!,
                        RegistryRights.FullControl,
                        InheritanceFlags.None,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    key.SetAccessControl(security);
                }
            }

            baseKey.DeleteSubKeyTree(_scratchPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Cleanup is best-effort — a leaked scratch key under
            // HKCU\Software\VrSessionMonitorTests is harmless and uniquely named per run.
        }
    }
}
