using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using VrSessionMonitor.Logging;

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

    /// <summary>Safety valve matching the pattern in FaceTrackingAutoFixConfig and
    /// SteamVrStuckSessionConfig, which this auto-restart was previously missing entirely.
    /// Confirmed live 2026-08-17/18: a left eye camera whose firmware had failed (outputting an
    /// error code as its frame instead of an image — a genuine hardware fault) stayed reachable on
    /// the network but never streamed once for an entire 2h11m session. With no failure ceiling,
    /// the Stop+Start automation fired 170 times and produced 171 errors that buried the real
    /// face-tracking signals in the same log. No amount of clicking Baballonia's buttons can fix
    /// broken hardware, so after this many consecutive restart attempts that never result in the
    /// camera actually streaming, back off for <see cref="GiveUpCooldownMs"/> instead of retrying
    /// forever. 0 disables the ceiling (retry indefinitely, the old behavior).</summary>
    public int GiveUpAfterAttempts { get; set; } = 5;

    /// <summary>How long to stop attempting restarts for a camera that hit
    /// <see cref="GiveUpAfterAttempts"/>, before trying the ladder again from scratch in case
    /// conditions changed. A camera actually streaming again, or going offline and coming back
    /// (a real power-cycle — new evidence), clears the backoff immediately regardless.</summary>
    public int GiveUpCooldownMs { get; set; } = 600000; // 10 minutes
}

/// <summary>Confirmed live 2026-07-30: EyeTrackingMonitor's "streaming" check (an ESTABLISHED TCP
/// connection from Baballonia to the camera) can read true for both eye cameras while VRChat's own
/// eye-tracking OSC parameters (EyeLeftX/EyeRightX/EyeY/eyelids) sit completely frozen — verified
/// directly via VRChat's own OSCQuery /avatar/parameters endpoint: zero of 31 eye parameters
/// changed across two snapshots while 35 other avatar parameters (including face/mouth tracking
/// from the separate SRanipal pipeline) changed in the same window. Same "TCP established doesn't
/// mean data is flowing" blind spot already found for SRanipal's loopback connection and VRChat's
/// own post-restart OSC readiness, just one hop further downstream this time — inside Baballonia's
/// own camera-tracking loop, invisible to the network-level streaming check entirely. A manual
/// Stop+Start Camera cycle on both eyes fixed it in under 10s once diagnosed; this automates that
/// diagnosis + fix.</summary>
public sealed class EyeTrackingOscFreshnessConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>How often to actually query VRChat's OSCQuery endpoint — deliberately much less
    /// frequent than the plain ping-based checks above, since this involves a real HTTP round trip
    /// (and a netstat-based port lookup) rather than a cheap local check.</summary>
    public int CheckIntervalMs { get; set; } = 10000;
    /// <summary>How long EyeLeftX/EyeRightX/EyeY/EyeLidLeft/EyeLidRight must stay bit-for-bit
    /// identical across checks (with both cameras still reporting online+streaming the whole time)
    /// before treating it as frozen rather than a legitimately still gaze — a real human blinks
    /// often enough that all five values staying frozen together for this long is a strong signal,
    /// not a coincidence.</summary>
    public int StaleThresholdMs { get; set; } = 25000;
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

    /// <summary>Independent of <see cref="Enabled"/>: when the Vive Facial Tracker's camera attaches
    /// AFTER sr_runtime.exe / VRCFaceTracking already started (e.g. VirtualHere's share came up
    /// late), the module never picks it up on its own — VRCFaceTracking attempts its SRanipal
    /// connection once at startup and never retries. When true (default), that specific
    /// camera-just-appeared-while-not-connected edge triggers a one-shot sr_runtime + VRCFaceTracking
    /// restart (the same no-UAC relaunch path the stalled-connection fix uses), so the module
    /// reloads with the camera present. Fires even when <see cref="Enabled"/> is false, since it's a
    /// narrow edge-triggered recovery, not the broader stalled-connection kill loop. The
    /// <see cref="CooldownMs"/> throttle applies so rapid camera attach/detach flapping can't loop.</summary>
    public bool RestartOnCameraReappear { get; set; } = true;

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
    /// <summary>Same OSC ground-truth technique as EyeTrackingOscFreshnessConfig, applied to the
    /// face/mouth side: ModuleConnectedToSRanipal is just a TCP-established check and can read true
    /// while VRChat's own face-tracking parameters (JawOpen/JawX/MouthX/LipPucker) sit frozen. When
    /// this fires, it's treated exactly like ModuleConnectedToSRanipal reading false — same
    /// sustained-timer, cooldown, and escalation ladder above, just a better-informed trigger.</summary>
    public bool OscFreshnessEnabled { get; set; } = true;
    /// <summary>How often to actually query VRChat's OSCQuery endpoint — deliberately less frequent
    /// than this monitor's own ~5s poll cycle, since this is a real HTTP round trip plus a netstat
    /// port lookup, not a cheap local check.</summary>
    public int OscFreshnessCheckIntervalMs { get; set; } = 10000;
    /// <summary>How long JawOpen/JawX/MouthX/LipPucker/etc. must stay bit-for-bit identical across
    /// checks before treating the face-tracking OSC output as frozen rather than a legitimately
    /// neutral/resting face.</summary>
    public int OscFreshnessStaleThresholdMs { get; set; } = 25000;
}

