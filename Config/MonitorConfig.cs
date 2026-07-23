using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace VrSessionMonitor.Config;

public sealed class TrackerConfig
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public bool HasExtension { get; set; }
}

public sealed class EyeCameraConfig
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>Exact section label text in Baballonia's ("Project Babble") UI — e.g. "Left Eye
    /// Camera" — used to scope UI-automation restart to the right Start/Stop Camera buttons,
    /// since all three sections (Left/Right/Face) share identically-named, unlabeled buttons.</summary>
    public string BaballoniaSectionLabel { get; set; } = "";
}

public sealed class EyeCameraAutoRestartConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minimum time between automated Stop+Start attempts for the same camera, so a
    /// persistently-dead camera doesn't get spam-clicked.</summary>
    public int CooldownMs { get; set; } = 45000;
    /// <summary>Delay between clicking Stop Camera and Start Camera, giving Babble time to
    /// actually tear down the capture before reinitializing it.</summary>
    public int StopToStartDelayMs { get; set; } = 800;
    /// <summary>How long with every configured eye camera unreachable (ping-confirmed, not just
    /// "not streaming") before BaballoniaLifecycleManager auto-closes Baballonia. Gated by
    /// BaballoniaLifecycleConfig.Enabled, not here — this is just the timing.</summary>
    public int AutoCloseAfterAllOfflineMs { get; set; } = 30000;
}

public sealed class BaballoniaLifecycleConfig
{
    /// <summary>Toggled live from the tray menu ("Auto-start/stop Baballonia") and persisted to
    /// appsettings.json immediately on change. Gates BOTH BaballoniaLaunchTrigger's auto-launch (when
    /// an eye camera is detected and Baballonia isn't running) and EyeTrackingMonitor's
    /// auto-close (after AutoCloseAfterAllOfflineMs with every eye camera unreachable). When
    /// false, Baballonia's start/stop is entirely manual — everything else (camera detection,
    /// per-camera auto-restart) keeps running regardless.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class FaceTrackingAutoFixConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minimum time between automated sr_runtime.exe kill+relaunch attempts, so a
    /// persistently broken link doesn't get kill-looped.</summary>
    public int CooldownMs { get; set; } = 45000;
    /// <summary>How long the module&lt;-&gt;SRanipal connection must stay dead (with both
    /// processes alive and a module loaded) before treating it as a real stall rather than a
    /// brief blip.</summary>
    public int SustainedDisconnectMs { get; set; } = 10000;
    /// <summary>Confirmed live 2026-07-16: killing and relaunching sr_runtime.exe ALONE never
    /// recovered the connection across 6+ consecutive attempts over 14+ minutes —
    /// ModuleConnectedToSRanipal read false on every single check immediately following a
    /// relaunch, even with ViveCameraDevicePresent staying true the entire time (so it wasn't a
    /// USB/device-attach issue either). Root cause: VRCFaceTracking's module process appears to
    /// attempt its SRanipal connection once, at its own startup, and never retries on its own —
    /// a fresh sr_runtime listening on the same ports doesn't matter if nothing ever asks it for
    /// a new connection. After this many consecutive failed sr_runtime-only attempts, also kill
    /// VRCFaceTracking.exe itself (not relaunched here — VrcFaceTrackingLifecycleManager owns
    /// that and will bring it back within one of its own poll cycles since a tracker is still
    /// present), forcing every module to reload and actually retry the connection.</summary>
    public int EscalateToVrcFaceTrackingRestartAfterAttempts { get; set; } = 2;
    /// <summary>Safety valve: if even the escalated fix (sr_runtime + VRCFaceTracking restart)
    /// keeps failing this many times in a row, stop attempting automated recovery for
    /// <see cref="GiveUpCooldownMs"/> instead of hammering both processes forever with no effect
    /// — at that point the problem is very likely upstream (VirtualHere on the headset side, the
    /// physical USB link) and no amount of local process restarting can fix it. Logged loudly
    /// and surfaced via a SteamVR toast so it's actually noticed instead of cycling silently.</summary>
    public int GiveUpAfterAttempts { get; set; } = 4;
    /// <summary>How long to back off after <see cref="GiveUpAfterAttempts"/> is reached before
    /// trying the whole escalation ladder again from scratch, in case conditions changed (replug,
    /// headset-side VirtualHere restart, etc.) without anyone toggling the monitor.</summary>
    public int GiveUpCooldownMs { get; set; } = 300000;
}

