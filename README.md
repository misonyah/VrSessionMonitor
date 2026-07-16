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
  trackers, confirms the actual VD video stream (not just a port match), then launches VRChat and
  SlimeVR through a launcher that serializes on process name so nothing gets double-started.
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
- **SteamVR + VRChat presence monitoring**, logged on every state change.
- **SteamVR in-headset toast notifications** for key auto-fix events (only when SteamVR is
  actually running).
- **Auto-detect headset/trackers/cameras** — a tray action that ping-sweeps the LAN for a
  Meta/Oculus MAC OUI prefix, queries SlimeVR's own local API, and reads Baballonia's camera
  address fields directly, instead of requiring manual IP/MAC entry. See the note below for what
  is and isn't verified yet.
- **Start with Windows** toggle, and a single-instance lock so a second launch can't collide with
  the first.
- Everything is toggleable from the tray menu, with live status for headset/trackers/eye+face
  pipeline/SteamVR/VRChat/last firmware self-heal event.

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
   needs editing). Alternatively, run the app once and use the tray menu's **"Auto-detect
   headset/trackers/cameras"** action.
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

## Config reference

`appsettings.json` is a plain JSON tree, loaded via `Microsoft.Extensions.Configuration` (saved
back out with `System.Text.Json`, since `IConfiguration` itself is read-only). Top-level
sections: `Network`, `Polling`, `Paths`, `Updates`, `Adb`, `EyeCameraAutoRestart`,
`BaballoniaLifecycle`, `FaceTrackingAutoFix`, `VrcFaceTrackingLifecycle`, `SessionFlow`,
`Trackers` (a list), `EyeCameras` (a list). Every field has a doc comment on its C# property in
`Config/MonitorConfig.cs` explaining what it does and, where relevant, why it has the default
value it does.

## License

MIT.
