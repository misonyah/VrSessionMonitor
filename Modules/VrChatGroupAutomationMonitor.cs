using System.Text.RegularExpressions;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

// Gated the same way VrChatOscAfkListener is (see that file's doc) - LucHeart.CoreOSC/
// VRChat.OSCQuery are only referenced when INCLUDE_HOME_ASSISTANT is defined in the csproj. The
// name is a misnomer for this feature (nothing here is Home-Assistant-specific), but splitting
// that build flag into a separate "include OSC" one is out of scope for this change - the flag
// is already always on in this build.
#if INCLUDE_HOME_ASSISTANT
using System.Net;
using LucHeart.CoreOSC;

/// <summary>
/// Tails VRChat's own log file for the group ID VRChat embeds directly in an instance's join
/// line (e.g. "...~group(grp_xxxxx)~groupAccessType(members)") and toggles a configured avatar
/// OSC bool parameter true while you're in a matching group's instance, false when you leave it
/// or move to a different (non-watched, or no) instance. No VRChat API login needed - the same
/// instance-location string VRCX itself parses for this already carries the group ID; confirmed
/// against VRCX's own Dotnet/LogWatcher.cs (ParseLogLocation's "[Behaviour] Joining " handling)
/// and src/shared/utils/instance.js (buildLegacyInstanceTag's ~group()/~groupAccessType() tags).
/// </summary>
public sealed class VrChatGroupAutomationMonitor : IDisposable
{
    private readonly MonitorConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private OscSender? _osc;

    private string? _logFilePath;
    private long _logPosition;
    private string? _activeGroupId;

    private VrcRepresentClient? _represent;
    private string? _lastRepresentedGroupId;   // in-memory cache of current representation
    private bool _representSeeded;

    private bool RepresentConfigured =>
        !string.IsNullOrWhiteSpace(_config.VrChatGroupAutomation.FallbackRepresentGroupId)
        || _config.VrChatGroupAutomation.Groups.Any(g => g.Represent);

    // "[Behaviour] Joining wrld_xxx:12345~group(grp_yyy)~groupAccessType(members)" - excludes the
    // two other "[Behaviour] Joining ..." lines VRChat logs that aren't actual instance joins
    // ("Joining or Creating Room: <world name>" and "Joining friend: <name>"), matching VRCX's
    // own exclusion list for the same line prefix.
    private static readonly Regex JoinLineRegex = new(@"\[Behaviour\] Joining (?!or Creating Room:|friend:)(\S+)", RegexOptions.Compiled);
    private static readonly Regex GroupIdRegex = new(@"~group\(([^)]+)\)", RegexOptions.Compiled);

    public VrChatGroupAutomationMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        if (!_config.VrChatGroupAutomation.Enabled || _config.VrChatGroupAutomation.Groups.Count == 0)
        {
            Log.Info("VrChatGroupAutomation", "Disabled or no groups configured — not starting.");
            return;
        }

        // VRChat's conventional default OSC receive port - same fallback VrChatOscAfkListener's
        // own send-endpoint uses (see that file's doc). Not resolved via OSCQuery discovery; if
        // multiple local OSC routers ever actually reassign VRChat's real receive port on this
        // machine, this would need to move to dynamic discovery instead.
        _osc = new OscSender(new IPEndPoint(IPAddress.Loopback, 9000));

