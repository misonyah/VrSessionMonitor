using System.Text.RegularExpressions;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Frame;

public interface IHotspotKeeper
{
    /// <summary>Starts the keeper scheduled task and waits until it is no longer Running (or timeout).
    /// The task itself runs elevated; starting it needs no UAC prompt.</summary>
    Task TriggerAndWaitAsync(string taskName, TimeSpan timeout);
    /// <summary>The keeper log's last line without its timestamp, or a short reason it can't be read.</summary>
    string ReadStatus(string logPath);
}

/// <summary>Starting and observing a Windows scheduled task. Seam over the Task Scheduler so the
/// wait logic is testable; the real one uses the COM API because schtasks.exe's text output is
/// localized and prints one row per trigger.</summary>
public interface IScheduledTaskApi
{
    bool TryRun(string taskName, out string error);
    bool IsRunning(string taskName);
}

/// <summary>Task Scheduler 2.0 COM ("Schedule.Service"), locale-independent. A task owned by the
/// current user can be started without elevation even when it runs with highest privileges.</summary>
public sealed class ComScheduledTaskApi : IScheduledTaskApi
{
    private const int TaskStateRunning = 4;

    private static dynamic GetTask(string taskName)
    {
        dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        svc.Connect();
        dynamic root = svc.GetFolder("\\");
        return root.GetTask(taskName);
    }

    public bool TryRun(string taskName, out string error)
    {
        try { GetTask(taskName).Run(null); error = ""; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public bool IsRunning(string taskName)
    {
        try { return (int)GetTask(taskName).State == TaskStateRunning; }
        catch { return false; }
    }
}

/// <summary>Drives the external "hotspot keeper" scheduled task (a script that keeps the PC's
/// Mobile Hotspot up on the right Wi-Fi card) and reads its log's last line as its status.</summary>
public sealed partial class HotspotKeeperClient : IHotspotKeeper
{
    private readonly IScheduledTaskApi _tasks;
    private readonly TimeSpan _pollInterval;

    public HotspotKeeperClient(IScheduledTaskApi? tasks = null, TimeSpan? pollInterval = null)
    {
        _tasks = tasks ?? new ComScheduledTaskApi();
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public async Task TriggerAndWaitAsync(string taskName, TimeSpan timeout)
    {
        if (!_tasks.TryRun(taskName, out var error))
        {
            Log.Warn("HotspotKeeper", $"Starting scheduled task '{taskName}' failed: {error}");
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!_tasks.IsRunning(taskName)) return;
            await Task.Delay(_pollInterval).ConfigureAwait(false);
        }
        Log.Warn("HotspotKeeper", $"'{taskName}' still running after {timeout.TotalSeconds:F0}s — continuing without waiting.");
    }

    public string ReadStatus(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return "keeper log missing";
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var line = LastLine(sr.ReadToEnd());
            return line is null ? "keeper log empty" : TimestampPrefix().Replace(line, "");
        }
        catch (Exception ex) { return $"keeper log unreadable: {ex.Message}"; }
    }

    public static string? LastLine(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.Trim().Length > 0);

    public static bool IsOk(string status) => status.Contains("OK - ", StringComparison.Ordinal);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} ")] private static partial Regex TimestampPrefix();
}
