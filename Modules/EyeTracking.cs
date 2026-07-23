using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Automation;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

// Everything Baballonia ("Project Babble") + eye-camera related lives in this one file:
// detection (ping + real streaming state), auto-restart/auto-close automation, and the
// pre-launch trigger that starts Baballonia once a camera is detected. Consolidated here
// 2026-07-16 (previously split across PeripheralMonitor.cs and two separate files) since it had
// grown into its own cohesive subsystem with a lot of hard-won, non-obvious behavior — see the
// history notes on EyeTrackingMonitor below before changing any of the detection logic.

public sealed class EyeCameraStatus
{
    public required EyeCameraConfig Camera { get; init; }
    /// <summary>ICMP reachable — camera is powered and on the network. This is the fast,
    /// authoritative "is it really there" signal — always trust this over Streaming.</summary>
    public bool Online { get; set; }
    /// <summary>Baballonia.Desktop.exe holds an ESTABLISHED TCP connection to this camera's
    /// HTTP port. Meaningful when Online is also true; when Online is false this can still read
    /// true for a long time (TCP has no way to notice a dead peer without traffic/keepalives —
    /// confirmed live 2026-07-16: stayed true 40+ seconds through an actual power-loss test), so
    /// never trust Streaming alone to mean "everything is fine".</summary>
    public bool Streaming { get; set; }
}

/// <summary>
/// Drives Baballonia's own UI via Windows UI Automation to click its Stop Camera / Start Camera /
/// Close buttons — there's no exposed API (confirmed via netstat: the app only makes outbound
/// connections, never listens on a local port), so this is the only automation path available.
///
/// The three camera sections (Left Eye, Right Eye, Face) share IDENTICALLY NAMED buttons with no
/// unique AutomationId — Avalonia's accessibility tree just exposes "Start Camera"/"Stop Camera"
/// three times over. Scoping to the right section works by walking the flat descendant list in
/// document order: each section starts with a Text element exactly matching its
/// BaballoniaSectionLabel (e.g. "Left Eye Camera"), and that section's Start/Stop buttons appear
/// before the next section's label. Confirmed live against the actual running app on 2026-07-16.
///
/// All calls are serialized via <see cref="_lock"/>. Confirmed live 2026-07-16: two concurrent
/// restarts (left + right camera both power-cycled together, each on its own Task.Run) hit
/// Windows UI Automation against the same window from two threads simultaneously, and one of
/// them threw a raw COM-level exception out of InvokePattern.Invoke(). UI Automation isn't safe
/// for concurrent access like that — calls now queue up and run one at a time instead.
/// </summary>
public sealed class BaballoniaAutomation
{
    private static readonly string[] KnownSectionLabels = { "Left Eye Camera", "Right Eye Camera", "Face Camera" };
    private readonly object _lock = new();

    /// <summary>Clicks Stop Camera then Start Camera for the named section. Never throws —
    /// returns false and logs on any failure (process not found, window not found, buttons not
    /// found, automation call failed). Blocks if another automation call is already in progress.</summary>
    public bool TryRestartCamera(string sectionLabel, int stopToStartDelayMs)
    {
        lock (_lock)
        {
            try
            {
                var window = FindWindowOrLog();
                if (window is null) return false;

                var (stopButton, startButton) = FindCameraButtons(window, sectionLabel);
                if (stopButton is null || startButton is null)
                {
                    Log.Warn("BaballoniaAutomation", $"Could not find Stop/Start Camera buttons for section '{sectionLabel}' — Baballonia's UI may have changed.");
                    return false;
                }

                Log.Info("BaballoniaAutomation", $"Clicking Stop Camera for '{sectionLabel}'...");
                Invoke(stopButton);

                Thread.Sleep(stopToStartDelayMs);

                Log.Info("BaballoniaAutomation", $"Clicking Start Camera for '{sectionLabel}'...");
                Invoke(startButton);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("BaballoniaAutomation", $"Camera restart for '{sectionLabel}' threw", ex);
                return false;
            }
        }
    }

