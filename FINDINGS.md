# Session Findings — 2026-07-21 / 2026-07-22

Live-debugging notes from a session where VRChat auto-launched into desktop mode, SteamVR came up
stuck (black view), and face/eye tracking stopped working. Kept as the working record — update in
place rather than appending a new file next time.

## Confirmed bugs (fixed)

### 1. VRChat launched anyway when VD wasn't actually streaming
`SessionOrchestrator.RunSessionStartAsync` waited up to 60s for a confirmed VD stream
(`WaitForHeadsetStreamAsync`), but on timeout it just logged a warning and launched VRChat anyway.
The headset's "online" signal is a plain ICMP ping to its LAN IP — true the moment it's powered on
and on Wi-Fi, long before Virtual Desktop is actually opened/streaming. Result: VRChat launched
into its background-mode flatscreen window (no real VR runtime attached yet) whenever the headset was
merely reachable.

**Fix** (commit `6dbf1d0`): skip the VRChat launch entirely when the stream isn't confirmed, log a
warning, and let the next headset-online cycle try again.

### 2. Steam (and thus SteamVR) launched on the same premature signal
`LaunchCoreAppsAsync` launched both `VirtualDesktop.Streamer.exe` and `steam.exe` unconditionally
as soon as the headset pinged online — same root cause as #1. Steam launching on this machine
appears to auto-start SteamVR, which then errors with no real HMD attached.

**Fix** (uncommitted as of this writing — see Outstanding below): split `LaunchCoreAppsAsync` into
`LaunchVdStreamerAsync` (still eager — VD Streamer has to be running to *receive* the incoming
connection) and `LaunchSteamAsync` (moved to only run after `WaitForHeadsetStreamAsync` confirms a
real stream, alongside VRChat and SlimeVR).

### 3. Stale binary running for 4+ days
The actual running tray process (found via `Get-Process`/`Get-CimInstance`) had a `StartTime`/exe
`LastWriteTime` of 2026-07-17 01:5x — the very first build, from *before* the OUI-detection commit,
the tray-icon commit, and the VRChat-gating fix above. It had simply never been restarted since,
so none of several days' worth of committed fixes were actually running. Confirmed by checking the
running exe's path, PID start time, and file mtime against `git log`.

**Lesson**: there's currently no way to tell from the tray app itself that it's running stale code.
See improvement idea below (log a build identifier on startup).

