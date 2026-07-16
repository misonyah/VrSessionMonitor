using Google.FlatBuffers;
using System.Net.WebSockets;
using VrSessionMonitor.Logging;
using solarxr_protocol;
using solarxr_protocol.data_feed;
using solarxr_protocol.data_feed.device_data;
using solarxr_protocol.datatypes.hardware_info;

namespace VrSessionMonitor.Modules;

public sealed class DiscoveredTracker
{
    public required string Name { get; init; }
    public required string Ip { get; init; }
    public required string Mac { get; init; }
}

/// <summary>
/// Queries SlimeVR Server's own SolarXR WebSocket API (ws://127.0.0.1:21110, confirmed live
/// 2026-07-17 via "[WebSocket] Web Socket VR Bridge started on port 21110" in SlimeVR's own log)
/// for its current device list, to auto-populate MonitorConfig.Trackers instead of requiring
/// manual IP/MAC entry.
///
/// SolarXR (github.com/SlimeVR/SolarXR-Protocol) turned out to be FlatBuffers-based, not JSON
/// or the protobuf a first search suggested — real integration meant generating C# bindings
/// with flatc (vendored under SolarXR/Generated/; regenerate from that repo's schema/ directory
/// if the protocol ever changes) against the Google.FlatBuffers NuGet runtime. IMPORTANT: the
/// flatc compiler version used for codegen MUST match the Google.FlatBuffers package version —
/// newer flatc emits a FlatBufferConstants.FLATBUFFERS_x_y_z() version-check call per generated
/// file that won't compile against an older runtime. Confirmed live 2026-07-17: scoop installed
/// flatc 25.12.19 while NuGet's newest Google.FlatBuffers was only 25.2.10 — had to download the
/// matching v25.2.10 flatc release directly from GitHub instead.
///
/// Protocol mechanics, verified against the real schema files (not guessed): send a
/// MessageBundle containing one DataFeedMessageHeader wrapping a PollDataFeed — a ONE-SHOT
/// request ("helpful when getting initial info about the device" per the schema's own doc
/// comment), not a persistent subscription — with DataFeedConfig.DataMask.DeviceData=true, as a
/// single binary WebSocket frame. The server replies with its own MessageBundle containing a
/// DataFeedUpdate whose Devices[] each carry a HardwareInfo with DisplayName, a packed MAC
/// (HardwareAddress.Addr, a uint64 with the 6 MAC bytes right-aligned in the low 48 bits per
/// the schema's doc comment), and an IP (Ipv4Address.Addr, a uint32 with the 4 octets packed
/// big-endian, also per the schema's doc comment).
///
/// UNTESTED against a live server as of 2026-07-17 — SlimeVR was not running when this was
/// written (see the vr-session-monitor memory for the fuller story). Verify end-to-end the next
/// time SlimeVR is up before trusting this for real tracker configuration.
/// </summary>
public sealed class SlimeVrDiscovery
{
    public async Task<List<DiscoveredTracker>> DiscoverTrackersAsync(string host = "127.0.0.1", int port = 21110, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var result = new List<DiscoveredTracker>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{host}:{port}"), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("SlimeVrDiscovery", $"Could not connect to SlimeVR's WebSocket API at {host}:{port} — is SlimeVR running? {ex.Message}");
            return result;
        }

        try
        {
            var request = BuildPollDevicesRequest();
            await ws.SendAsync(request, WebSocketMessageType.Binary, endOfMessage: true, cts.Token).ConfigureAwait(false);

            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            WebSocketReceiveResult received;
            do
            {
                received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    Log.Warn("SlimeVrDiscovery", "SlimeVR closed the connection before replying.");
                    return result;
                }
                ms.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);

            result = ParseDevices(ms.ToArray());
            Log.Info("SlimeVrDiscovery", $"Discovered {result.Count} device(s) from SlimeVR.");
        }
        catch (Exception ex)
        {
            Log.Warn("SlimeVrDiscovery", $"SlimeVR discovery request/response threw: {ex.Message}");
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

    private static ArraySegment<byte> BuildPollDevicesRequest()
    {
        var builder = new FlatBufferBuilder(256);

        var deviceDataMaskOffset = DeviceDataMask.CreateDeviceDataMask(builder, device_data: true);
        var configOffset = DataFeedConfig.CreateDataFeedConfig(builder, data_maskOffset: deviceDataMaskOffset);
        var pollOffset = PollDataFeed.CreatePollDataFeed(builder, configOffset);
        var headerOffset = DataFeedMessageHeader.CreateDataFeedMessageHeader(
            builder, DataFeedMessage.PollDataFeed, pollOffset.Value);

        var headersVector = MessageBundle.CreateDataFeedMsgsVector(builder, new[] { headerOffset });
        var bundleOffset = MessageBundle.CreateMessageBundle(builder, data_feed_msgsOffset: headersVector);

        builder.Finish(bundleOffset.Value);
        return new ArraySegment<byte>(builder.SizedByteArray());
    }

    private static List<DiscoveredTracker> ParseDevices(byte[] data)
    {
        var trackers = new List<DiscoveredTracker>();
        var bundle = MessageBundle.GetRootAsMessageBundle(new ByteBuffer(data));

        for (var i = 0; i < bundle.DataFeedMsgsLength; i++)
        {
            var header = bundle.DataFeedMsgs(i);
            if (header is null || header.Value.MessageType != DataFeedMessage.DataFeedUpdate)
                continue;

            var update = header.Value.MessageAsDataFeedUpdate();
            for (var d = 0; d < update.DevicesLength; d++)
            {
                var device = update.Devices(d);
                var hw = device?.HardwareInfo;
                if (hw is null) continue;

                var mac = FormatMac(hw.Value.HardwareAddress);
                var ip = FormatIp(hw.Value.IpAddress);
                if (mac is null && ip is null) continue; // nothing usable to report

                var name = hw.Value.DisplayName ?? device?.CustomName ?? $"device_{d}";
                trackers.Add(new DiscoveredTracker { Name = name, Mac = mac ?? "", Ip = ip ?? "" });
            }
        }

        return trackers;
    }

    private static string? FormatMac(HardwareAddress? addr)
    {
        if (addr is null) return null;
        var v = addr.Value.Addr;
        var bytes = new byte[6];
        for (var i = 5; i >= 0; i--) { bytes[i] = (byte)(v & 0xFF); v >>= 8; }
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private static string? FormatIp(solarxr_protocol.datatypes.Ipv4Address? addr)
    {
        if (addr is null) return null;
        var v = addr.Value.Addr;
        return $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
    }
}