    /// <summary>Clicks Baballonia's own title-bar Close button (a graceful close, not a kill).
    /// Returns true if already not running (nothing to do) or if the click was sent.</summary>
    public bool TryCloseWindow()
    {
        lock (_lock)
        {
            try
            {
                var proc = Process.GetProcessesByName("Baballonia.Desktop").FirstOrDefault();
                if (proc is null)
                {
                    Log.Debug("BaballoniaAutomation", "Baballonia.Desktop not running — nothing to close.");
                    return true;
                }

                var window = FindMainWindow(proc.Id);
                if (window is null)
                {
                    Log.Warn("BaballoniaAutomation", "Could not find Baballonia's main window to close it.");
                    return false;
                }

                var closeCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_CloseButton");
                var closeButton = window.FindFirst(TreeScope.Descendants, closeCondition);
                if (closeButton is null)
                {
                    Log.Warn("BaballoniaAutomation", "Could not find Baballonia's Close button — UI may have changed.");
                    return false;
                }

                Log.Info("BaballoniaAutomation", "Clicking Close on Baballonia's window...");
                Invoke(closeButton);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("BaballoniaAutomation", "Closing Baballonia threw", ex);
                return false;
            }
        }
    }

    /// <summary>Reads the current value of a camera section's address field (e.g. "192.168.1.50")
    /// for auto-detection purposes, rather than clicking anything. UNVERIFIED as of 2026-07-17 —
    /// written while Baballonia wasn't running, based only on the "Left/Right Camera Address"
    /// textboxes referenced in EyeTrackingMonitor's class history notes from the original
    /// 2026-07-16 cross-check. Assumes the address field is exposed as the first
    /// ControlType.Edit element within the section (same section-scoping walk as
    /// FindCameraButtons); if Baballonia's UI doesn't match that shape, this needs adjusting
    /// once actually run against a live instance.</summary>
    public string? TryReadCameraAddress(string sectionLabel)
    {
        lock (_lock)
        {
            try
            {
                var window = FindWindowOrLog();
                if (window is null) return null;

                var field = FindCameraAddressField(window, sectionLabel);
                if (field is null)
                {
                    Log.Warn("BaballoniaAutomation", $"Could not find the camera address field for section '{sectionLabel}' — Baballonia's UI may differ from what was assumed (unverified live).");
                    return null;
                }

                if (field.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) && patternObj is ValuePattern valuePattern)
                    return valuePattern.Current.Value;

                return field.Current.Name; // fallback if it's not ValuePattern-backed
            }
            catch (Exception ex)
            {
                Log.Error("BaballoniaAutomation", $"Reading camera address for '{sectionLabel}' threw", ex);
                return null;
            }
        }
    }

    private static AutomationElement? FindCameraAddressField(AutomationElement window, string sectionLabel)
    {
        var relevantCondition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        var all = window.FindAll(TreeScope.Descendants, relevantCondition);

        bool inTargetSection = false;
        foreach (AutomationElement el in all)
        {
            string name;
            ControlType controlType;
            try
            {
                name = el.Current.Name;
                controlType = el.Current.ControlType;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            if (controlType == ControlType.Text && KnownSectionLabels.Contains(name))
            {
                inTargetSection = string.Equals(name, sectionLabel, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inTargetSection) continue;

            if (controlType == ControlType.Edit)
                return el;
        }

        return null;
    }

    private static AutomationElement? FindWindowOrLog()
    {
        var proc = Process.GetProcessesByName("Baballonia.Desktop").FirstOrDefault();
        if (proc is null)
        {
            Log.Warn("BaballoniaAutomation", "Baballonia.Desktop process not found.");
            return null;
        }

        var window = FindMainWindow(proc.Id);
        if (window is null)
            Log.Warn("BaballoniaAutomation", "Could not find Baballonia's main window via UI Automation.");
        return window;
    }

    private static AutomationElement? FindMainWindow(int pid)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
    }

    private static (AutomationElement? Stop, AutomationElement? Start) FindCameraButtons(AutomationElement window, string sectionLabel)
    {
        var relevantCondition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        var all = window.FindAll(TreeScope.Descendants, relevantCondition);

        bool inTargetSection = false;
        AutomationElement? stopButton = null;
        AutomationElement? startButton = null;

        foreach (AutomationElement el in all)
        {
            string name;
            ControlType controlType;
            try
            {
                name = el.Current.Name;
                controlType = el.Current.ControlType;
            }
            catch (ElementNotAvailableException)
            {
                continue; // element went away mid-scan (e.g. UI updating); skip it
            }

            if (controlType == ControlType.Text && KnownSectionLabels.Contains(name))
            {
                inTargetSection = string.Equals(name, sectionLabel, StringComparison.OrdinalIgnoreCase);
                if (!inTargetSection)
                {
                    stopButton = null;
                    startButton = null;
                }
                continue;
            }

            if (!inTargetSection || controlType != ControlType.Button) continue;

            if (string.Equals(name, "Start Camera", StringComparison.OrdinalIgnoreCase))
                startButton = el;
            else if (string.Equals(name, "Stop Camera", StringComparison.OrdinalIgnoreCase))
                stopButton = el;

            if (stopButton is not null && startButton is not null) break;
        }

        return (stopButton, startButton);
    }

    private static void Invoke(AutomationElement element)
    {
        var pattern = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);
        pattern.Invoke();
    }
}

