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
/// UNTESTED against a live server as of 2026-08-10 (see SlimeVrDiscovery for the same caveat on the
/// connect/parse shape this mirrors) — live verification is Task 5 of the SlimeVR auto-stop plan.
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
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("SlimeVrMotion", $"Started. Polling ws://{Host}:{Port} every {_config.SlimeVrLifecycle.PollIntervalMs}ms.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("SlimeVrMotion", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await PollOnceAsync(token).ConfigureAwait(false);
            try { await Task.Delay(_config.SlimeVrLifecycle.PollIntervalMs, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        List<TrackerRotation> rotations;
        try
        {
            rotations = await FetchRotationsAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let a connect/send/receive/parse failure escape the poll loop.
            Log.Debug("SlimeVrMotion", $"Poll threw, treating as empty (idle): {ex.Message}");
            rotations = new List<TrackerRotation>();
        }

        _lastRotationCount = rotations.Count;
        _detector.Observe(rotations, DateTime.UtcNow);
        TrackersIdle = _detector.IsIdle;
        Log.Trace("SlimeVrMotion", $"Observed {rotations.Count} tracker rotation(s) -> TrackersIdle={TrackersIdle}");
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

            using var ms = new MemoryStream();
            var buffer = new byte[8192];
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

            result = ParseRotations(ms.ToArray());
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

    private static List<TrackerRotation> ParseRotations(byte[] data)
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

            for (var j = 0; j < update.SyntheticTrackersLength; j++)
            {
                if (update.SyntheticTrackers(j)?.Rotation is { } quat)
                    rotations.Add(new TrackerRotation(trackerId++, quat.X, quat.Y, quat.Z, quat.W));
            }

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
        Stop();
        _cts?.Dispose();
    }
}