### 4. SlimeVR GUI genuinely launches twice (confirmed 2026-07-22, corrects an earlier wrong conclusion)
Originally (2026-07-21) dismissed this as a false alarm — `ProcessLauncher.EnsureRunningAsync`
correctly logged `'SlimeVR' already running, skipping launch` on every repeat cycle, and the
second `SlimeVR`-named process cluster was assumed to be harmless SteamVR-driver companion
processes. **That was wrong** — it was based on `Get-Process` names alone, not full command lines.
Checking `Get-CimInstance Win32_Process` command lines on 2026-07-22 showed the two clusters are
genuinely different launches: our own (`slimevr.exe`, lowercase, no args, matching
`Paths.SlimeVrExe` exactly) and a second, independent one 25s later (`SlimeVR.exe -- --steam`,
uppercase, with its own full set of GPU/renderer/utility child processes) fired right after
`SlimeVR-Bindings-Provider.exe` (SlimeVR's SteamVR-driver bridge) loaded. SlimeVR's own SteamVR
driver integration auto-launches the full GUI itself once SteamVR loads it as a registered driver
— entirely independent of this app — and since our own launch already fired ~25s earlier, both
end up running side by side as two real windows.

**Fix**: added `SessionFlowConfig.SlimeVrLaunchDelayMs` (default 35000) — `LaunchSlimeVrAsync` now
waits this long before even checking/launching, giving the driver-triggered auto-launch a chance
to land first so our own `EnsureRunningAsync`'s "already running" check just skips. Kept as a
delayed safety net rather than removing our own launch entirely, in case the driver-triggered
auto-launch doesn't fire in every scenario (only observed the one time).

**Lesson**: `Get-Process`/`ProcessLauncher`'s own logs only prove *this app's* launch calls were
deduped correctly — they say nothing about a completely separate program (SteamVR's driver bridge,
in this case) independently launching the same exe. Check full command lines
(`Get-CimInstance Win32_Process`) before concluding a second process cluster is "just a driver
helper."

### 5. VD-Streamer-PID ping-flap guard skipped an entire overnight session start (confirmed 2026-07-26)
Commit `a2ad10c` (2026-07-24) added `_launchChainCompletedForVdPid` to stop a genuine ping flap
(Quest's Wi-Fi blipping for a few seconds) from re-running the whole launch chain when VD Streamer
itself was never touched. The guard matches purely on VD Streamer's PID being unchanged, with no
time bound — but VD Streamer can legitimately keep running for hours across a real headset-off
period without crashing. This morning the headset went offline at `00:34:50` and didn't come back
online until `11:20:05` — an 11-hour real overnight gap — but VD Streamer's PID (10592) never
changed, so the orchestrator logged "this is a headset ping flap, not a new VD session" and
silently skipped launching Steam/VRChat/SlimeVR/OVR Toolkit for a genuinely new session. Confirmed
via `Get-Process` (VD Streamer PID unchanged since before midnight) cross-referenced against
`monitor_2026-07-26.log`'s `HeadsetMonitor`/`Orchestrator` lines.

**Fix**: added `SessionFlowConfig.MinHeadsetOfflineDurationForNewSessionMs` (default 90000ms).
`SessionOrchestrator` now tracks when the headset was last seen going offline
(`_headsetWentOfflineAtUtc`) and only treats a same-PID match as "just a flap" if that gap was
shorter than the threshold — otherwise it reruns the full launch chain regardless of VD Streamer's
PID. 90s is comfortably above every observed real flap (~5s in this machine's logs) and comfortably
below any gap that represents an actual new session.

**Lesson**: a "same instance, skip re-run" guard keyed on a long-lived companion process's PID
needs an explicit time bound — "the PID didn't change" only proves "no crash happened," not "no
real session boundary occurred," whenever that companion process can outlive the session it's
nominally tied to.

### 6. VRCFaceTracking's stale-handshake detection missed the "launched during VRChat's absence" case (confirmed 2026-07-25, re-confirmed 2026-07-26)
`VrcFaceTrackingLifecycle.cs`'s `CheckVrChatRestart` (added 2026-07-21 to catch VRChat restarting
while VRCFaceTracking was already running) tracked only the previous poll's VRChat start time, and
reset that tracking to `null` the instant VRChat wasn't running. Real incident 2026-07-25: VRChat
was closed for ~18 hours (`03:27:22` → `21:31:48`); VRCFaceTracking launched via tracker-presence
detection at `21:23:23`/`21:27:05` during that gap; when VRChat finally restarted at `21:31:48`,
the reset-to-null wiped out the only reference point the check needed, so the restart was never
flagged. Every other health signal (`moduleConnectedToSRanipal=True`, module processes alive,
SRanipal connected) kept reading perfectly healthy for the next ~20 minutes regardless, because
none of them check *which* VRChat instance the OSC handshake actually points at — face tracking
was silently dead in-game the whole time.

**Fix**: replaced the previous-poll comparison with `_refreshedForVrChatStartTime`, tracking the
specific VRChat instance (by `StartTime`) already handled — this can't be wiped out by a gap since
it's only ever compared against whichever VRChat instance is actually running right now. Confirmed
working live twice: once at `21:52:10` on 2026-07-25 (the moment the patched build first ran, it
immediately caught and restarted the already-stale VRCFaceTracking), and again on 2026-07-26 at
`11:24:16` — mid-session, no monitor restart involved — when VRCFaceTracking (launched `11:19:07`)
was correctly flagged stale 5 seconds after VRChat started at `11:24:11` and restarted on its own.

**Lesson**: same failure class as finding #5 — a "have I already handled this" guard needs to key
on the identity of the specific instance being compared against, not on "did the last poll see a
change," whenever the state being watched can disappear and reappear with no comparison basis left
in between.

## False leads (worth remembering so they don't get re-chased)

### `VrChatBackgroundMode` (formerly `VrChatLowPowerMode`) does not control VR vs. desktop rendering
Initially assumed the background-mode toggle (`-screen-width 1024 -screen-fullscreen 0` etc.) was
why VRChat wasn't appearing in the headset, and flipped it off. Wrong: those launch args only shape
VRChat's flatscreen companion window (size/fullscreen). Whether it renders into the headset is
automatic once SteamVR is active, independent of this toggle. Reverted the config change.
Follow-on mistake: when manually relaunching VRChat after a SteamVR restart, used the *non*-background
args instead of reading the actual configured value, producing an unwanted maximized window that
had to be corrected. **Any manual relaunch needs to read current config and build args exactly as
`SessionOrchestrator.LaunchVrChatAsync` would — don't hand-type them.**

### The avatar's duplicate PhysBone parameter was a red herring for the FT-not-working investigation
VRChat's own log showed a recurring `OSC:: Trying to add existing endpoint
/avatar/parameters/MPB_R_Squish1` (and `_L_Squish1`) error on every avatar load. Hypothesized this
aborted the rest of OSC parameter registration, explaining why the generated avatar OSC config
(`%AppData%\..\LocalLow\VRChat\VRChat\OSC\<usr>\Avatars\<avtr>.json`) appeared to have zero
face-tracking parameters. **This was wrong** — caused by a grep pattern bug on the investigating
side (`"address":"value"`, assuming no space after the colon) against a file that was actually
pretty-printed (`"address": "value"`, with a space). Re-run with the correct pattern found 159
`EchoFT/v2/...` face-tracking parameters present and correctly registered. The duplicate-parameter
log error is very likely harmless/pre-existing and unrelated. **Real fix for face/eye tracking not
working**: physical hardware issue — replugging the Vive Facial Tracker fixed face tracking;
manually stopping/restarting the cameras inside Baballonia fixed eye tracking. Nothing wrong with
the avatar, the OSC pipeline, or VrSessionMonitor's code.

**Lesson**: when a JSON-scraping grep/search comes back suspiciously empty, verify the actual
on-disk formatting before trusting a negative result — don't build a whole theory on it.

### SteamVR came up as a genuine stuck/zombie session (not caused by VrSessionMonitor)
`vrserver`/`vrcompositor` were alive as OS processes but never produced real log output —
`vrserver.txt` and `vrclient_vrcompositor.txt` were 0 bytes, and the primary `vrcompositor.txt`
hadn't been touched since the *previous day* despite the process supposedly starting fresh. This
lines up exactly with the reported black view in the headset and SlimeVR's tracker driver failing
to register. Fixed by killing `vrserver`/`vrmonitor`/`vrcompositor` and manually forcing a fresh
SteamVR launch via `steam://rungameid/250820` (confirmed healthy afterward: real-sized logs,
no fatal errors, just normal SteamVR startup noise). VrSessionMonitor's `SteamVrMonitor` only
checks *process presence*, not whether the compositor is actually producing output, so it did not
detect or self-heal this — see improvement idea below.

### OSC `/avatar/change` doesn't work from a raw one-off UDP packet
Tried sending a manually-constructed OSC packet to `127.0.0.1:9000` to trigger an avatar switch.
Had no effect (no corresponding "Loading Avatar Data" log line). VRChat's log shows it actively
discovers other apps as **OSCQuery** clients (`Found new OSC Service: VRCFT-G33QN5...`,
`OVRToolkit-87162...`) — plausible that `/avatar/change` specifically requires the sender to be a
recognized OSCQuery client, unlike plain parameter puppeting which may work over raw UDP regardless.
Not investigated further; avatar switching was done manually in VR instead.

## Other notes

- `vrchat-osc-mcp` (local project at `C:\Users\<user>\git\vrchat-osc-mcp`) registered as a
  user-scope (global) MCP server via `claude mcp add vrchat-osc -s user -- ...`. Showed "Failed to
  connect" in `claude mcp list` immediately after adding — likely just needs a fresh Claude Code
  session to pick up the new user config, not yet confirmed working.
- OVR Toolkit's `favourites.json` was empty (`Titles: []`, `ProcessNames: []`) — it only auto-loads
  windows you've explicitly pinned via its in-VR window picker; there's no static "always capture
  the first display" setting to fall back on. Fixed 2026-07-22: `windows.json` already had a saved
  `\\.\DISPLAY1` capture entry (position/rotation/size) from a prior session, just never
  favourited — added it to `favourites.json` (`Titles: ["\\\\.\\DISPLAY1"]`,
  `ProcessNames: ["null"]`, `IsWebcam: [false]`) so it auto-loads on startup. Confirmed working
  (`Player.log`: "Toggled on 1 windows!").
- **OVR Toolkit needs admin elevation, and needs to be launched via Steam, not its exe directly**
  (confirmed 2026-07-22): a direct `OVR Toolkit.exe` launch — even with `-Verb RunAs` via a manual
  UAC approval — logged `Process is not running as admin or has failed to get the right elevation
  level!` followed by its bridge process and WebSocket server failing, and it exited shortly after.
  Launching via `steam://rungameid/1068820` (its Steam App ID, confirmed via its `appmanifest_*.acf`
  and matching the `steam.overlay.1068820` references already seen in VRChat's own log) worked
  cleanly with no elevation warning. Reason: Windows UIPI (User Interface Privilege Isolation)
  blocks a standard-privilege process from capturing/interacting with windows owned by an elevated
  process (Steam/SteamVR/some games can run elevated) — a universal window-mirroring tool like this
  needs to run elevated itself to see past that boundary, and Steam evidently handles that
  elevation via its own per-app compatibility settings when launched through it.

## Improvements implemented (2026-07-22)

Fixes driven directly by the findings above — see individual doc comments in the code for details:

- **SteamVR stuck-session detection** (`SteamVrMonitor.cs`, `SteamVrStuckSessionConfig`) —
  compares `vrserver.txt`/`vrcompositor.txt` last-write-time against the process's own start time;
  auto-restarts via `steam://rungameid/250820` if neither has been written to since start, with a
  grace period and a give-up cooldown (mirrors the `FaceTrackingAutoFixConfig` pattern).
- **VRCFaceTracking refresh on VRChat restart** (`VrcFaceTrackingLifecycle.cs`,
  `CheckVrChatRestart`) — restarts VRCFaceTracking if VRChat's process start time is newer than
  VRCFaceTracking's own, so a stale OSC/OSCQuery handshake against a dead VRChat instance can't
  silently persist.
- **Tray "Restart VRChat now" action** (`SessionOrchestrator.RestartVrChatAsync`) — kills any
  running VRChat and relaunches through the same `DoLaunchVrChatAsync` code path the automatic flow
  uses, so a manual restart always respects the current background/fullscreen config instead of
  being hand-typed.
- **Startup build-timestamp log line** (`TrayApplicationContext` constructor) — logs the running
  exe's own `LastWriteTime` so a stale build (like the 4-day-old one found tonight) is visible in
  the log immediately, without needing `Get-Process`/file-mtime comparisons.
- **SlimeVR launch delay** (`SessionOrchestrator.LaunchSlimeVrAsync`,
  `SessionFlowConfig.SlimeVrLaunchDelayMs`, default 35s) — see finding #4 above.
- **Selectable VR overlay auto-launch** (`SessionOrchestrator.LaunchVrOverlayAsync`,
  `SessionFlowConfig.VrOverlay`, `VrOverlayChoice` — None/OvrToolkit/XSOverlay, default
  XSOverlay) — launches the chosen overlay via `steam://rungameid` (OVR Toolkit
  `1068820`/`PathsConfig.OvrToolkitSteamAppId`, XSOverlay `1173510`/`PathsConfig.XSOverlaySteamAppId`)
  rather than through `ProcessLauncher` (which requires a real file path, not a URL) or the exe
  directly (which hits the elevation bug above). Chosen from the Settings tab's overlay dropdown,
  replacing the old single `AutoLaunchOvrToolkit` toggle.

All compiled clean (verified via a scratch `-o` build directory, since the live tray process
still had the real `bin/` output locked at the time).

## Outstanding / not yet done

- [ ] Commit + push all changes on disk (Steam-gating split, SlimeVR delay, OVR Toolkit
  auto-launch, and the earlier 4 resilience improvements) — none of it is committed yet.
- [ ] Rebuild + restart the live tray process to actually pick up any of this (not done yet — kept
  deferring since the process was live/in-session each time).
- [ ] Confirm `vrchat-osc-mcp` actually connects in a fresh session.
