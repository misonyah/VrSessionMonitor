using VrSessionMonitor.Config;

namespace VrSessionMonitor.Modules;

/// <summary>Pure decision rule for which group should be represented given the instance you're
/// currently in. Split out from the monitor so it's unit-testable without any VRChat/VRCX I/O.</summary>
public static class RepresentPolicy
{
    /// <summary>Returns the group ID to represent, or null meaning "clear representation".
    /// In a listed group with Represent=true → that group; otherwise the fallback (or null if none).</summary>
    public static string? ComputeDesiredRepresentedGroupId(
        string? activeGroupId, IReadOnlyList<GroupAutomationEntry> groups, string fallbackGroupId)
    {
        if (activeGroupId is not null)
        {
            foreach (var g in groups)
            {
                if (g.Represent && string.Equals(g.GroupId, activeGroupId, StringComparison.Ordinal))
                    return activeGroupId;
            }
        }
        return string.IsNullOrWhiteSpace(fallbackGroupId) ? null : fallbackGroupId;
    }
}