        if (RepresentConfigured)
        {
            _represent = new VrcRepresentClient(new VrcxSessionProvider());
            if (!_represent.HasSession)
                Log.Warn("VrChatGroupAutomation", "Represent is configured but no VRCX session was found — represent will be skipped until VRCX is logged in.");
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("VrChatGroupAutomation", $"Started, watching {_config.VrChatGroupAutomation.Groups.Count} group(s).");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                PollLog();
            }
            catch (Exception ex)
            {
                Log.Debug("VrChatGroupAutomation", $"Log poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private void PollLog()
    {
        // Same location .NET's own VRChat client (and VRCX) uses:
        // %LOCALAPPDATA%Low\VRChat\VRChat\output_log_*.txt - confirmed against VRCX's
        // AppApiCef.GetVRChatAppDataLocation().
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"Low\VRChat\VRChat";
        if (!Directory.Exists(dir)) return;

        var newest = new DirectoryInfo(dir).GetFiles("output_log_*.txt")
            .OrderByDescending(f => f.CreationTimeUtc)
            .FirstOrDefault();
        if (newest is null) return;

        if (_logFilePath != newest.FullName)
        {
            // Switched to a new/different log file (VRChat (re)started) - start reading from its
            // current end, not the beginning. We only care about live join/leave events from now
            // on, not replaying that session's whole history.
            _logFilePath = newest.FullName;
            _logPosition = newest.Length;
            Log.Debug("VrChatGroupAutomation", $"Watching log file: {newest.FullName}");
            return;
        }

        if (newest.Length < _logPosition)
        {
            // File got shorter than our last read position - VRChat truncated/rotated it under
            // us. Reset to the current end rather than throwing on a negative seek.
            _logPosition = newest.Length;
            return;
        }
        if (newest.Length == _logPosition) return; // no new data

        using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = _logPosition;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ProcessLine(line);
        }
        _logPosition = stream.Position;
    }

    private void ProcessLine(string line)
    {
        if (line.Contains("[Behaviour] OnLeftRoom") || line.Contains("[Behaviour] Successfully left room"))
        {
            SetActiveGroup(null);
            return;
        }

        var match = JoinLineRegex.Match(line);
        if (!match.Success) return;

        var location = match.Groups[1].Value;
        var groupMatch = GroupIdRegex.Match(location);
        SetActiveGroup(groupMatch.Success ? groupMatch.Groups[1].Value : null);
    }

    private void SetActiveGroup(string? groupId)
    {
        if (groupId == _activeGroupId) return;

        // Clear the previous group's param first (if it was one we're watching) before setting
        // the new one - handles moving directly from one watched group's instance into another's
        // without an intervening "no group" state.
        if (_activeGroupId is not null)
        {
            var previous = _config.VrChatGroupAutomation.Groups.FirstOrDefault(g => g.GroupId == _activeGroupId);
            if (previous is not null) SendParam(previous.ParamName, false);
        }

        _activeGroupId = groupId;

        if (groupId is not null)
        {
            var entry = _config.VrChatGroupAutomation.Groups.FirstOrDefault(g => g.GroupId == groupId);
            if (entry is not null)
            {
                Log.Info("VrChatGroupAutomation", $"Entered watched group '{entry.DisplayName}' ({groupId}) — setting {entry.ParamName} = true.");
                SendParam(entry.ParamName, true);
            }
        }

        _ = ReconcileRepresentAsync(groupId);
    }

    private async Task ReconcileRepresentAsync(string? activeGroupId)
    {
        if (_represent is null || !_represent.HasSession) return;
        try
        {
            var desired = RepresentPolicy.ComputeDesiredRepresentedGroupId(
                activeGroupId, _config.VrChatGroupAutomation.Groups, _config.VrChatGroupAutomation.FallbackRepresentGroupId);

            if (!_representSeeded)
            {
                _lastRepresentedGroupId = await _represent.GetRepresentedGroupIdAsync().ConfigureAwait(false);
                _representSeeded = true;
            }

            if (string.Equals(desired, _lastRepresentedGroupId, StringComparison.Ordinal)) return; // already correct

            if (desired is not null)
            {
                if (await _represent.SetRepresentedAsync(desired, true).ConfigureAwait(false))
                {
                    Log.Info("VrChatGroupAutomation", $"Represented group set to {desired}.");
                    _lastRepresentedGroupId = desired;
                }
            }
            else if (_lastRepresentedGroupId is not null)
            {
                // clear: un-represent whatever is currently represented
                if (await _represent.SetRepresentedAsync(_lastRepresentedGroupId, false).ConfigureAwait(false))
                {
                    Log.Info("VrChatGroupAutomation", "Cleared represented group.");
                    _lastRepresentedGroupId = null;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("VrChatGroupAutomation", $"Represent reconcile failed: {ex.Message}");
        }
    }

    private async void SendParam(string paramName, bool value)
    {
        if (string.IsNullOrWhiteSpace(paramName) || _osc is null) return;
        try
        {
            await _osc.SendAsync(new OscMessage($"/avatar/parameters/{paramName}", new object[] { value })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("VrChatGroupAutomation", $"Failed to send OSC param {paramName}={value}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _osc?.Dispose();
        _represent?.Dispose();
    }
}
#endif
