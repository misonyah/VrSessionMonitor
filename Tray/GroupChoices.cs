using VrSessionMonitor.Modules;

namespace VrSessionMonitor.Tray;

/// <summary>One entry of a group dropdown: the grp_ id is the stored value, the name is what the
/// user sees.</summary>
public sealed record GroupChoice(string Id, string Name);

/// <summary>Builds the group dropdown's entries: every group from VRCX's cache by name
/// (alphabetical), then any group the config still references that the cache doesn't know, labelled
/// as unknown rather than shown as a bare grp_ id. Every configured id must have an entry, or the
/// grid's combo cell rejects the bound value.</summary>
public static class GroupChoices
{
    public static List<GroupChoice> Build(IEnumerable<string> configuredIds, IReadOnlyList<VrcGroupInfo> cached)
    {
        var choices = cached
            .Where(g => !string.IsNullOrWhiteSpace(g.Id))
            .GroupBy(g => g.Id, StringComparer.Ordinal).Select(grp => grp.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new GroupChoice(g.Id, string.IsNullOrWhiteSpace(g.Name) ? UnknownLabel(g.Id) : g.Name))
            .ToList();

        foreach (var id in configuredIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal))
            if (!choices.Any(c => c.Id == id))
                choices.Add(new GroupChoice(id, UnknownLabel(id)));

        return choices;
    }

    public static string UnknownLabel(string id) => $"Unknown group ({id})";
}

/// <summary>Runs work on a control's UI thread once it has a window handle. The settings window is
/// constructed hidden at app start; BeginInvoke before its handle exists throws, which silently
/// dropped the background-loaded group names.</summary>
public static class UiDispatch
{
    public static void WhenHandleReady(Control control, Action action)
    {
        if (control.IsHandleCreated)
        {
            control.BeginInvoke(action);
            return;
        }

        void OnCreated(object? sender, EventArgs e)
        {
            control.HandleCreated -= OnCreated;
            action();
        }
        control.HandleCreated += OnCreated;
    }
}
