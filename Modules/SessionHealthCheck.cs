using Microsoft.Win32;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>One thing worth warning about before or during a session.</summary>
public sealed record SessionWarning(string Summary, string Detail);

/// <summary>
/// Surfaces the conditions that have actually cost frames on this machine, so they are visible
/// before a session rather than discovered at 20 FPS mid-session.
///
/// Both checks come from a real diagnosis on 2026-09-02. Memory: a development environment had
/// pushed RAM to 95% used with 29.5 GB paged out, and VRChat stuttered on page faults while CPU and
/// GPU both sat idle — nothing in the app reported it. Avatars: with the performance filter off and
/// a 31-avatar cap, a 17-player world put VRChat's main thread at 86% of one core and held frames
/// at 20 while the GPU idled at 64%.
///
/// Deliberately advisory. Nothing here changes a setting on the user's behalf: both have real
/// trade-offs (memory belongs to applications they are using; avatar limits decide what they can
/// see of other people), so the right move is to say so and let them choose.
/// </summary>
public static class SessionHealthCheck
{
    /// <summary>Free physical RAM below this is where paging started to hurt here.</summary>
    public const double LowMemoryGb = 8.0;

    /// <summary>Pagefile beyond this means a lot has already been pushed to disk.</summary>
    public const double HighPagefileGb = 10.0;

    /// <summary>VRChat's "show every avatar however expensive" value.</summary>
    public const int ShowAllAvatarsRating = 5;

    /// <summary>Above this many simultaneous avatars, main-thread cost dominates in a busy world.</summary>
    public const int HighAvatarCap = 20;

    /// <summary>Pure so it can be tested without touching the machine.</summary>
    public static SessionWarning? CheckMemory(double freeGb, double pagefileGb)
    {
        if (freeGb >= LowMemoryGb && pagefileGb <= HighPagefileGb) return null;

        var parts = new List<string>();
        if (freeGb < LowMemoryGb) parts.Add($"only {freeGb:0.#} GB of RAM free");
        if (pagefileGb > HighPagefileGb) parts.Add($"{pagefileGb:0.#} GB pushed to the pagefile");

        return new SessionWarning(
            $"Memory is tight — {string.Join(", ", parts)}",
            "VRChat stutters on page faults long before the CPU or GPU look busy, so this is easy to "
            + "misread as a graphics problem. Close or freeze background applications; editors and "
            + "browsers are usually the biggest holders.");
    }

    /// <summary>Pure counterpart for the avatar settings.</summary>
    public static SessionWarning? CheckAvatarLimits(int? minimumRatingToDisplay, int? maxAvatars)
    {
        var parts = new List<string>();
        if (minimumRatingToDisplay >= ShowAllAvatarsRating) parts.Add("every avatar is shown regardless of how expensive it is");
        if (maxAvatars > HighAvatarCap) parts.Add($"up to {maxAvatars} avatars at once");
        if (parts.Count == 0) return null;

        return new SessionWarning(
            $"VRChat avatar limits are wide open — {string.Join(", and ", parts)}",
            "Avatar physics run on VRChat's main thread, so in a busy instance one unoptimised "
            + "avatar can cost more than everyone else combined. Raising the minimum performance "
            + "rank is the single biggest lever when frames drop in a crowded world.");
    }

    /// <summary>Reads VRChat's own settings, which Unity stores as PlayerPrefs under hashed key
    /// names — hence matching on the readable prefix rather than computing the hash.</summary>
    public static (int? MinimumRating, int? MaxAvatars) ReadVrChatAvatarSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\VRChat\VRChat");
            if (key is null) return (null, null);

            int? Find(string prefix)
            {
                foreach (var name in key.GetValueNames())
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return key.GetValue(name) as int?;
                return null;
            }

            return (Find("VRC_AVATAR_PERFORMANCE_RATING_MINIMUM_TO_DISPLAY"), Find("avatarProxyShowMaxNumber"));
        }
        catch (Exception ex)
        {
            Log.Debug("Health", $"Could not read VRChat's avatar settings: {ex.Message}");
            return (null, null);
        }
    }
}