/// <summary>Confirmed live 2026-07-27: the "SRanipalService" Windows Service (Automatic start
/// type) was found Stopped while sr_runtime.exe (a separate process) kept running as an orphaned
/// shell — this monitor's only SRanipal health checks are sr_runtime.exe's process presence and
/// an ESTABLISHED TCP connection to it, neither of which has any visibility into this service at
/// all. A dead SRanipalService with sr_runtime.exe still alive is indistinguishable, from this
/// monitor's existing checks, from a genuinely healthy pipeline — the loopback TCP connection to
/// sr_runtime can stay "ESTABLISHED" as a zombie link (TCP has no way to notice a dead peer
/// without traffic/keepalives, same caveat already documented for EyeCameraStatus.Streaming, just
/// never applied to this hop). Restarting VRCFaceTracking.exe or sr_runtime.exe alone can't fix a
/// dead backing service; only the legacy ft.cmd's full kill-and-cold-start ritual did, which
/// restarts sr_runtime.exe from an environment where the service happens to come back too. This
/// config adds a direct, explicit health check + restart for the service itself instead of relying
/// on that side effect.</summary>
public sealed class SRanipalServiceConfig
{
    public bool Enabled { get; set; } = true;
    public string ServiceName { get; set; } = "SRanipalService";
    /// <summary>Minimum time between automated service-start attempts, so a service that's
    /// Disabled or fails to start under the current (possibly unprivileged) account doesn't get
    /// hammered every ~5s poll cycle — an access-denied failure is logged once per cooldown window
    /// instead of on every check.</summary>
    public int RestartCooldownMs { get; set; } = 60000;
    /// <summary>On startup, checks whether the current Windows user already has an explicit ACE on
    /// this service and, if not, requests one elevation (a single UAC prompt) to grant Start/Stop
    /// rights via `sc sdset` — see SRanipalServicePermissions. This is what lets the recovery above
    /// actually succeed while this app itself keeps running unelevated. Once granted it persists in
    /// the service's own security descriptor, so this only ever prompts once (or again if declined
    /// last time).</summary>
    public bool GrantStartStopPermissionOnStartup { get; set; } = true;
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
    /// — rather than running unconditionally. sr_runtime.exe/vhui64.exe get the same treatment
    /// via VirtualHereSRanipalLifecycleConfig below, not unconditional crash-recovery anymore
    /// (that was the 2026-08-09 bug this config was added to fix).</summary>
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
    /// <summary>Confirmed live 2026-07-27: the VRChat-restart staleness refresh (see
    /// VrcFaceTrackingLifecycleManager.CheckVrChatRestart) fired 1-2s after a new VRChat instance's
    /// process object first appeared — long before VRChat's own OSC service is actually up. The
    /// fresh VRCFaceTracking that came back immediately after made a one-shot OSC handshake attempt
    /// against a VRChat that wasn't listening yet, and (matching the same "never retries" behavior
    /// documented for the SRanipal side) silently never established real output — every health
    /// signal this monitor has stayed green (SRanipal-side connection, module count, Vive tracker)
    /// the entire time, since none of them observe the VRChat-facing hop at all. Requiring the new
    /// VRChat instance to have been running at least this long before triggering the refresh gives
    /// its OSC service realistic time to come up first.</summary>
    public int MinVrChatUptimeBeforeRestartMs { get; set; } = 20000;
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

