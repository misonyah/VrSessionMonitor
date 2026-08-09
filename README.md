# VR Session Monitor

A Windows tray app that watches a PCVR session end-to-end — headset, SlimeVR trackers, eye/face
tracking, SteamVR, VRChat — and automatically launches, self-heals, and reports on all of it. It
replaces a set of fragile `.cmd` scripts (headset detection via port-sniffing, no real launch
serialization, no self-healing at all) with a proper background watchdog.

This is a personal-hardware tool, built for one specific rig (Quest 2 + Virtual Desktop, SlimeVR
full-body tracking, a Vive Facial Tracker over SRanipal/VRCFaceTracking, Baballonia for eye
tracking). It's shared as a reference/starting point, not a polished general product — expect to
read the code and adjust things for your own setup.

## What it does

- **Headset detection** — pings your headset's LAN IP directly rather than sniffing for a
  Virtual Desktop connection, which turned out to false-positive on VD's own outbound WAN traffic
  before the headset was even connected.
- **Session-start orchestration** — once the headset comes online: pre-flight checks your SlimeVR
  trackers, launches VD Streamer (needed to receive the connection), then confirms an actual VD
  video stream (not just a port match) before launching anything else — Steam, VRChat, SlimeVR, and
  OVR Toolkit all wait for that confirmation, so nothing spins up on a headset that's merely powered
  on but not actually streaming. Every launch goes through a launcher that serializes on process
  name so nothing gets double-started.
- **SlimeVR tracker monitoring** — pings every tracker board directly, independent of whether the
  SlimeVR server is even running, so a dead tracker shows up before you're already in a world.
- **Eye tracking (Baballonia)** — detects camera presence/streaming via real TCP connection state
  (not just "is the process alive"), auto-restarts a camera that's on the network but not
  streaming via UI Automation, and auto-closes Baballonia if every camera goes away. Includes a
  ping-debounce (a single dropped packet doesn't look like a power-cycle) learned the hard way
  after it caused restart storms.
- **Face tracking (SRanipal / VRCFaceTracking / Vive Facial Tracker)** — checks the actual
  module↔SRanipal TCP connection and the tracker's real USB attach state (via WMI, not the
  `Get-PnpDevice` cmdlet, which returns stale ghost entries). Self-heals a stalled connection by
  restarting SRanipal, escalating to a full VRCFaceTracking restart if that alone doesn't recover
  it, with a backoff if even that keeps failing. Also periodically restarts VRCFaceTracking on a
  long-uptime timer, after it was found to silently wedge its OSC output after several hours with
  no other visible symptom.
- **VRCFaceTracking / eye-tracking lifecycle** — starts VRCFaceTracking the moment a tracker or
  eye camera is detected, shuts it down after a delay once neither is present.
- **OSC ground-truth freshness checks (eye + face)** — every check above only observes the
  transport (TCP connections, process presence), never the actual data. Confirmed live
  2026-07-30: both the eye-camera "streaming" check and the face-tracking "connected to SRanipal"
  check can read perfectly healthy while VRChat's own tracking parameters sit completely frozen
  (verified directly against VRChat's own OSCQuery `/avatar/parameters` endpoint — one incident had
  eye gaze/eyelids frozen at exactly 0, another had `JawOpen`/`MouthClosed` frozen while unrelated
  jitter channels kept moving, which is why this tracks each parameter's own change history and
  votes on a majority rather than requiring the whole bundle to be identical). An eye-tracking
  freeze forces a Stop+Start Camera cycle on both eyes; a face-tracking freeze feeds into the
  existing SRanipal stalled-connection escalation ladder above, as a stronger signal than the TCP
  check alone. Polls infrequently (every 10s by default) and only once the cheaper transport-level
  checks already look healthy, so it's a second opinion, not a replacement.
- **SteamVR + VRChat presence monitoring**, logged on every state change.
- **SteamVR stuck-session detection** — vrserver/vrcompositor can come up as live OS processes
  while never actually producing real log output (a black view in the headset, confirmed live
  2026-07-21). Detected by checking whether their log files have been written to since the process
  started, and auto-restarted (kill + relaunch via `steam://rungameid/250820`) with a give-up
  cooldown so a persistently broken SteamVR doesn't get restart-looped forever.
- **VRCFaceTracking refresh on VRChat restart** — if VRChat restarts while VRCFaceTracking is
  already running, its OSC/OSCQuery handshake can go stale against the new instance even though
  every other health signal still looks fine. Detected by comparing process start times; restarts
  VRCFaceTracking automatically so the next poll cycle relaunches it fresh.
