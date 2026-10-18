using System.Diagnostics;
using Microsoft.Win32;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>Whether the Steam client is running and logged in. Steam writes the logged-in account to
/// HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser once login completes, and 0 before that.</summary>
public static class SteamClient
{
    public static bool IsLoggedIn()
    {
        try
        {
            if (Process.GetProcessesByName("steam").Length == 0) return false;
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int user && user != 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// Sends steam://rungameid requests reliably. Steam silently drops a request that arrives while it is
/// still starting up or logging in (2026-10-17: SteamVR auto-started Steam, and the requests for two
/// overlays sent a second later never launched anything). So: wait until Steam is logged in, send,
/// check the app actually appeared, and send once more if it didn't.
/// </summary>
public sealed class SteamUriLauncher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<bool> _steamLoggedIn;
    private readonly Action<string> _launchUri;
    private readonly Func<string, bool> _isRunning;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _verifyAfter;

    public SteamUriLauncher(Func<bool> steamLoggedIn, Action<string> launchUri, Func<string, bool> isRunning,
        Func<TimeSpan, Task>? delay = null, TimeSpan? readyTimeout = null, TimeSpan? verifyAfter = null)
    {
        _steamLoggedIn = steamLoggedIn;
        _launchUri = launchUri;
        _isRunning = isRunning;
        _delay = delay ?? Task.Delay;
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(90);
        _verifyAfter = verifyAfter ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>The production instance: real Steam login check and shell launch.</summary>
    public static SteamUriLauncher CreateDefault(Func<string, bool> isRunning) =>
        new(SteamClient.IsLoggedIn, uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }), isRunning);

    /// <summary>True once Steam is logged in; false if it isn't within the timeout.</summary>
    public async Task<bool> WaitUntilReadyAsync(string label)
    {
        var waited = TimeSpan.Zero;
        var logged = false;
        while (!_steamLoggedIn())
        {
            if (waited >= _readyTimeout)
            {
                Log.Warn("SteamLaunch", $"Steam still isn't logged in after {_readyTimeout.TotalSeconds:0}s — not starting {label}.");
                return false;
            }
            if (!logged) { Log.Info("SteamLaunch", $"Waiting for Steam to finish logging in before starting {label}."); logged = true; }
            await _delay(PollInterval).ConfigureAwait(false);
            waited += PollInterval;
        }
        return true;
    }

    /// <summary>Waits for Steam, sends steam://rungameid/&lt;appId&gt;, and — when a process name is
    /// known — verifies it appeared, retrying once. True when the app is (believed) started.</summary>
    public async Task<bool> LaunchAsync(string appId, string? processName, string label)
    {
        if (!await WaitUntilReadyAsync(label).ConfigureAwait(false)) return false;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { _launchUri($"steam://rungameid/{appId}"); }
            catch (Exception ex)
            {
                Log.Error("SteamLaunch", $"Failed to send steam://rungameid/{appId} for {label}", ex);
                return false;
            }

            if (string.IsNullOrWhiteSpace(processName)) return true;   // nothing to verify against

            await _delay(_verifyAfter).ConfigureAwait(false);
            if (_isRunning(processName)) return true;

            if (attempt == 1)
                Log.Warn("SteamLaunch", $"{label} didn't start within {_verifyAfter.TotalSeconds:0}s of the Steam request — asking Steam once more.");
        }

        Log.Warn("SteamLaunch", $"{label} still isn't running after two Steam launch requests — giving up.");
        return false;
    }
}