    /// <summary>When true (default), auto-click "Yes" on VRCOSC's own "Update Available" dialog so
    /// it applies the update itself instead of sitting there blocking, waiting for a manual click.
    /// Handled by UI Automation against the dialog window (same approach as the Baballonia camera
    /// buttons). Set false to leave the prompt alone (dismiss it yourself, or handle updates
    /// manually).</summary>
    public bool AutoAcceptUpdatePrompt { get; set; } = true;
}

/// <summary>Added 2026-08-09 after vhui64.exe/sr_runtime.exe were found running indefinitely with
/// no headset connected and no VR session active at all — they used to get unconditional
/// crash-recovery in FaceTrackingMonitor (launch if not running, no matter what), which meant
/// nothing ever actually stopped them once started. See VirtualHereSRanipalLifecycleManager for
/// the presence signal used (headset online, OR the Vive tracker device already actively
/// present so an existing desktop-mode session isn't interrupted).</summary>
public sealed class VirtualHereSRanipalLifecycleConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Deliberately longer than VrcFaceTrackingLifecycleConfig.ShutdownDelayMs (30s) —
    /// vhui64.exe/sr_runtime.exe are cheap to leave running for a bit in case the headset comes
    /// back shortly (e.g. a brief WiFi drop), and relaunching sr_runtime.exe specifically has a
    /// real cost (fresh SRanipal init, UAC-prompt-suppression dance) worth avoiding on a false
    /// "session ended" read.</summary>
    public int ShutdownDelayMs { get; set; } = 300000; // 5 minutes
}

public sealed class SlimeVrLifecycleConfig
{
    /// <summary>Auto-stop SlimeVR (slimevr.exe + its java child) once you're done with VR.
    /// "Done" = SteamVR not running AND headset offline AND the trackers have been motionless for
    /// IdleWindowMs — all three, held for ShutdownDelayMs. This manager only STOPS SlimeVR;
    /// launching stays with SessionOrchestrator.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How long all three signals (SteamVR off, headset off, trackers idle) must stay
    /// inactive before SlimeVR is stopped. Matches VirtualHereSRanipalLifecycle's 5 min.</summary>
    public int ShutdownDelayMs { get; set; } = 300000;
    /// <summary>How often to poll SlimeVR's SolarXR WebSocket for tracker rotations.</summary>
    public int PollIntervalMs { get; set; } = 2000;
    /// <summary>Trackers must stay within IdleAngleThresholdDeg for this long to count as idle.</summary>
    public int IdleWindowMs { get; set; } = 120000;
    /// <summary>Per-tracker angular change (degrees) under this over the window = "still". Set above
    /// the IMU noise floor so a resting-but-powered tracker reads as idle.</summary>
    public double IdleAngleThresholdDeg { get; set; } = 2.0;
}

public enum OptimizationMode { Off, Manual, Auto }