/// <summary>
/// Pings the eye camera IPs at a fast interval (default 1s — much faster than
/// EyeTrackingMonitor's normal 5s cadence) purely to catch "cameras just powered on" quickly,
/// and auto-launches Baballonia the moment either camera responds. Only does anything while
/// Baballonia isn't already running — once it's up, EyeTrackingMonitor's normal checks (and
/// auto-restart/auto-close) take over, so this stays out of the way rather than double-pinging.
/// </summary>
public sealed class BaballoniaLaunchTrigger : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly ProcessLauncher _launcher = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public BaballoniaLaunchTrigger(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("BaballoniaLaunch", $"Started. Pinging eye cameras every {_config.Polling.EyeCameraPreLaunchPingIntervalMs}ms while Baballonia isn't running.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("BaballoniaLaunch", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("BaballoniaLaunch", $"Check cycle threw: {ex.Message}");
            }

            try { await Task.Delay(_config.Polling.EyeCameraPreLaunchPingIntervalMs, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task CheckOnceAsync()
    {
        if (!_config.BaballoniaLifecycle.Enabled)
            return;

        if (ProcessLauncher.IsRunning("Baballonia.Desktop"))
            return; // already up — EyeTrackingMonitor owns monitoring/restart from here

        if (_config.EyeCameras.Count == 0)
            return;

        foreach (var cam in _config.EyeCameras)
        {
            bool online;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(cam.Ip, 300).ConfigureAwait(false);
                online = reply.Status == IPStatus.Success;
            }
            catch (Exception ex)
            {
                online = false;
                Log.Debug("BaballoniaLaunch", $"Ping {cam.Ip} ({cam.Name}) threw: {ex.Message}");
            }

            if (!online) continue;

            Log.Info("BaballoniaLaunch", $"Eye camera '{cam.Name}' ({cam.Ip}) detected online and Baballonia isn't running — launching it.");
            var result = await _launcher.EnsureRunningAsync(
                "Baballonia.Desktop", _config.Paths.BaballoniaExe, null,
                _config.Polling.ProcessLaunchTimeoutMs, _config.Polling.ProcessPollIntervalMs).ConfigureAwait(false);

            if (!result.Success)
                Log.Warn("BaballoniaLaunch", $"Baballonia launch did not confirm success: {result.Error}");

            return; // one camera was enough to trigger the launch; no need to ping the other this cycle
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// Detects real eye-camera liveness and drives Baballonia accordingly (auto-restart a stuck
/// camera, auto-close Baballonia if every camera is genuinely gone). Owns a
/// <see cref="BaballoniaLaunchTrigger"/> internally so callers only need to manage one object for
/// the whole eye-tracking lifecycle: not running -> detect cameras -> launch -> monitor -> restart
/// stuck cameras -> close if all cameras vanish -> back to "not running", loop closed.
///
/// History (2026-07-16 live testing), because the "obvious" signals kept being wrong:
///  - A standalone "eyetrackapp.exe" process (matching the old et.cmd flow) doesn't exist even
///    while eye tracking is genuinely working — the pipeline is Baballonia (cameras) -> UDP
///    127.0.0.1:8888 -> a VRCFaceTracking.ModuleProcess.exe child (see FaceTracking.cs for the
///    CPU-activity and IO-bytes heuristics that were tried against that process and BOTH
///    disproven by live testing — not repeated here since this file is about the cameras
///    directly, which turned out to be the right level to check at all along).
///  - What actually worked: netstat showed Baballonia.Desktop.exe holding ESTABLISHED TCP
///    connections to the camera IPs on port 80 (both Espressif/ESP32-class devices per MAC
///    lookup) while eye tracking was confirmed working — cross-checked directly against
///    Baballonia's own "Left/Right Camera Address" text boxes via UI Automation. This mirrors the
///    same ESTABLISHED-connection-to-known-remote-endpoint technique SessionOrchestrator uses to
///    confirm the real VD stream (as opposed to a false-positive port match).
///  - BUT that TCP state lies on power loss: confirmed live, online=False streaming=True
///    persisted 40+ seconds through an actual unplug test, because TCP has no way to notice a
///    dead peer without traffic/keepalives. Online (ping) is the only signal that's ever
///    authoritative for "is the camera really there" — Streaming is only meaningful once Online
///    is confirmed true, and even then only for "is Baballonia's app-level link to it healthy".
///  - A camera coming back online after being unreachable can still show Streaming=true (stale)
///    with no transition to trigger the normal restart path, so that transition forces a restart
///    regardless of the streaming flag — and that forced restart bypasses the per-camera cooldown
///    too: confirmed live, a camera that power-cycled 27s after an unrelated earlier restart got
///    skipped by its own cooldown without the bypass.
///  - That cooldown-bypassing forced restart then became its own bug: raw ICMP results had no
///    debounce, so a single dropped ping flipped Online false for one ~5s cycle and the next
///    successful ping looked exactly like a real power-cycle recovery. Confirmed live 2026-07-16
///    via logs: forced restarts fired every 16-22s repeatedly for cameras that were never
///    actually down, spamming Baballonia's UI automation for nothing. Fixed by requiring
///    PollingConfig.EyeCameraOfflineDebounceFailures consecutive failed pings before Online goes
///    false at all — see the consecutive-failure counter in CheckOnceAsync. Recovery (false ->
///    true) is intentionally NOT debounced the same way: only the false "went offline" direction
///    was ever the problem, and reacting to a real recovery instantly is harmless.
///  - Restarting two cameras at once must NOT run concurrently — see BaballoniaAutomation's lock.
/// </summary>
public sealed class EyeTrackingMonitor : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly BaballoniaAutomation _automation = new();
    private readonly BaballoniaLaunchTrigger _launchTrigger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private List<EyeCameraStatus> _last = new();

    private readonly Dictionary<string, DateTime> _lastRestartAttemptUtc = new();
    private readonly Dictionary<string, int> _consecutivePingFailures = new();
    private readonly HashSet<string> _everConfirmedOnline = new();
    private DateTime? _allCamerasOfflineSinceUtc;
    private bool _closedForCurrentOutage;

    public IReadOnlyList<EyeCameraStatus> Current => _last;

    /// <summary>Exposes the shared BaballoniaAutomation instance's address-reading for the
    /// tray's auto-detect action, so it stays serialized (via that instance's own lock) against
    /// this monitor's own restart/close calls rather than risking concurrent UI Automation
    /// access from a second, independent BaballoniaAutomation instance.</summary>
    public string? TryReadCameraAddress(string sectionLabel) => _automation.TryReadCameraAddress(sectionLabel);

    public EyeTrackingMonitor(MonitorConfig config)
    {
        _config = config;
        _launchTrigger = new BaballoniaLaunchTrigger(config);
    }

    public void Start()
    {
        _launchTrigger.Start();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("EyeTracking", "Started eye-camera + Baballonia monitoring.");
    }

    public void Stop()
    {
        _launchTrigger.Stop();
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("EyeTracking", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await CheckOnceAsync().ConfigureAwait(false);
            try { await Task.Delay(_config.Polling.ProcessPollIntervalMs * 5, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task<List<EyeCameraStatus>> CheckOnceAsync()
    {
        var results = new List<EyeCameraStatus>();
        if (_config.EyeCameras.Count == 0)
        {
            _last = results;
            return results;
        }

        HashSet<IPAddress> establishedRemotes;
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            establishedRemotes = props.GetActiveTcpConnections()
                .Where(c => c.State == TcpState.Established && c.RemoteEndPoint.Port == _config.Network.EyeCameraHttpPort)
                .Select(c => c.RemoteEndPoint.Address)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            Log.Debug("EyeTracking", $"GetActiveTcpConnections() threw: {ex.Message}");
            establishedRemotes = new HashSet<IPAddress>();
        }

        foreach (var cam in _config.EyeCameras)
        {
            bool pingSucceeded;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(cam.Ip, 500).ConfigureAwait(false);
                pingSucceeded = reply.Status == IPStatus.Success;
            }
            catch (Exception ex)
            {
                pingSucceeded = false;
                Log.Debug("EyeTracking", $"Ping {cam.Ip} ({cam.Name}) threw: {ex.Message}");
            }

            // Debounce online->offline only (see PollingConfig.EyeCameraOfflineDebounceFailures).
            // Recovery stays instant: any successful ping resets the counter and online=true.
            // The grace period only applies once a camera has been genuinely confirmed online at
            // least once — otherwise a monitor restart starts every camera's failure counter at 0
            // and a real, ongoing failure gets mistaken for "just flapped", misreporting online=true
            // for the first few checks after every restart (confirmed live 2026-07-24, mid-fix for
            // the same in-memory-state-vs-restart bug class as SessionOrchestrator's VD-PID gate).
            var failureThreshold = _config.Polling.EyeCameraOfflineDebounceFailures;
            var consecutiveFailures = _consecutivePingFailures.GetValueOrDefault(cam.Ip);
            consecutiveFailures = pingSucceeded ? 0 : consecutiveFailures + 1;
            _consecutivePingFailures[cam.Ip] = consecutiveFailures;

            if (pingSucceeded)
                _everConfirmedOnline.Add(cam.Ip);

            var online = pingSucceeded || (_everConfirmedOnline.Contains(cam.Ip) && consecutiveFailures < failureThreshold);

            if (!pingSucceeded && online)
                Log.Debug("EyeTracking", $"Eye camera '{cam.Name}' ({cam.Ip}): ping failed ({consecutiveFailures}/{failureThreshold} consecutive) — within debounce grace period, still counted online.");

            var streaming = IPAddress.TryParse(cam.Ip, out var ip) && establishedRemotes.Contains(ip);
            var status = new EyeCameraStatus { Camera = cam, Online = online, Streaming = streaming };
            results.Add(status);

            var previous = _last.FirstOrDefault(c => c.Camera.Ip == cam.Ip);
            var cameraJustCameBackOnline = false;
            if (previous is not null)
            {
                if (previous.Online != online)
                {
                    Log.Info("EyeTracking", $"Eye camera '{cam.Name}' ({cam.Ip}) is now {(online ? "reachable on the network" : "UNREACHABLE (powered off or disconnected?)")}.");
                    cameraJustCameBackOnline = online && !previous.Online;
                    if (!online)
                        SteamVrNotifier.TryNotify(_config, $"Eye camera '{cam.Name}' is unreachable");
                }
                if (previous.Streaming != streaming)
                {
                    if (streaming)
                    {
                        Log.Info("EyeTracking", $"Eye camera '{cam.Name}' ({cam.Ip}): Baballonia now has a live connection to it.");
                        SteamVrNotifier.TryNotify(_config, $"Eye camera '{cam.Name}' recovered");
                    }
                    else
                        Log.Warn("EyeTracking", $"Eye camera '{cam.Name}' ({cam.Ip}): Baballonia's connection to it dropped — {(online ? "camera is still on the network but not streaming to Baballonia" : "camera is also unreachable")}.");
                }
            }

            Log.Trace("EyeTracking", $"Eye camera '{cam.Name}' ({cam.Ip}): online={online} streaming={streaming}");

            if (!online) continue;

            if (!streaming)
            {
                MaybeAutoRestart(cam);
            }
            else if (cameraJustCameBackOnline)
            {
                Log.Info("EyeTracking", $"Eye camera '{cam.Name}' just came back online after being unreachable — forcing a restart in case Baballonia's connection is stale.");
                MaybeAutoRestart(cam, bypassCooldown: true);
            }
        }

        _last = results;
        MaybeAutoCloseBaballonia(results);
        return results;
    }

    /// <summary>Fires a Stop+Start Camera UI-automation attempt for a camera that's on the
    /// network but not streaming to Baballonia. Respects a per-camera cooldown so a
    /// persistently-dead camera doesn't get spam-clicked every poll cycle — UNLESS
    /// <paramref name="bypassCooldown"/> is set, which the offline-to-online transition uses:
    /// a fresh power-cycle is new evidence the connection needs resetting and shouldn't be
    /// blocked by an unrelated cooldown left over from a prior, different failure.</summary>
    private void MaybeAutoRestart(EyeCameraConfig cam, bool bypassCooldown = false)
    {
        if (!_config.EyeCameraAutoRestart.Enabled) return;
        if (string.IsNullOrEmpty(cam.BaballoniaSectionLabel))
        {
            Log.Debug("EyeTracking", $"Eye camera '{cam.Name}' has no BaballoniaSectionLabel configured, skipping auto-restart.");
            return;
        }

        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMilliseconds(_config.EyeCameraAutoRestart.CooldownMs);
        if (!bypassCooldown && _lastRestartAttemptUtc.TryGetValue(cam.Name, out var last) && now - last < cooldown)
        {
            Log.Trace("EyeTracking", $"Eye camera '{cam.Name}': auto-restart on cooldown ({(cooldown - (now - last)).TotalSeconds:F0}s remaining).");
            return;
        }

        _lastRestartAttemptUtc[cam.Name] = now;
        var delayMs = _config.EyeCameraAutoRestart.StopToStartDelayMs;
        var sectionLabel = cam.BaballoniaSectionLabel;
        var camName = cam.Name;

        Log.Info("EyeTracking", $"Eye camera '{camName}' is on the network but not streaming — attempting automatic Stop+Start Camera via Baballonia's UI.");
        SteamVrNotifier.TryNotify(_config, $"Restarting eye camera: {camName}");
        Task.Run(() =>
        {
            var ok = _automation.TryRestartCamera(sectionLabel, delayMs);
            Log.Info("EyeTracking", ok
                ? $"Eye camera '{camName}': auto-restart clicks sent, will confirm on next check(s)."
                : $"Eye camera '{camName}': auto-restart attempt failed (see BaballoniaAutomation log above).");
            if (!ok)
                SteamVrNotifier.TryNotify(_config, $"Eye camera '{camName}' restart failed");
        });
    }

    /// <summary>If every configured camera is genuinely unreachable (ping-confirmed) for long
    /// enough continuously, close Baballonia — there's nothing for it to do with no cameras
    /// present, and closing it lets BaballoniaLaunchTrigger cleanly relaunch it fresh once a
    /// camera actually comes back, rather than leaving it running indefinitely with a dead
    /// connection that our restart logic has already given up retrying.</summary>
    private void MaybeAutoCloseBaballonia(List<EyeCameraStatus> results)
    {
        if (!_config.BaballoniaLifecycle.Enabled) return;
        if (results.Count == 0) return;

        if (!ProcessLauncher.IsRunning("Baballonia.Desktop"))
        {
            _allCamerasOfflineSinceUtc = null;
            _closedForCurrentOutage = false;
            return;
        }

        if (results.Any(c => c.Online))
        {
            _allCamerasOfflineSinceUtc = null;
            _closedForCurrentOutage = false;
            return;
        }

        var now = DateTime.UtcNow;
        _allCamerasOfflineSinceUtc ??= now;

        if (_closedForCurrentOutage) return;

        var elapsed = now - _allCamerasOfflineSinceUtc.Value;
        var threshold = TimeSpan.FromMilliseconds(_config.EyeCameraAutoRestart.AutoCloseAfterAllOfflineMs);
        if (elapsed < threshold) return;

        _closedForCurrentOutage = true;
        Log.Warn("EyeTracking", $"All {results.Count} eye camera(s) have been unreachable for {elapsed.TotalSeconds:F0}s — closing Baballonia automatically. It'll relaunch once a camera comes back online.");
        SteamVrNotifier.TryNotify(_config, "Closing Baballonia — no eye cameras detected");
        Task.Run(() =>
        {
            var ok = _automation.TryCloseWindow();
            Log.Info("EyeTracking", ok ? "Baballonia close requested." : "Failed to close Baballonia (see BaballoniaAutomation log above).");
        });
    }

    /// <summary>Surfaces per-camera restart cooldowns and the Baballonia auto-close countdown for
    /// the tray — previously only visible at Trace log level, which made an auto-close look like
    /// it came out of nowhere unless you were watching the log at the exact moment.</summary>
    public IReadOnlyList<string> DescribePendingActions()
    {
        var pending = new List<string>();
        var now = DateTime.UtcNow;

        if (_config.EyeCameraAutoRestart.Enabled)
        {
            var cooldown = TimeSpan.FromMilliseconds(_config.EyeCameraAutoRestart.CooldownMs);
            foreach (var (name, last) in _lastRestartAttemptUtc)
            {
                var remaining = cooldown - (now - last);
                if (remaining > TimeSpan.Zero)
                    pending.Add($"{name} restart cooldown {remaining.TotalSeconds:F0}s");
            }
        }

        if (_config.BaballoniaLifecycle.Enabled && _allCamerasOfflineSinceUtc is DateTime offlineSince && !_closedForCurrentOutage)
        {
            var remaining = TimeSpan.FromMilliseconds(_config.EyeCameraAutoRestart.AutoCloseAfterAllOfflineMs) - (now - offlineSince);
            if (remaining > TimeSpan.Zero)
                pending.Add($"Baballonia auto-close in {remaining.TotalSeconds:F0}s");
        }

        if (pending.Count > 0) return pending;

        // Nothing actively counting down — explain what would trigger the next change instead of
        // going silent (a healthy "all running" state needs no explanation, so this only fires
        // when at least one camera is offline).
        var offlineCams = _last.Where(c => !c.Online).ToList();
        if (offlineCams.Count > 0)
        {
            pending.Add(!ProcessLauncher.IsRunning("Baballonia.Desktop")
                ? "waiting for a camera to respond to ping to launch Baballonia"
                : $"waiting for {string.Join("/", offlineCams.Select(c => c.Camera.Name))} to respond to ping");
        }

        return pending;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _launchTrigger.Dispose();
    }
}
