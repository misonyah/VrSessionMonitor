using System.Globalization;

namespace VrSessionMonitor.Modules.Suspend;

/// <summary>The text of the Status tab's freeze table, kept out of the form so it is testable.</summary>
public static class FreezeTableFormat
{
    private const double Mb = 1024d * 1024;

    public static string Memory(long bytes)
    {
        var mb = bytes / Mb;
        return mb < 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:0} MB", Math.Floor(mb))
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", mb / 1024);
    }

    /// <summary>One label per row of a single app: the part of the window title that tells the
    /// rows apart, or the executable name for a process without a window.</summary>
    public static IReadOnlyList<string> ProcessLabels(IReadOnlyList<FreezeProcessRow> appRows)
    {
        var titles = WindowTitleDiff.Distinguish(appRows.Select(r => r.WindowTitle).ToList());
        return appRows.Select((r, i) => string.IsNullOrWhiteSpace(titles[i]) ? r.ProcessName + ".exe" : titles[i]).ToList();
    }

    public static string State(FreezeState state) => state switch
    {
        FreezeState.Frozen => "frozen",
        FreezeState.KeptRunning => "kept running",
        _ => "running",
    };

    public static string AppState(IReadOnlyList<FreezeProcessRow> appRows)
    {
        var frozen = appRows.Count(r => r.State == FreezeState.Frozen);
        if (frozen == appRows.Count) return "frozen";
        if (frozen > 0) return $"{frozen} of {appRows.Count} frozen";
        return appRows.All(r => r.State == FreezeState.KeptRunning) ? "kept running" : "running";
    }
}