/// <summary>Persisted per-check state for the Optimizations tab (see
/// docs/superpowers/specs/2026-08-16-optimizations-tab-design.md). CapturedOriginalValues is
/// populated by the owning IOptimization on first Apply and consumed on Revert — keys are
/// implementation-defined (e.g. RegistryValueTarget.StorageKey), values are always
/// string-serialized so the whole entry round-trips through plain JSON.</summary>
public sealed class OptimizationEntry
{
    public OptimizationMode Mode { get; set; } = OptimizationMode.Off;
    /// <summary>True once the one-time elevation grant (if any) for this check has succeeded —
    /// see RegistryAccessGrant/IServiceController.GrantControlPermissionAsync. For checks with a
    /// static target set this is never re-requested once true. For checks with a DYNAMIC target
    /// set (RegistryValueOptimization's tcp-low-latency-tuning, ServiceStateOptimization's
    /// stray-vm-services-stopped), this reflects whether every target CURRENTLY known was granted
    /// as of the last EnsureAccessGrantedAsync call — see GrantedTargets, which those two checks
    /// consult to grant only newly-appeared targets rather than re-requesting everything.</summary>
    public bool AccessGranted { get; set; }
    /// <summary>Per-target record of which specific grant targets (HKLM subkey paths, service
    /// names) have already been successfully granted — lets RegistryValueOptimization/
    /// ServiceStateOptimization diff a freshly-enumerated target set against what's already
    /// covered and only request elevation for the new ones (e.g. a newly-connected network
    /// adapter, or a service installed after the first grant). Unused by checks with a static
    /// target set (PowerPlanOptimization, UsbSelectiveSuspendOptimization), which keep using the
    /// plain AccessGranted bool.</summary>
    public HashSet<string> GrantedTargets { get; set; } = new();
    public Dictionary<string, string?> CapturedOriginalValues { get; set; } = new();
}

/// <summary>Keyed by each IOptimization's stable Id (see OptimizationRegistry) — never rename an
/// existing Id, since it's the persistence key here.</summary>
public sealed class OptimizationsConfig
{
    public Dictionary<string, OptimizationEntry> Entries { get; set; } = new();
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
    public string VrcOscExe { get; set; } = @"C:\Users\<user>\AppData\Local\VRCOSC\VRCOSC.exe";
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
    /// <summary>XSOverlay's Steam App ID. Launched via steam://rungameid/&lt;this&gt; exactly like
    /// OVR Toolkit (same elevation-handshake reasoning). Value 1173510 is XSOverlay's known Steam id;
    /// confirm against the real appmanifest/process once XSOverlay is installed (it wasn't installed
    /// as of 2026-08-10).</summary>
    public string XSOverlaySteamAppId { get; set; } = "1173510";
    /// <summary>VRChat's Steam App ID (well-known/public). Added 2026-08-09 when VRChat's launch
    /// switched from directly wrapping VrChatLaunchExe (via VD Streamer) to steam://rungameid — a
    /// direct exe launch was found to bypass Steam Input's per-game binding activation entirely,
    /// since that only fires for games launched through Steam's own protocol. Confirmed the same
    /// class of bug already fixed for OVR Toolkit above. The tradeoff: VRChat's background-mode
    /// launch args (see SessionFlow.VrChatBackgroundMode) can no longer be passed dynamically per
    /// launch — steam:// takes no arguments, so they must be set once as this app's static Steam
    /// "Launch Options" instead (right-click VRChat in Steam > Properties > Launch Options).</summary>
    public string VrChatSteamAppId { get; set; } = "438100";
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
    /// <summary>Optional fallback IP, checked when HeadsetIp doesn't respond — e.g. a headset that
    /// can be on either your normal WiFi or the PC's own hotspot depending on which one it
    /// associated with, each giving it a different DHCP-reserved address. Blank/inert by default;
    /// leave empty if your headset only ever has one address. HeadsetMonitor treats the headset as
    /// online if EITHER address responds, and reports whichever one actually answered.</summary>
    public string HeadsetIpSecondary { get; set; } = "";
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
    /// <summary>Consecutive failed pings required before HeadsetMonitor actually declares the
    /// headset offline. Same debounce pattern as EyeCameraOfflineDebounceFailures below, added
    /// after the same class of bug: confirmed live 2026-08-07 that with no debounce, an isolated
    /// dropped ICMP packet (headset connected via the PC's own WiFi hotspot rather than the usual
    /// router, which has more latency/loss) flipped online-to-offline for a single ~5s cycle, and
    /// the very next successful ping flipped it straight back — Home Assistant's HeadsetOnActions/
    /// HeadsetOffActions fire on every transition, so this was toggling real lights on and off
    /// every 20-45s while the headset sat mostly-connected but occasionally missed one ping. Only
    /// the online-to-offline direction is debounced; a single successful ping still brings the
    /// headset back online immediately, matching the eye-camera precedent's reasoning.
    /// Raised 3 -> 8 on 2026-08-14: an idle Quest 2 (screen off, not worn, WiFi radio power-saving)
    /// was observed duty-cycling — answering pings for ~40s bursts, then going silent for 16-22s —
    /// for 7+ consecutive minutes. That 16-22s silent gap already exceeds the old 3-failure/15s
    /// threshold, so the old value still declared it offline every cycle and re-toggled the lights
    /// on the very next successful ping. 8 failures = 40s comfortably clears the largest observed
    /// gap (22s) with margin for jitter.</summary>
    public int HeadsetOfflineDebounceFailures { get; set; } = 8;
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
    /// <summary>VRCOSC's own startup update check pops a blocking dialog that requires a manual
    /// click to dismiss — fatal for an unattended automated launch, same class of problem
    /// documented on ProcessLauncher's suppressUacPrompt. Checking here first and surfacing it as
    /// a tray notification (see TrayApplicationContext's UpdateFindingsAvailable handler) gives a
    /// chance to update manually ahead of time, before it can block an actual session launch.</summary>
    public bool CheckVrcOsc { get; set; } = true;
    public string SlimeVrGithubRepo { get; set; } = "SlimeVR/SlimeVR-Server";
    public string VrcFaceTrackingGithubRepo { get; set; } = "benaclejames/VRCFaceTracking";
    public string VrcOscGithubRepo { get; set; } = "VolcanicArts/VRCOSC";
}