/// <summary>Detects the exact SteamVR failure mode found live on 2026-07-21: vrserver.exe and
/// vrcompositor.exe come up and stay running as OS processes, but never produce any real log
/// output (vrserver.txt/vrcompositor.txt stayed 0 bytes / untouched since the previous day) —
/// a stuck/zombie session that showed up in the headset as a solid black view and prevented
/// SlimeVR's SteamVR driver from registering. The manual fix was killing vrserver/vrmonitor/
/// vrcompositor and relaunching via the steam://rungameid/250820 protocol; this automates that.</summary>
public sealed class SteamVrStuckSessionConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>How long to wait after vrserver+vrcompositor are BOTH first seen running before
    /// judging whether they ever wrote real log output — a fresh, healthy SteamVR needs a few
    /// seconds to start logging, so checking immediately would false-positive.</summary>
    public int GracePeriodMs { get; set; } = 45000;
    /// <summary>Safety valve matching the pattern in FaceTrackingAutoFixConfig: after this many
    /// consecutive restart attempts still end up stuck, stop and back off for
    /// <see cref="GiveUpCooldownMs"/> instead of endlessly relaunching a SteamVR that isn't going
    /// to come up healthy no matter how many times it's kicked.</summary>
    public int GiveUpAfterAttempts { get; set; } = 2;
    public int GiveUpCooldownMs { get; set; } = 300000;
}

public sealed class VrcFaceTrackingLifecycleConfig
{
    /// <summary>VRCFaceTracking is launched whenever the Vive Facial Tracker or either eye
    /// camera is detected present, and shut down after ShutdownDelayMs of neither being present
    /// — rather than running unconditionally. This replaces plain crash-recovery for
    /// VRCFaceTracking specifically (sr_runtime.exe and vhui64.exe still get unconditional
    /// crash-recovery in FaceTrackingMonitor).</summary>
    public bool Enabled { get; set; } = true;
    public int ShutdownDelayMs { get; set; } = 30000;
    /// <summary>Confirmed live 2026-07-16: VRCFaceTracking.exe's own parent process can silently
    /// wedge its OSC output after a long continuous run — one real incident froze both mouth and
    /// eye movement in-avatar simultaneously after exactly 3h26m of unbroken uptime (WMI
    /// CreationDate matched to the second), while every existing health signal kept reading fine
    /// the whole time: ModuleConnectedToSRanipal stayed true, Baballonia's own UI showed live
    /// camera capture, EyeTrackingMonitor showed both cameras streaming. The freeze was in
    /// neither module's link to its own backend (both individually healthy) but in however the
    /// parent aggregates/forwards that data to OSC — invisible to every signal this monitor has,
    /// since none of them observe the parent's actual output freshness, only presence/connection
    /// state. A plain restart fixed it in seconds.
    ///
    /// Real OSC-output-freshness detection was considered and rejected as impractical: sampling
    /// this process's own I/O perf counters was already confirmed elsewhere in this codebase to
    /// read 0 for .NET's IOCP-based async socket I/O (see FaceTrackingMonitor's history notes),
    /// and a second process can't passively "sniff" UDP unicast traffic already destined for
    /// another process's bound socket without a packet-capture driver (WinDivert/Npcap) — a heavy
    /// dependency for one edge case. So this is a bounded-uptime preventive restart instead: a
    /// deliberately blunt, well-understood mitigation (the same shape as periodic pod restarts or
    /// liveness-probe fallback timers) for a failure mode that's real but too expensive to detect
    /// precisely. Only fires while a tracker is actually present (so it relaunches immediately via
    /// the normal presence-based path below, not left down) and is based on a single observed data
    /// point, so the default has real margin below the 3h26m failure and may need tuning if it
    /// either fires too eagerly or turns out too conservative. 0 disables it.</summary>
    public int MaxContinuousUptimeMs { get; set; } = 10800000; // 3 hours
}

