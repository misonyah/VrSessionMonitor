namespace VrSessionMonitor.Modules.Suspend;

/// <summary>
/// Shortens the window titles of one app's processes to the part that tells them apart. Two Unity
/// editors read "ProjectA - SampleScene - … - Unity 2022.3.22f1 &lt;DX11&gt;" and "ProjectB - Main - … -
/// Unity 2022.3.22f1 &lt;DX11&gt;"; the shared ending is noise in a narrow table column.
/// </summary>
public static class WindowTitleDiff
{
    private static readonly char[] Separators = { ' ', '-', '–', '—', '|', ':', '·' };

    /// <summary>Returns one entry per input, in order. Words every titled entry shares at its start
    /// and its end are dropped. Empty titles stay empty and take no part in the comparison; a lone
    /// title, identical titles, or a title that would be left empty are returned in full.</summary>
    public static IReadOnlyList<string> Distinguish(IReadOnlyList<string> titles)
    {
        var words = titles.Select(t => string.IsNullOrWhiteSpace(t)
            ? Array.Empty<string>()
            : t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var titled = words.Where(w => w.Length > 0).ToList();
        if (titled.Count < 2 || titled.Select(w => string.Join(' ', w)).Distinct().Count() < 2)
            return titles.ToList();

        var shortest = titled.Min(w => w.Length);

        var prefix = 0;
        while (prefix < shortest && titled.All(w => w[prefix] == titled[0][prefix])) prefix++;

        var suffix = 0;
        while (suffix < shortest - prefix
               && titled.All(w => w[^(suffix + 1)] == titled[0][^(suffix + 1)])) suffix++;

        return titles.Select((title, i) =>
        {
            var w = words[i];
            if (w.Length == 0) return title;
            var middle = string.Join(' ', w.Skip(prefix).Take(w.Length - prefix - suffix)).Trim(Separators);
            return middle.Length == 0 ? title : middle;
        }).ToList();
    }
}
