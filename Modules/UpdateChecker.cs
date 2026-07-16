using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

public sealed class UpdateFinding
{
    public string Component { get; init; } = "";
    public string LocalVersion { get; init; } = "unknown";
    public string? LatestKnownVersion { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// Check-and-notify ONLY — never installs anything. Launch always proceeds regardless of what
/// this finds; results are just logged (and can be surfaced in the tray tooltip/menu).
///
/// GitHub-hosted projects (SlimeVR, VRCFaceTracking) get a real "latest release" comparison.
/// VRChat (Steam-updated) and Virtual Desktop Streamer (no public version API) only get a local
/// file-version/last-write snapshot logged — there's no reliable free API to diff against, so
/// this deliberately doesn't pretend to know if they're current.
/// </summary>
public sealed class UpdateChecker
{
    private readonly MonitorConfig _config;
    private readonly HttpClient _http;

    public UpdateChecker(MonitorConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VrSessionMonitor", "1.0"));
    }

    public async Task<List<UpdateFinding>> RunAllAsync()
    {
        var findings = new List<UpdateFinding>();
        if (!_config.Updates.Enabled)
        {
            Log.Info("UpdateChecker", "Update checking disabled in config, skipping.");
            return findings;
        }

        Log.Info("UpdateChecker", "Running update checks (notify-only, non-blocking)...");

        if (_config.Updates.CheckVrChat)
            findings.Add(CheckLocalFileOnly("VRChat", _config.Paths.VrChatLaunchExe, "Steam auto-updates on launch; no separate check needed."));

        if (_config.Updates.CheckVirtualDesktopStreamer)
            findings.Add(CheckLocalFileOnly("Virtual Desktop Streamer", _config.Paths.VirtualDesktopStreamerExe, "No public update-check API; verify manually via the app's built-in updater."));

        if (_config.Updates.CheckSlimeVr)
            findings.Add(await CheckGithubAsync("SlimeVR Server", _config.Paths.SlimeVrExe, _config.Updates.SlimeVrGithubRepo).ConfigureAwait(false));

        if (_config.Updates.CheckVrcFaceTracking)
            findings.Add(await CheckGithubAsync("VRCFaceTracking", _config.Paths.VrcFaceTrackingExe, _config.Updates.VrcFaceTrackingGithubRepo).ConfigureAwait(false));

        foreach (var f in findings)
            Log.Info("UpdateChecker", $"{f.Component}: local={f.LocalVersion} latest={f.LatestKnownVersion ?? "n/a"} :: {f.Note}");

        return findings;
    }

    private static UpdateFinding CheckLocalFileOnly(string component, string exePath, string note)
    {
        if (!File.Exists(exePath))
        {
            Log.Warn("UpdateChecker", $"{component}: executable not found at {exePath}, cannot read version.");
            return new UpdateFinding { Component = component, LocalVersion = "not found", Note = note };
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var version = info.FileVersion ?? info.ProductVersion ?? "unknown";
            var lastWrite = File.GetLastWriteTime(exePath);
            return new UpdateFinding
            {
                Component = component,
                LocalVersion = $"{version} (file dated {lastWrite:yyyy-MM-dd})",
                Note = note,
            };
        }
        catch (Exception ex)
        {
            Log.Debug("UpdateChecker", $"{component}: FileVersionInfo read failed: {ex.Message}");
            return new UpdateFinding { Component = component, LocalVersion = "unreadable", Note = note };
        }
    }

    private async Task<UpdateFinding> CheckGithubAsync(string component, string exePath, string repo)
    {
        string localVersion = "unknown";
        if (File.Exists(exePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                localVersion = info.FileVersion ?? info.ProductVersion ?? "unknown";
            }
            catch (Exception ex)
            {
                Log.Debug("UpdateChecker", $"{component}: FileVersionInfo read failed: {ex.Message}");
            }
        }
        else
        {
            Log.Debug("UpdateChecker", $"{component}: executable not found at {exePath}, will still check latest release.");
        }

        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            Log.Trace("UpdateChecker", $"{component}: GET {url}");
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn("UpdateChecker", $"{component}: GitHub release check failed with {(int)resp.StatusCode} {resp.StatusCode}");
                return new UpdateFinding { Component = component, LocalVersion = localVersion, Note = $"GitHub check failed ({resp.StatusCode})" };
            }

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;

            var note = tag is not null && localVersion != "unknown" && !localVersion.Contains(tag.TrimStart('v'), StringComparison.OrdinalIgnoreCase)
                ? "Local version string doesn't match latest tag — worth a manual look (version formats may just differ)."
                : "Looks current (or comparison inconclusive from version strings alone).";

            return new UpdateFinding { Component = component, LocalVersion = localVersion, LatestKnownVersion = tag, Note = note };
        }
        catch (Exception ex)
        {
            Log.Warn("UpdateChecker", $"{component}: GitHub release check threw: {ex.Message}");
            return new UpdateFinding { Component = component, LocalVersion = localVersion, Note = $"Check failed: {ex.Message}" };
        }
    }
}