public sealed class SessionFlowConfig
{
    /// <summary>Toggled live from the tray menu ("Auto-start VRChat") and persisted to
    /// appsettings.json immediately on change. When false, SessionOrchestrator still does everything
    /// else (VD Streamer, Steam, SlimeVR) but skips launching VRChat itself.</summary>
    public bool AutoLaunchVrChat { get; set; } = true;

    /// <summary>When true (default), the launch chain waits for a confirmed Virtual Desktop video
    /// stream from the headset before launching Steam/VRChat/SlimeVR/overlay — the gate that keeps
    /// SteamVR from starting on a headset that's merely powered on but not actually streaming.
    /// Set false for a SteamVR-direct rig (headset over Link/wired, or SteamVR started by hand, with
    /// no Virtual Desktop stream to wait for): the chain then proceeds as soon as the headset is
    /// reachable, so the overlay + VRChat actually auto-launch instead of timing out on a stream
    /// that never comes. The ping-flap / new-session guards still apply. Takes effect next session.</summary>
    public bool RequireVdStream { get; set; } = true;

    /// <summary>Which VR overlay to auto-launch each session (None / OVR Toolkit / XSOverlay).
    /// Chosen from the Settings window's overlay picker. Replaces the old single-overlay
    /// auto-launch bool (fully migrated as of Task 3); defaults to XSOverlay. Takes effect on the
    /// next session (config changes need an app restart).</summary>
    public VrOverlayChoice VrOverlay { get; set; } = VrOverlayChoice.XSOverlay;

    /// <summary>Toggled live from the tray menu ("VRChat: background mode"). When true, VRChat
    /// launches windowed at a small resolution instead of fullscreen — for when you're not
    /// actually going to view it through Virtual Desktop and just want it running (e.g. for
    /// OSC/avatar work) in the background, minimized. No FPS cap - VRChat runs at whatever rate
    /// it wants, the point is just staying out of the way visually, not saving render cost.</summary>
    public bool VrChatBackgroundMode { get; set; } = false;
    public int VrChatBackgroundWidth { get; set; } = 640;
    public int VrChatBackgroundHeight { get; set; } = 480;
    public int VrChatBackgroundMonitor { get; set; } = 1;

    /// <summary>Confirmed live 2026-07-22: once SteamVR loads SlimeVR's own OpenVR driver
    /// (SlimeVR-Bindings-Provider.exe), that driver auto-launches the full SlimeVR.exe GUI itself
    /// (with a `-- --steam` arg) roughly 25s later — entirely independent of this app. Launching
    /// SlimeVR immediately (the old behavior) raced that auto-launch and produced two real GUI
    /// windows. Waiting this long before our own launch attempt gives the driver's auto-launch a
    /// chance to land first, so our own EnsureRunningAsync's "already running" check just skips —
    /// while still launching it ourselves as a safety net if that auto-launch doesn't happen.</summary>
    public int SlimeVrLaunchDelayMs { get; set; } = 35000;

