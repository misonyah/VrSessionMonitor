namespace VrSessionMonitor.Logging;

/// <summary>
/// Deletes log files past their retention window.
///
/// Split out of Log deliberately: Log is a process-wide static with a background writer thread, so
/// a test that drives it has to Init/Shutdown the singleton and poisons every later test in the
/// run (Shutdown calls CompleteAdding, after which any Write throws). Pruning is a pure function
/// of a directory, a window and the file currently open, so it belongs somewhere it can be tested
/// without touching global state — the same reasoning as AfkHoldGate and LightSetting.
///
/// Added after the log directory reached 625 MB across 44 files with no retention at all.
/// </summary>
public static class LogRetention
{
    public const string FilePrefix = "monitor_";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>The log file name for a given day.</summary>
    public static string FileNameFor(DateTime date) => $"{FilePrefix}{date:yyyy-MM-dd}.log";

    /// <summary>
    /// Deletes log files whose filename date is older than retentionDays before today. Returns how
    /// many were removed.
    ///
    /// Matches on the date in the FILENAME rather than the file's last-write time: a file is only
    /// appended to on its own day, whereas a timestamp can be rewritten by a backup tool, a sync
    /// client or a plain copy — which would make retention delete the wrong things.
    ///
    /// Anything that isn't ours is left alone: a different prefix, an unparseable date, or the file
    /// currently being written to. Never throws; a directory that can't be read or a file that
    /// can't be deleted is reported and skipped, because failing to prune must never take the app
    /// down with it.
    /// </summary>
    public static int Prune(string logDirectory, int retentionDays, string? currentFilePath = null,
        DateTime? today = null, Action<string>? onProblem = null)
    {
        if (retentionDays <= 0) return 0; // disabled
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory)) return 0;

        var cutoff = (today ?? DateTime.Now).Date.AddDays(-retentionDays);
        var deleted = 0;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(logDirectory, FilePrefix + "*.log").ToList(); }
        catch (Exception ex)
        {
            onProblem?.Invoke($"Could not list {logDirectory}: {ex.Message}");
            return 0;
        }

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith(FilePrefix, StringComparison.Ordinal)) continue;

            var datePart = name[FilePrefix.Length..];
            if (!DateTime.TryParseExact(datePart, DateFormat, null,
                    System.Globalization.DateTimeStyles.None, out var fileDate))
                continue; // hand-renamed or someone else's file — not ours to delete

            if (fileDate >= cutoff) continue;

            if (currentFilePath is not null &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentFilePath), StringComparison.OrdinalIgnoreCase))
                continue; // never delete the file the writer has open

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                onProblem?.Invoke($"Could not delete {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return deleted;
    }
}
