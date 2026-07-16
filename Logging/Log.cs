using System.Collections.Concurrent;

namespace VrSessionMonitor.Logging;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>
/// Deliberately verbose for now (per request) — every module logs Trace/Debug freely.
/// Trim levels/noise once the flow is proven reliable.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new();
    private static StreamWriter? _writer;
    private static Thread? _worker;
    private static string _logDir = "";
    private static string _currentPath = "";
    private static readonly object FileLock = new();

    public static LogLevel MinLevel { get; set; } = LogLevel.Trace;

    public static void Init(string logDirectory)
    {
        _logDir = logDirectory;
        Directory.CreateDirectory(_logDir);
        OpenWriterForToday();

        _worker = new Thread(ProcessQueue) { IsBackground = true, Name = "LogWriter" };
        _worker.Start();

        Info("Log", $"Logging started. Directory={_logDir}");
    }

    private static void OpenWriterForToday()
    {
        lock (FileLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _currentPath = Path.Combine(_logDir, $"monitor_{DateTime.Now:yyyy-MM-dd}.log");
            _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
    }

    private static void ProcessQueue()
    {
        var lastDay = DateTime.Now.Day;
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            if (DateTime.Now.Day != lastDay)
            {
                OpenWriterForToday();
                lastDay = DateTime.Now.Day;
            }

            lock (FileLock)
            {
                _writer?.WriteLine(line);
            }
        }
    }

    public static void Trace(string module, string message) => Write(LogLevel.Trace, module, message);
    public static void Debug(string module, string message) => Write(LogLevel.Debug, module, message);
    public static void Info(string module, string message) => Write(LogLevel.Info, module, message);
    public static void Warn(string module, string message) => Write(LogLevel.Warn, module, message);
    public static void Error(string module, string message) => Write(LogLevel.Error, module, message);
    public static void Error(string module, string message, Exception ex) =>
        Write(LogLevel.Error, module, $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(LogLevel level, string module, string message)
    {
        if (level < MinLevel) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{module,-18}] {message}";

        // Mirror to console when one is attached (useful during dev runs).
        try { Console.WriteLine(line); } catch { /* no console */ }

        Queue.Add(line);
    }

    public static string CurrentLogFilePath => _currentPath;
    public static string LogDirectory => _logDir;

    public static void Shutdown()
    {
        Queue.CompleteAdding();
        _worker?.Join(2000);
        lock (FileLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
    }
}