- **SlimeVR launch delay** — SteamVR auto-launches SlimeVR's own GUI itself (via its OpenVR driver
  bridge) a few seconds after it loads, independent of this app. Launching SlimeVR immediately
  raced that and produced two real GUI windows (confirmed live 2026-07-22); a configurable delay
  before this app's own launch attempt lets the driver's auto-launch land first.
- **OVR Toolkit auto-launch** — launched via `steam://rungameid/<PathsConfig.OvrToolkitSteamAppId>`
  rather than its exe path directly. A direct exe launch was found to skip OVR Toolkit's own
  admin-elevation handshake and crash shortly after starting; going through Steam's launch protocol
  avoids that.
- **SteamVR in-headset toast notifications** for key auto-fix events (only when SteamVR is
  actually running).
- **Auto-detect headset/trackers/cameras** — a Settings-tab action that ping-sweeps the LAN for a
  Meta/Oculus MAC OUI prefix, queries SlimeVR's own local API, and reads Baballonia's camera
  address fields directly, instead of requiring manual IP/MAC entry. See the note below for what
  is and isn't verified yet.
- **Start with Windows** toggle, and a single-instance lock so a second launch can't collide with
  the first.
- **Manual "Restart" button next to the VRChat status row** — kills any running VRChat and
  relaunches it through the same code path (and current config) as the automatic flow, so a manual
  restart can't drift from your configured background/fullscreen preference (a real mistake made
  once during live debugging, hand-typing the wrong args).
- Logs its own build timestamp on startup — compare against `git log` to catch a stale running
  build before chasing a "fixed" bug that's actually just not deployed yet (also confirmed live:
  a build ran unrestarted for 4 days across 3 subsequent fixes).
- **VRChat group → avatar parameter automation** — toggles a configured avatar OSC bool parameter
  true while you're in a specific VRChat group's instance, false otherwise. See its own section
  below.
- Everything is toggleable from the Settings/Status window (see below), with live status for
  headset/trackers/eye+face pipeline/SteamVR/VRChat/last firmware self-heal event.

## Settings/Status window

Replaces what used to be a right-click tray context menu (that menu had a recurring WinForms
crash — see `docs/superpowers/specs/2026-08-09-settings-status-window-design.md` for the full
story). The tray icon now opens a normal window instead, and clicking the same button again while
that tab is already open closes it:

- **Left-click** → **Status** tab (live-updating: headset, trackers, eye/face tracking, SteamVR,
  VRChat, firmware self-heal).
- **Right-click** → **Settings** tab (launch toggles, network IPs, background-mode dimensions, and
  an Actions column pinned to the right so it's visible without scrolling).
- **Middle-click** → **Home Assistant** tab (only present in builds with that feature compiled in —
  connection setup, area picker, per-light action pickers for all three trigger categories).
- An **Automation** tab holds the VRChat group → OSC parameter config (see below), and an
  **Advanced** tab opens `appsettings.json` directly in your default editor for anything not
  promoted to the curated Settings tab.
- **Exit** is a button inside the window itself, not a menu item.

## Requirements

- Windows, .NET 10 SDK
- Whatever subset of these you actually use: SteamVR, Virtual Desktop (Streamer + headset app),
  SlimeVR Server, VRCFaceTracking, Baballonia, SRanipal (for a Vive Facial Tracker), VirtualHere
  (if your face tracker connects over USB passthrough from the headset)

## Setup

1. Copy `appsettings.example.json` to `appsettings.json`.
2. Fill in your own headset IP, tracker IPs/MACs, eye camera IPs, and any install paths that
   don't match the defaults (most third-party paths default to their usual Steam/Program Files
   location — only your own trackers/cameras/headset and anything installed somewhere unusual
   needs editing). Alternatively, run the app once and use the Settings tab's **"Auto-detect
   headset/trackers/cameras"** button.
3. `dotnet build`, then run `bin/Debug/net10.0-windows/VrSessionMonitor.exe`.

`appsettings.json` is gitignored — it holds your real network layout and is never meant to be
committed. `appsettings.example.json` (fake placeholder values) is the one that's tracked.

## A note on the auto-discovery feature

Three independent sources feed this, each with a different confidence level:

- **Headset** (`Modules/HeadsetDiscovery.cs`) — ping-sweeps your local /23-or-smaller subnet to
  populate Windows' ARP cache, then matches `arp -a` entries against known Meta/Oculus MAC OUI
  prefixes (verified against the IEEE-sourced vendor registry, not guessed — `2C:26:17` is
  independently confirmed real, it matches this rig's own Quest 2). **Verified working live**:
  ran it against the real LAN with the headset powered off, correctly found 0 matches among 54
  real ARP entries, confirming the whole pipeline (subnet detection → sweep → parse → match)
  without a false positive. The true-positive case (an actual Meta device present) is still
  unverified — should just work, but hasn't been observed directly. If more than one Meta device
  is found on the network, this won't guess which one is the headset; it logs every candidate
  and leaves `HeadsetIp` for you to set by hand.
- **SlimeVR** — over its local SolarXR WebSocket API (`ws://127.0.0.1:21110`), which turned out
  to be FlatBuffers-based rather than JSON — the bindings are vendored under
  `Modules/SolarXR/Generated/` (generated with `flatc` from
  [SolarXR-Protocol](https://github.com/SlimeVR/SolarXR-Protocol); regenerate from there if the
  protocol ever changes). Built from the real protocol schema, not a guess, but **not yet
  exercised against a live server** — SlimeVR wasn't running when this was written.
- **Baballonia** — reads its camera-address textboxes via Windows UI Automation. Built from real
  UI research, but likewise **not yet exercised live**.

If a source finds nothing, check the log — every call here is meant to fail gracefully and
report why rather than silently doing nothing.

## Home Assistant lights

Switches your Home Assistant lights automatically as the VR session changes state, with a separate
configurable light map per trigger — headset on, headset off, and AFK — each light set to On, Off,
or No change from the Home Assistant tab. AFK comes from two independent sources OR'd together:
SteamVR's own HMD activity level (headset taken off your face) and VRChat's own
`/avatar/parameters/AFK` parameter over OSC (AFK toggled from the quick menu while still wearing
the headset).

It's gated twice, and both gates have to be open for anything to happen:

- **`IncludeHomeAssistant`** — a compile-time MSBuild property, **on by default**. Build with
  `dotnet build -p:IncludeHomeAssistant=false` to compile the feature out entirely: no HA client,
  no OpenVR activity polling, no OSCQuery service advertised to VRChat, no Home Assistant tab (and,
  since the VRChat group automation feature currently shares the same OSC package references, no
  Automation tab either — see that section for why).
- **`HomeAssistant.Enabled`** — the runtime flag in `appsettings.json`, **off by default**. While
  it's false nothing connects, polls, or advertises itself, even in a build that includes the
  feature. The setup flow below sets this to `true` for you.

**Caveat before changing the compile flag on an existing install:** `MonitorConfig.Save()` does a
full-object rewrite, and a build made with `-p:IncludeHomeAssistant=false` has no `HomeAssistant`
property to write out. The first time any settings change triggers a config save, that whole
section — access token, selected area, and all three light maps — is silently dropped from
`appsettings.json`. Back the file up first if you care about those settings.

### Connecting it (all from the window, no JSON editing required)

1. Middle-click the tray icon (or right-click → Settings tab, if you'd rather navigate manually) →
   **Home Assistant** tab → **"Set up connection..."**.
2. Type your Home Assistant Base URL (e.g. `http://homeassistant.local:8123`).
3. Click **"Create token"** — opens that URL's `/profile/security` page in your browser using
   whatever you just typed (doesn't need anything saved yet). Scroll down to **"Long-lived access
   tokens"** on that page to create one.
4. Paste the token back into the dialog, click **Connect**.

That one click saves both values, sets `HomeAssistant.Enabled` to `true`, (re)starts the WebSocket
connection live — no app restart needed — and automatically runs area/light discovery once
connected. The **Area** picker and the three per-trigger light lists (**Headset On / Headset Off /
AFK**) populate right after; each light gets its own On / Off / No change choice, independently per
trigger. Use **"Refresh areas/lights"** later if you rearrange anything in Home Assistant itself.

## VRChat group → avatar parameter automation

**Not yet exercised live** — built and compiles clean, but hasn't been tested against a real
VRChat session yet.

Toggles a configured avatar OSC bool parameter true while you're in a specific VRChat group's
instance, false when you leave it or move to a different one. Detected by tailing VRChat's own log
file (`%LOCALAPPDATA%Low\VRChat\VRChat\output_log_*.txt`) for the group ID VRChat embeds directly
in an instance's join line (`...~group(grp_xxxxx)~groupAccessType(members)`) — no VRChat API login
needed, since that's the same instance-location string VRCX itself parses for the same purpose
(confirmed against VRCX's own `Dotnet/LogWatcher.cs` and `src/shared/utils/instance.js`).

Configure it on the **Automation** tab: enable it, then add a row per group with its Group ID
(`grp_...`), a display label (not sent anywhere, just for your own reference), and the avatar OSC
parameter name to toggle. Changes save immediately but only take effect after restarting the app —
like the rest of this app's config, there's no hot-reload.

Gated the same way as Home Assistant (see above) since it currently shares the same
`LucHeart.CoreOSC`/`VRChat.OSCQuery` package references, gated behind `IncludeHomeAssistant` —
despite that flag's name, this feature has nothing to do with Home Assistant itself. OSC messages
are sent to VRChat's conventional default receive port (`127.0.0.1:9000`) rather than resolved via
OSCQuery discovery; if you run other local OSC routing tools that actually reassign VRChat's real
receive port, this would need to move to dynamic discovery instead (see
`Modules/VrChatGroupAutomationMonitor.cs`'s own doc comment).

## Config reference

`appsettings.json` is a plain JSON tree, loaded via `Microsoft.Extensions.Configuration` (saved
back out with `System.Text.Json`, since `IConfiguration` itself is read-only). Top-level
sections: `Network`, `Polling`, `Paths`, `Updates`, `Adb`, `HomeAssistant` (only in builds that
include the feature — see above), `EyeCameraAutoRestart`, `EyeTrackingOscFreshness`,
`BaballoniaLifecycle`, `FaceTrackingAutoFix` (includes the `OscFreshness*` fields),
`SRanipalService`, `SteamVrStuckSession`, `VrcFaceTrackingLifecycle`, `VrcOscLifecycle`,
`SessionFlow`, `VrChatGroupAutomation` (only in builds that include the Home Assistant feature —
see above), `Trackers` (a list), `EyeCameras` (a list). Every field has a doc comment on its
C# property in `Config/MonitorConfig.cs` explaining what it does and, where relevant, why it has
the default value it does.

## Known issues / TODO

- ~~Steam overlay never attaches to VRChat, breaking in-game payment UI (e.g. gift-subbing
  VRC+).~~ — **resolved 2026-08-09.** VRChat previously launched directly via its own `launch.exe`
  (wrapped by VD Streamer, so the background-mode windowed args — `-monitor`, `-screen-width`,
  etc. — could be passed), never through `steam://rungameid/438100`. Steam's overlay injection
  only happens at the moment *it* creates a game's process, so it never attached — confirmed live
  2026-07-28 via `RunningAppID` staying pinned to OVR Toolkit's app ID (which *is* launched via
  `steam://rungameid/`) the entire time VRChat was running. Same root category as the OVR Toolkit
  elevation-handshake fix from 2026-07-22 and the same fix: launch via `steam://rungameid/438100`
  instead. The background-mode window args can no longer be passed dynamically per launch (steam://
  takes no arguments) — they now need to be set once as VRChat's static Steam "Launch Options"
  instead (right-click VRChat in Steam > Properties > Launch Options). VD Streamer no longer wraps
  VRChat's launcher at all; it's launched standalone in the background (confirmed live that VD's
  streaming only needs VD Streamer running, not to be VRChat's parent process).

- **VRChat's background-mode window now gets explicitly minimized via `ShowWindow`/`SW_MINIMIZE`**
  (`SessionOrchestrator.MinimizeVrChatWindowAsync`, added 2026-07-28) — `ProcessLauncher`'s
  `WindowStyle=Minimized` hint never reached VRChat's actual window (STARTUPINFO hints don't
  propagate to processes a launched process spawns internally, which mattered back when VD
  Streamer still wrapped the launch — now VRChat launches via `steam://` instead, so there's no
  process handle of ours to set that hint on at all either way). This explicit minimize-after-launch
  step is unaffected by the steam:// launch change and still runs the same way.

- ~~Root cause of the 2026-07-27 face-tracking outage was never conclusively found~~ — **resolved
  2026-07-30**, generalized rather than root-caused for that specific incident. The exact trigger
  that night is still unconfirmed (the `SRanipalService` Windows Service angle was a dead lead —
  orphaned registration, binary doesn't exist on this machine, disabled in config), but the
  *mechanism* is now understood and monitored directly: `ModuleConnectedToSRanipal`'s TCP-ESTABLISHED
  check (and `EyeCameraStatus.Streaming`'s equivalent) can read healthy against a zombie
  connection while VRChat's own OSC output is actually frozen. Confirmed live twice more the same
  night this was built (eye gaze/eyelids frozen at exactly 0; separately, `JawOpen`/`MouthClosed`
  frozen while unrelated jitter channels kept moving) — see the OSC ground-truth freshness checks
  above. The "Restart face-tracking pipeline" tray action remains available for manual use, but the
  automated checks now catch this class of failure on their own.

## License

MIT.
