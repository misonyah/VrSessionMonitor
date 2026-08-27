using System;
using System.IO;
using System.Linq;
using VrSessionMonitor.Logging;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Retention deletes files, so its rules need pinning: only our own filenames, never the file
/// being written, never anything with an unparseable date.
///
/// Drives LogRetention directly rather than the Log singleton. An earlier version of these tests
/// called Log.Init/Log.Shutdown and broke 33 unrelated tests in the same run — Shutdown calls
/// CompleteAdding on the shared queue, after which every later Write throws.
/// </summary>
public class LogRetentionTests : IDisposable
{
    private readonly string _dir;
    private static readonly DateTime Today = new(2026, 8, 27);

    public LogRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vrsm-log-retention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteLog(int daysAgo)
    {
        var path = Path.Combine(_dir, LogRetention.FileNameFor(Today.AddDays(-daysAgo)));
        File.WriteAllText(path, "x");
        return path;
    }

    private string[] Remaining() =>
        Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    [Fact]
    public void Files_past_the_window_go_and_files_inside_it_stay()
    {
        var ancient = WriteLog(45);
        var justOutside = WriteLog(31);
        var justInside = WriteLog(29);
        var today = WriteLog(0);

        var deleted = LogRetention.Prune(_dir, retentionDays: 30, today: Today);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(ancient));
        Assert.False(File.Exists(justOutside));
        Assert.True(File.Exists(justInside));
        Assert.True(File.Exists(today));
    }

    [Fact]
    public void The_boundary_day_is_kept_not_deleted()
    {
        // Exactly retentionDays old is still within "keep 30 days" — off-by-one here silently
        // shortens the window the user asked for.
        var exactlyAtCutoff = WriteLog(30);

        LogRetention.Prune(_dir, retentionDays: 30, today: Today);

        Assert.True(File.Exists(exactlyAtCutoff));
    }

    [Fact]
    public void The_file_being_written_is_never_deleted()
    {
        // Even when it is old enough to qualify — deleting the open file is self-inflicted damage.
        var current = WriteLog(90);

        var deleted = LogRetention.Prune(_dir, retentionDays: 30, currentFilePath: current, today: Today);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void Unrelated_files_are_left_alone()
    {
        // A log directory is somewhere people drop things. Only our naming scheme, with a
        // parseable date, is ours to delete.
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "keep");
        File.WriteAllText(Path.Combine(_dir, "monitor_backup.log"), "keep");
        File.WriteAllText(Path.Combine(_dir, "monitor_not-a-date.log"), "keep");
        File.WriteAllText(Path.Combine(_dir, "other_2020-01-01.log"), "keep");
        WriteLog(90);

        var deleted = LogRetention.Prune(_dir, retentionDays: 30, today: Today);

        Assert.Equal(1, deleted);
        var remaining = Remaining();
        Assert.Contains("notes.txt", remaining);
        Assert.Contains("monitor_backup.log", remaining);
        Assert.Contains("monitor_not-a-date.log", remaining);
        Assert.Contains("other_2020-01-01.log", remaining);
    }

    [Fact]
    public void Retention_of_zero_or_less_disables_pruning()
    {
        var ancient = WriteLog(400);

        Assert.Equal(0, LogRetention.Prune(_dir, retentionDays: 0, today: Today));
        Assert.Equal(0, LogRetention.Prune(_dir, retentionDays: -5, today: Today));
        Assert.True(File.Exists(ancient));
    }

    [Fact]
    public void A_missing_directory_is_harmless()
    {
        // Startup ordering could call this before anything created the folder.
        var missing = Path.Combine(_dir, "nope");

        Assert.Equal(0, LogRetention.Prune(missing, retentionDays: 30, today: Today));
    }

    [Fact]
    public void An_empty_directory_is_harmless()
    {
        Assert.Equal(0, LogRetention.Prune(_dir, retentionDays: 30, today: Today));
    }

    [Fact]
    public void The_real_backlog_shape_prunes_to_the_window()
    {
        // Models the directory that motivated this: 44 daily files, ~6 weeks, nothing deleted.
        for (var i = 0; i < 44; i++) WriteLog(i);

        var deleted = LogRetention.Prune(_dir, retentionDays: 30, today: Today);

        Assert.Equal(13, deleted);            // days 31..43
        Assert.Equal(31, Remaining().Length); // days 0..30 inclusive
    }
}
