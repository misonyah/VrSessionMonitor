using Google.FlatBuffers;
using System.Net.WebSockets;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using solarxr_protocol;
using solarxr_protocol.data_feed;
using solarxr_protocol.data_feed.device_data;
using solarxr_protocol.data_feed.tracker;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Polls SlimeVR Server's own SolarXR WebSocket API (ws://127.0.0.1:21110, same endpoint as
/// SlimeVrDiscovery) for tracker ROTATIONS every SlimeVrLifecycleConfig.PollIntervalMs, and feeds
/// each snapshot into a single TrackerIdleDetector to decide whether the trackers are motionless.
///
/// Mirrors SlimeVrDiscovery's connect/send/receive/parse shape (one-shot PollDataFeed per cycle —
/// simple, and cheap enough at a ~2s cadence — rather than a persistent StartDataFeed subscription),
/// but the DataFeedConfig here asks for a rotation-only TrackerDataMask instead of DeviceDataMask's
/// device_data flag: it is set BOTH as DataFeedConfig.SyntheticTrackersMask (for SlimeVR's own
/// computed/synthetic trackers) and as DeviceDataMask.TrackerData (for each physical device's own
/// trackers), so whichever collection the server actually populates is read. Two independent
/// TrackerDataMask tables are built (not one shared offset) to avoid any doubt about whether a
/// FlatBuffers offset may safely be referenced from two different parent tables.
///
/// TrackersIdle defaults to true ("idle") until the first successful read proves otherwise, so a
/// SlimeVR/SolarXR that never starts (or hasn't been polled yet) never blocks
/// SlimeVrLifecycleManager's shutdown decision. Any connection or parse failure — SlimeVR not
/// running, the WebSocket refusing/closing, a malformed reply — is treated exactly like an empty
/// tracker snapshot (TrackerIdleDetector.Observe([], now), which resolves to idle immediately) and
/// the loop continues; nothing here is allowed to throw out of the poll loop. Logged at
/// Debug/Trace only so a not-running SlimeVR doesn't spam Info/Warn.
///
/// Confirmed live 2026-08-11 (Task 5 of the SlimeVR auto-stop plan): SyntheticTrackers reliably
/// returns real tracker rotations (14 on this rig) every poll. See FetchRotationsAsync and
/// ParseRotations for two live-only issues this uncovered and worked around: an unsolicited
/// legacy JSON frame sent on every connect, and a parse failure isolated to the redundant
/// per-device Trackers vector.
/// </summary>
public sealed class SlimeVrMotionMonitor : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 21110;
    private const int PollTimeoutMs = 3000;

    private readonly MonitorConfig _config;
    private readonly TrackerIdleDetector _detector;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _lastRotationCount = -1; // -1 = never successfully polled yet

    /// <summary>True once the trackers have been motionless for the configured window (or nothing
    /// is reporting at all). Defaults true so this never blocks a shutdown decision before the
    /// first successful poll.</summary>
    public bool TrackersIdle { get; private set; } = true;

    public SlimeVrMotionMonitor(MonitorConfig config)
    {
        _config = config;
        _detector = new TrackerIdleDetector(_config.SlimeVrLifecycle.IdleAngleThresholdDeg, _config.SlimeVrLifecycle.IdleWindowMs);
    }

    public void Start()
    {
        // When the auto-stop feature is off there's no consumer for TrackersIdle, so don't open a
        // WebSocket to SlimeVR and poll it every couple seconds for nothing. TrackersIdle stays at
        // its safe default (true); SlimeVrLifecycleManager is likewise disabled and never reads it.
        if (!_config.SlimeVrLifecycle.Enabled)
        {
            Log.Info("SlimeVrMotion", "SlimeVrLifecycle disabled — not polling SolarXR for tracker motion.");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("SlimeVrMotion", $"Started. Polling ws://{Host}:{Port} every {_config.SlimeVrLifecycle.PollIntervalMs}ms.");
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        cts.Dispose();
        Log.Info("SlimeVrMotion", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Belt-and-suspenders: PollOnceAsync already guards its own body end-to-end, but this
            // outer guard means a future edit inside PollOnceAsync can't silently kill the loop.
            try { await PollOnceAsync(token).ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("SlimeVrMotion", $"Poll cycle threw, continuing: {ex.Message}"); }

            try { await Task.Delay(_config.SlimeVrLifecycle.PollIntervalMs, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        // Entire body guarded — including Observe/TrackersIdle/logging — so nothing here (not just
        // the fetch) can ever throw out of the poll loop. Never let a connect/send/receive/parse or
        // detector failure escape unnoticed; treat it exactly like an empty (idle) snapshot.
        List<TrackerRotation> rotations;
        try
        {
            rotations = await FetchRotationsAsync(token).ConfigureAwait(false);

            _lastRotationCount = rotations.Count;
            _detector.Observe(rotations, DateTime.UtcNow);
            TrackersIdle = _detector.IsIdle;
            Log.Trace("SlimeVrMotion", $"Observed {rotations.Count} tracker rotation(s) -> TrackersIdle={TrackersIdle}");
        }
        catch (Exception ex)
        {
            Log.Debug("SlimeVrMotion", $"Poll threw, treating as empty (idle): {ex.Message}");
            rotations = new List<TrackerRotation>();
            _lastRotationCount = rotations.Count;
            _detector.Observe(rotations, DateTime.UtcNow);
            TrackersIdle = _detector.IsIdle;
        }
    }

    private async Task<List<TrackerRotation>> FetchRotationsAsync(CancellationToken ct)
    {
        var result = new List<TrackerRotation>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PollTimeoutMs);

        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{Host}:{Port}"), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug("SlimeVrMotion", $"Could not connect to SlimeVR's WebSocket API at {Host}:{Port} — is SlimeVR running? {ex.Message}");
            return result;
        }

        try
        {
            var request = BuildPollRotationsRequest();
            await ws.SendAsync(request, WebSocketMessageType.Binary, endOfMessage: true, cts.Token).ConfigureAwait(false);

            var buffer = new byte[8192];

            // Confirmed live 2026-08-11: on every new connection SlimeVR immediately pushes an
            // unsolicited legacy JSON "config" text frame (e.g.
            // {"type":"config","tracker_id":"SlimeVR Tracker 1","location":"hmd",...}) ahead of
            // the actual PollDataFeed reply — unrelated to SolarXR/FlatBuffers, seemingly one
            // per known tracker. The original single-receive version treated whatever arrived
            // first as the FlatBuffers response and threw (ArgumentOutOfRangeException) trying to
            // parse JSON text as a binary MessageBundle on every single poll. Loop over logical WS
            // messages, discarding non-Binary frames, until the real reply shows up or the shared
            // PollTimeoutMs budget (via cts) runs out.
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Debug("SlimeVrMotion", "SlimeVR closed the connection before replying.");
                        return result;
                    }
                    ms.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    Log.Trace("SlimeVrMotion", $"Ignoring non-binary WS message ({received.MessageType}, {ms.Length} bytes) while waiting for the PollDataFeed reply.");
                    continue;
                }

                result = ParseRotations(ms.ToArray());
                break;
            }
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }

        return result;
    }

    private static ArraySegment<byte> BuildPollRotationsRequest()
    {
        var builder = new FlatBufferBuilder(256);

        // Two independent rotation masks — one for the per-device tracker list, one for SlimeVR's
        // synthetic trackers — rather than reusing a single offset across both parent tables.
        var deviceTrackerMaskOffset = TrackerDataMask.CreateTrackerDataMask(builder, rotation: true);
        var deviceDataMaskOffset = DeviceDataMask.CreateDeviceDataMask(builder, tracker_dataOffset: deviceTrackerMaskOffset);
        var syntheticTrackerMaskOffset = TrackerDataMask.CreateTrackerDataMask(builder, rotation: true);

        var configOffset = DataFeedConfig.CreateDataFeedConfig(
            builder,
            data_maskOffset: deviceDataMaskOffset,
            synthetic_trackers_maskOffset: syntheticTrackerMaskOffset);
        var pollOffset = PollDataFeed.CreatePollDataFeed(builder, configOffset);
        var headerOffset = DataFeedMessageHeader.CreateDataFeedMessageHeader(
            builder, DataFeedMessage.PollDataFeed, pollOffset.Value);

        var headersVector = MessageBundle.CreateDataFeedMsgsVector(builder, new[] { headerOffset });
        var bundleOffset = MessageBundle.CreateMessageBundle(builder, data_feed_msgsOffset: headersVector);

        builder.Finish(bundleOffset.Value);
        return new ArraySegment<byte>(builder.SizedByteArray());
    }

    // internal (not private) so SlimeVrMotionMonitorParseTests can exercise the binary parse — the
    // JSON-frame-skip in FetchRotationsAsync and the synthetic-vs-device selection here are the two
    // live-only issues Task 5 uncovered, and this is their regression net.
    internal static List<TrackerRotation> ParseRotations(byte[] data)
    {
        var rotations = new List<TrackerRotation>();
        var bundle = MessageBundle.GetRootAsMessageBundle(new ByteBuffer(data));

        var trackerId = 0; // stable per-poll id; only needs to be unique within a single Observe() call
        for (var i = 0; i < bundle.DataFeedMsgsLength; i++)
        {
            var header = bundle.DataFeedMsgs(i);
            if (header is null || header.Value.MessageType != DataFeedMessage.DataFeedUpdate)
                continue;

            var update = header.Value.MessageAsDataFeedUpdate();

            var syntheticBefore = rotations.Count;
            for (var j = 0; j < update.SyntheticTrackersLength; j++)
            {
                if (update.SyntheticTrackers(j)?.Rotation is { } quat)
                    rotations.Add(new TrackerRotation(trackerId++, quat.X, quat.Y, quat.Z, quat.W));
            }

            // The per-device Trackers vector (DeviceDataMask.TrackerData) is only a FALLBACK for a
            // server that doesn't populate SyntheticTrackers — so only touch it when this update's
            // synthetic pass yielded nothing. Confirmed live 2026-08-11: SyntheticTrackers (SlimeVR's
            // own fused/computed skeleton trackers) parses cleanly and is populated on a normal rig
            // (14 on this one), which used to mean the block below still ran every poll and threw
            // ArgumentOutOfRangeException indirecting into Devices(0) — a swallowed exception + Debug
            // line on a ~2s cadence, forever, for data we never needed. Root cause of that throw was
            // never fully isolated (looked like a valid vector through DevicesLength, but element 0
            // read out of bounds; likely a build-vs-server SolarXR schema drift specific to
            // DeviceData/HardwareInfo, since SyntheticTrackers's TrackerData shares the struct layout
            // and parses fine). Kept as a guarded fallback so a synthetic-empty server still yields
            // rotations, and still isolated so a Devices problem can never discard synthetic ones.
            if (rotations.Count == syntheticBefore)
            {
                try
                {
                    for (var d = 0; d < update.DevicesLength; d++)
                    {
                        var device = update.Devices(d);
                        if (device is null) continue;

                        for (var t = 0; t < device.Value.TrackersLength; t++)
                        {
                            if (device.Value.Trackers(t)?.Rotation is { } quat)
                                rotations.Add(new TrackerRotation(trackerId++, quat.X, quat.Y, quat.Z, quat.W));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("SlimeVrMotion", $"Per-device Trackers fallback failed to parse (synthetic was empty; {rotations.Count} rotation(s) so far): {ex.Message}");
                }
            }
        }

        return rotations;
    }

    /// <summary>Human-readable summary for the tray, e.g. "SlimeVR trackers idle (12 tracked)". Null
    /// before the first poll has completed.</summary>
    public string? DescribeStatus()
    {
        if (_lastRotationCount < 0) return null;
        var state = TrackersIdle ? "idle" : "moving";
        return $"SlimeVR trackers {state} ({_lastRotationCount} tracked)";
    }

    public void Dispose()
    {
        // Stop() itself is idempotent (guards on _cts being null) and already disposes the CTS, so
        // Dispose() is safe to call more than once (and safe to call after an explicit Stop()).
        Stop();
    }
}