    /// <summary>Guards SessionOrchestrator's VD-Streamer-PID "already completed the launch chain
    /// for this instance" skip (see _launchChainCompletedForVdPid) — that skip only makes sense
    /// for a genuine short ping flap (Quest's Wi-Fi blipping for a few seconds while VD Streamer
    /// itself is never touched), not a real new session after the headset was off for a while.
    /// Confirmed live 2026-07-26: VD Streamer survived an entire ~11-hour overnight gap without
    /// restarting, so the PID-match skip fired on the next morning's genuine "headset online" and
    /// silently skipped launching VRChat entirely. If the headset was offline for at least this
    /// long before coming back, the launch chain reruns regardless of whether VD Streamer's PID
    /// is unchanged. Comfortably above every observed real flap (~5s in practice) and comfortably
    /// below any gap that represents an actual new session.</summary>
    public int MinHeadsetOfflineDurationForNewSessionMs { get; set; } = 90000;
}

/// <summary>One VRChat group whose instances should toggle an avatar OSC parameter while you're
/// in them. Detected by tailing VRChat's own log file for the group ID VRChat embeds directly in
/// an instance's join line (e.g. "...~group(grp_xxxxx)~groupAccessType(members)") - no VRChat API
/// login needed, since the same instance-location string VRCX itself parses already carries it.</summary>
public sealed class GroupAutomationEntry
{
    /// <summary>VRChat group ID, e.g. "grp_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx".</summary>
    public string GroupId { get; set; } = "";
    /// <summary>Just a label for the Settings UI - not sent anywhere.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Avatar OSC parameter name (sent as /avatar/parameters/&lt;ParamName&gt;, bool).</summary>
    public string ParamName { get; set; } = "";
    /// <summary>When true, set THIS group as your VRChat represented group (nameplate) while you're
    /// in its instance. Only listed groups with this on are ever auto-represented; leaving one falls
    /// back to VrChatGroupAutomationConfig.FallbackRepresentGroupId (or clears representation).</summary>
    public bool Represent { get; set; } = false;
}

public sealed class VrChatGroupAutomationConfig
{
    public bool Enabled { get; set; } = false;
    public List<GroupAutomationEntry> Groups { get; set; } = new();
    /// <summary>Group ID represented when you're not in a listed Represent=true group's instance.
    /// Empty = clear representation (represent nothing) in that case. Represent actions require a
    /// logged-in VRCX session on this machine (see VrcxSessionProvider) — no login = represent is
    /// skipped, OSC toggles still work.</summary>
    public string FallbackRepresentGroupId { get; set; } = "";
}

public sealed class AdbConfig
{
    public bool Enabled { get; set; } = true;
    public bool AutoLaunchVirtualDesktopApp { get; set; } = true;
    public string VirtualDesktopPackageName { get; set; } = "com.virtualdesktop.vr";
}

/// <summary>Which VR desktop overlay the session-start flow auto-launches. Launched via
/// steam://rungameid/&lt;app id&gt; (see PathsConfig.OvrToolkitSteamAppId's doc for why Steam,
/// not a direct exe). Notifications are unaffected — those go through OpenVR's overlay-independent
/// IVRNotifications (see SteamVrNotifier), not the overlay app.</summary>
public enum VrOverlayChoice { None, OvrToolkit, XSOverlay }

#if INCLUDE_HOME_ASSISTANT
public enum LightAction { NoChange, On, Off }

public static class LightActionExtensions
{
    public static LightAction ParseOrDefault(this string? value) =>
        Enum.TryParse<LightAction>(value, ignoreCase: true, out var parsed) ? parsed : LightAction.NoChange;

    public static string ToConfigString(this LightAction action) => action.ToString();
}

