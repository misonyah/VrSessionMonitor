using Microsoft.Win32;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Registers/unregisters this app in the per-user Windows startup Run key. User-level (no admin
/// needed, no Task Scheduler), matching how most small tray utilities offer a "start with
/// Windows" toggle.
/// </summary>
public static class WindowsStartup
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VrSessionMonitor";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Log.Debug("WindowsStartup", $"Reading Run key threw: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Log.Warn("WindowsStartup", "Could not resolve this process's own exe path — cannot register for startup.");
                    return;
                }
                key.SetValue(ValueName, $"\"{exePath}\"");
                Log.Info("WindowsStartup", $"Registered to start with Windows ({exePath}).");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("WindowsStartup", "Removed from Windows startup.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("WindowsStartup", $"Failed to {(enabled ? "register" : "unregister")} Windows startup entry", ex);
        }
    }
}