/// <summary>Replaces VRCOSC.exe's old manual launch/kill in vd.cmd/kill.cmd with presence-based
/// lifecycle management: launched the moment VRChat.exe is seen running, closed entirely
/// (not just its internal "stop running" state — the whole process) after VRChat has been gone
/// for ShutdownDelayMs. This is independent of VRCOSC's own internal "start when VRChat is
/// detected" setting, which only governs whether its modules start doing work once VRCOSC is
/// already open — it says nothing about whether the VRCOSC.exe process itself is alive, which is
/// what this class actually owns.</summary>
public sealed class VrcOscLifecycleConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Mirrors VrcFaceTrackingLifecycleConfig.ShutdownDelayMs — avoids killing (and then
    /// relaunching) VRCOSC across a quick VRChat restart, which would otherwise bounce its OSC
    /// connection for no reason.</summary>
    public int ShutdownDelayMs { get; set; } = 30000;
}

public sealed class PathsConfig
{
    public string SteamExe { get; set; } = @"C:\Program Files (x86)\Steam\steam.exe";
    public string VirtualDesktopStreamerExe { get; set; } = @"C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe";
    public string VrChatLaunchExe { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\VRChat\launch.exe";
    public string SlimeVrExe { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\SlimeVR\slimevr.exe";
    public string VrcFaceTrackingExe { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\VRCFaceTracking\VRCFaceTracking.exe";
    /// <summary>Confirmed live 2026-07-24 — this is where the installer actually puts it on this
    /// machine, previously only launched/killed by hand via vd.cmd/kill.cmd.</summary>
    public string VrcOscExe { get; set; } = @"C:\Users\darkf\AppData\Local\VRCOSC\VRCOSC.exe";
    public string BaballoniaExe { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\Baballonia\Baballonia.Desktop.exe";
    public string VirtualHereClientExe { get; set; } = @"C:\Programs\vhui64.exe";
    public string SRanipalExe { get; set; } = @"C:\Programs\SRanipal\sr_runtime.exe";
    public string OpenVrApiDllPath { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\openvr_api.dll";
    /// <summary>SteamVR's actual log directory — read from this machine's
    /// %LOCALAPPDATA%\openvr\openvrpaths.vrpath ("log" entry) rather than assumed, since it can
    /// differ from the SteamVR install path. Used only for the stuck-session check (see
    /// SteamVrStuckSessionConfig) — vrserver.txt/vrcompositor.txt never getting written despite
    /// the process running is exactly the black-screen bug found live on 2026-07-21.</summary>
    public string SteamVrLogDirectory { get; set; } = @"C:\Program Files (x86)\Steam\logs";
    /// <summary>OVR Toolkit's Steam App ID (confirmed live 2026-07-22 via its appmanifest_*.acf —
    /// also matches the `steam.overlay.1068820` references seen in VRChat's own log). Launched via
    /// steam://rungameid/&lt;this&gt; rather than its exe path directly — a direct exe launch was
    /// found to silently skip OVR Toolkit's own admin-elevation handshake ("Process is not running
    /// as admin or has failed to get the right elevation level!"), causing its bridge process and
    /// WebSocket server to fail and the whole app to exit shortly after starting. Launching through
    /// Steam avoids that (Steam handles the elevation per its own per-app compatibility settings).</summary>
    public string OvrToolkitSteamAppId { get; set; } = "1068820";
    /// <summary>Wherever your ADB install puts it — e.g. SideQuest bundles its own under
    /// "...\SideQuest\resources\app.asar.unpacked\build\platform-tools\adb.exe". Left blank by
    /// default; ADB integration is entirely best-effort and degrades gracefully if unset (see
    /// AdbController).</summary>
    public string AdbExe { get; set; } = "";
    /// <summary>Relative to the app's own working directory by default, so this works out of
    /// the box on any machine. Point it elsewhere if you'd rather logs live somewhere else.</summary>
    public string LogDirectory { get; set; } = "logs";
}

public sealed class NetworkConfig
{
    /// <summary>Your headset's LAN IP — give it a DHCP reservation so it doesn't change.
    /// Blank by default; the headset-detection ping loop simply never succeeds until this is
    /// set, which is a safe/inert default (see HeadsetMonitor).</summary>
    public string HeadsetIp { get; set; } = "";
    public string HeadsetName { get; set; } = "";
    public int VirtualDesktopPort { get; set; } = 38830;
    public int SlimeVrTrackerPort { get; set; } = 6969;
    public int FirmwareNotifyUdpPort { get; set; } = 6970;
    public int AdbPort { get; set; } = 5555;
    public int EyeCameraHttpPort { get; set; } = 80;
    /// <summary>SRanipal's own local TCP listening ports (127.0.0.1) — confirmed live 2026-07-16
    /// via netstat (sr_runtime.exe listens on 1000-1011; the face-tracking VRCFaceTracking
    /// module connects to 1001/1002 in practice). Used to detect a real module&lt;-&gt;SRanipal
    /// connection rather than trusting process-presence alone.</summary>
    public int SRanipalPortRangeStart { get; set; } = 1000;
    public int SRanipalPortRangeEnd { get; set; } = 1011;
}

public sealed class PollingConfig
{
    public int HeadsetPingIntervalMs { get; set; } = 5000;
    public int HeadsetPingTimeoutMs { get; set; } = 800;
    public int TrackerCheckIntervalMs { get; set; } = 10000;
    public int TrackerPingTimeoutMs { get; set; } = 500;
    public int ProcessPollIntervalMs { get; set; } = 1000;
    public int ProcessLaunchTimeoutMs { get; set; } = 60000;
    public int UpdateCheckIntervalMinutes { get; set; } = 120;
    /// <summary>How often to ping eye camera IPs while Baballonia isn't running yet, purely to
    /// detect "cameras just powered on" fast enough to auto-launch Baballonia promptly. Separate
    /// from the normal 5s peripheral-check cadence, which only matters once Baballonia is
    /// already up.</summary>
    public int EyeCameraPreLaunchPingIntervalMs { get; set; } = 1000;
    /// <summary>Consecutive failed pings required before EyeTrackingMonitor actually declares a
    /// camera offline. Confirmed live 2026-07-16: with no debounce, an isolated dropped ICMP
    /// packet flipped Online to false for a single ~5s cycle, and the very next successful ping
    /// then read as an offline-to-online transition — which forces a cooldown-bypassing camera
    /// restart by design (see EyeTrackingMonitor class doc). Result: real logs showed forced
    /// restarts firing every 16-22s for cameras that were never actually down. Only the
    /// online-to-offline direction is debounced; a single successful ping still brings a camera
    /// back online immediately (recovering fast is safe — only false "went offline" blips caused
    /// the problem).</summary>
    public int EyeCameraOfflineDebounceFailures { get; set; } = 3;
}

public sealed class UpdateCheckConfig
{
    public bool Enabled { get; set; } = true;
    public bool CheckVrChat { get; set; } = true;
    public bool CheckSlimeVr { get; set; } = true;
    public bool CheckVirtualDesktopStreamer { get; set; } = true;
    public bool CheckVrcFaceTracking { get; set; } = true;
    public string SlimeVrGithubRepo { get; set; } = "SlimeVR/SlimeVR-Server";
    public string VrcFaceTrackingGithubRepo { get; set; } = "benaclejames/VRCFaceTracking";
}

public sealed class SessionFlowConfig
{
    /// <summary>Toggled live from the tray menu ("Auto-start VRChat") and persisted to
    /// appsettings.json immediately on change. When false, SessionOrchestrator still does everything
    /// else (VD Streamer, Steam, SlimeVR) but skips launching VRChat itself.</summary>
    public bool AutoLaunchVrChat { get; set; } = true;

    /// <summary>Toggled live from the tray menu ("Auto-start OVR Toolkit"). Launched via
    /// steam://rungameid/&lt;PathsConfig.OvrToolkitSteamAppId&gt; — see that field's doc for why a
    /// direct exe launch doesn't work.</summary>
    public bool AutoLaunchOvrToolkit { get; set; } = true;

    /// <summary>Toggled live from the tray menu ("VRChat: low-power window"). When true, VRChat
    /// launches windowed at a small resolution with a lower FPS target instead of fullscreen —
    /// for when you're not actually going to view it through Virtual Desktop and just want it
    /// running (e.g. for OSC/avatar work) without paying full rendering cost.</summary>
    public bool VrChatLowPowerMode { get; set; } = false;
    public int VrChatLowPowerWidth { get; set; } = 1024;
    public int VrChatLowPowerHeight { get; set; } = 768;
    public int VrChatLowPowerFps { get; set; } = 45;
    public int VrChatLowPowerMonitor { get; set; } = 1;

    /// <summary>Confirmed live 2026-07-22: once SteamVR loads SlimeVR's own OpenVR driver
    /// (SlimeVR-Bindings-Provider.exe), that driver auto-launches the full SlimeVR.exe GUI itself
    /// (with a `-- --steam` arg) roughly 25s later — entirely independent of this app. Launching
    /// SlimeVR immediately (the old behavior) raced that auto-launch and produced two real GUI
    /// windows. Waiting this long before our own launch attempt gives the driver's auto-launch a
    /// chance to land first, so our own EnsureRunningAsync's "already running" check just skips —
    /// while still launching it ourselves as a safety net if that auto-launch doesn't happen.</summary>
    public int SlimeVrLaunchDelayMs { get; set; } = 35000;
}

public sealed class AdbConfig
{
    public bool Enabled { get; set; } = true;
    public bool AutoLaunchVirtualDesktopApp { get; set; } = true;
    public string VirtualDesktopPackageName { get; set; } = "com.virtualdesktop.vr";
}

public sealed class MonitorConfig
{
    public NetworkConfig Network { get; set; } = new();
    public PollingConfig Polling { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public UpdateCheckConfig Updates { get; set; } = new();
    public AdbConfig Adb { get; set; } = new();
    public EyeCameraAutoRestartConfig EyeCameraAutoRestart { get; set; } = new();
    public BaballoniaLifecycleConfig BaballoniaLifecycle { get; set; } = new();
    public FaceTrackingAutoFixConfig FaceTrackingAutoFix { get; set; } = new();
    public SteamVrStuckSessionConfig SteamVrStuckSession { get; set; } = new();
    public VrcFaceTrackingLifecycleConfig VrcFaceTrackingLifecycle { get; set; } = new();
    public VrcOscLifecycleConfig VrcOscLifecycle { get; set; } = new();
    public SessionFlowConfig SessionFlow { get; set; } = new();
    public List<TrackerConfig> Trackers { get; set; } = new();
    public List<EyeCameraConfig> EyeCameras { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Loading goes through Microsoft.Extensions.Configuration (the standard .NET
    /// config idiom) rather than a raw JsonSerializer.Deserialize call. Saving still uses
    /// System.Text.Json directly below — IConfiguration is deliberately read-only with no
    /// supported way to write settings back out, a real framework limitation (not something
    /// ASP.NET itself solves either), and this app's tray toggles need to persist clicks. So
    /// this is intentionally hybrid: standard binding for reads, a hand-rolled writer for saves.
    ///
    /// Same gotcha as always with this kind of load-or-default pattern: a JSON key that's
    /// missing from an existing appsettings.json (e.g. after adding a new config field) binds to
    /// that property's own C# default, NOT whatever CreateDefault() would have seeded — Get&lt;T&gt;
    /// has the same "missing key keeps the property initializer's value" semantics the old
    /// System.Text.Json call had. Delete appsettings.json to force full regeneration if that
    /// ever matters.</summary>
    public static MonitorConfig LoadOrCreateDefault(string path)
    {
        if (File.Exists(path))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var configuration = new ConfigurationBuilder()
                .SetBasePath(directory)
                .AddJsonFile(Path.GetFileName(path), optional: false, reloadOnChange: false)
                .Build();

            var loaded = configuration.Get<MonitorConfig>();
            if (loaded is not null)
                return loaded;
        }

        var defaultConfig = CreateDefault();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(defaultConfig, JsonOptions));
        return defaultConfig;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    // Deliberately empty — this is what a fresh clone gets. Trackers/cameras are entirely
    // hardware-specific, so shipping someone else's real network topology in source doesn't
    // help anyone. Either fill in appsettings.json by hand (see appsettings.example.json for
    // the shape) or use the tray menu's "Auto-detect trackers/cameras" action, which queries
    // SlimeVR's own API and reads Baballonia's camera fields directly (see SlimeVrDiscovery /
    // BaballoniaAutomation.TryReadCameraAddress).
    public static MonitorConfig CreateDefault() => new()
    {
        Trackers = new List<TrackerConfig>(),
        EyeCameras = new List<EyeCameraConfig>(),
    };
}