/// <summary>See docs/superpowers/specs/2026-07-26-home-assistant-lights-design.md. Three
/// independent tri-state light maps (entityId -> On/Off/NoChange), one per trigger event:
/// headset coming online (also re-applied when AFK ends), headset going offline, and AFK
/// starting (HMD proximity OR VRChat's own AFK OSC parameter, OR'd — see
/// HomeAssistantLightsManager).</summary>
public sealed class HomeAssistantConfig
{
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string SelectedAreaId { get; set; } = "";
    public Dictionary<string, string> HeadsetOnActions { get; set; } = new();
    public Dictionary<string, string> HeadsetOffActions { get; set; } = new();
    public Dictionary<string, string> AfkActions { get; set; } = new();
    public int AfkConsecutiveReadsBeforeFlip { get; set; } = 3;
    public int AfkPollIntervalMs { get; set; } = 2000;
}
#endif

public sealed class MonitorConfig
{
    public NetworkConfig Network { get; set; } = new();
    public PollingConfig Polling { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public UpdateCheckConfig Updates { get; set; } = new();
    public AdbConfig Adb { get; set; } = new();
#if INCLUDE_HOME_ASSISTANT
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
#endif
    public EyeCameraAutoRestartConfig EyeCameraAutoRestart { get; set; } = new();
    public EyeTrackingOscFreshnessConfig EyeTrackingOscFreshness { get; set; } = new();
    public BaballoniaLifecycleConfig BaballoniaLifecycle { get; set; } = new();
    public FaceTrackingAutoFixConfig FaceTrackingAutoFix { get; set; } = new();
    public SRanipalServiceConfig SRanipalService { get; set; } = new();
    public SteamVrStuckSessionConfig SteamVrStuckSession { get; set; } = new();
    public VrcFaceTrackingLifecycleConfig VrcFaceTrackingLifecycle { get; set; } = new();
    public VrcOscLifecycleConfig VrcOscLifecycle { get; set; } = new();
    public VirtualHereSRanipalLifecycleConfig VirtualHereSRanipalLifecycle { get; set; } = new();
    public SlimeVrLifecycleConfig SlimeVrLifecycle { get; set; } = new();
    public OptimizationsConfig Optimizations { get; set; } = new();
    public SessionFlowConfig SessionFlow { get; set; } = new();
    public VrChatGroupAutomationConfig VrChatGroupAutomation { get; set; } = new();
    public List<TrackerConfig> Trackers { get; set; } = new();
    public List<EyeCameraConfig> EyeCameras { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
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
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(directory)
                    .AddJsonFile(Path.GetFileName(path), optional: false, reloadOnChange: false)
                    .Build();

                var loaded = configuration.Get<MonitorConfig>();
                if (loaded is not null)
                    return loaded;

                Log.Warn("Config", $"'{path}' parsed to nothing usable — starting with in-memory defaults instead. The file itself is left untouched; fix it by hand or re-save settings from the app to overwrite it.");
            }
            catch (Exception ex)
            {
                // A crash or a race mid-write (see Save's atomic-replace below) could otherwise
                // leave appsettings.json truncated/corrupt, which used to prevent the app from
                // starting at all — nothing further up the call chain to Program.Main caught this.
                // Deliberately does NOT overwrite the file here: it's left in place for manual
                // recovery/inspection, and only gets replaced if/when the app later saves settings.
                Log.Warn("Config", $"Failed to load '{path}' ({ex.GetType().Name}: {ex.Message}) — starting with in-memory defaults instead of crashing. The file itself is left untouched.");
            }

            return CreateDefault();
        }

        var defaultConfig = CreateDefault();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(defaultConfig, JsonOptions));
        return defaultConfig;
    }

    /// <summary>Writes via a temp file + atomic swap rather than a direct File.WriteAllText, so a
    /// crash or a concurrent Save race (see OptimizationsManager's _saveLock — this method itself
    /// isn't synchronized, callers are responsible for that) can't leave appsettings.json
    /// truncated/half-written on disk.</summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));

        if (File.Exists(path))
            File.Replace(tempPath, path, null);
        else
            File.Move(tempPath, path);
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
