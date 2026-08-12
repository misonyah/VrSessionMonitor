using Google.FlatBuffers;
using VrSessionMonitor.Modules;
using solarxr_protocol;
using solarxr_protocol.data_feed;
using solarxr_protocol.data_feed.device_data;
using solarxr_protocol.data_feed.tracker;
using solarxr_protocol.datatypes.math;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Regression net for SlimeVrMotionMonitor.ParseRotations — the binary SolarXR parse that was
/// previously validated live only. Builds real DataFeedUpdate MessageBundles (the same FlatBuffers
/// shape the server replies with) and asserts: synthetic rotations are read; the per-device Trackers
/// vector is a fallback used ONLY when synthetic is empty (the F2 fix — no double-count when both are
/// present); and a trackerless update yields nothing.
/// </summary>
public class SlimeVrMotionMonitorParseTests
{
    private static byte[] BuildUpdateBundle((float x, float y, float z, float w)[]? synthetic,
                                            (float x, float y, float z, float w)[]? deviceTrackers)
    {
        var builder = new FlatBufferBuilder(512);

        VectorOffset syntheticVec = default;
        if (synthetic is not null)
            syntheticVec = DataFeedUpdate.CreateSyntheticTrackersVector(builder, BuildTrackerDatas(builder, synthetic));

        VectorOffset devicesVec = default;
        if (deviceTrackers is not null)
        {
            var trackersVec = DeviceData.CreateTrackersVector(builder, BuildTrackerDatas(builder, deviceTrackers));
            DeviceData.StartDeviceData(builder);
            DeviceData.AddTrackers(builder, trackersVec);
            var device = DeviceData.EndDeviceData(builder);
            devicesVec = DataFeedUpdate.CreateDevicesVector(builder, new[] { device });
        }

        var update = DataFeedUpdate.CreateDataFeedUpdate(builder,
            devicesOffset: devicesVec,
            synthetic_trackersOffset: syntheticVec);

        var header = DataFeedMessageHeader.CreateDataFeedMessageHeader(builder, DataFeedMessage.DataFeedUpdate, update.Value);
        var msgs = MessageBundle.CreateDataFeedMsgsVector(builder, new[] { header });
        var bundle = MessageBundle.CreateMessageBundle(builder, data_feed_msgsOffset: msgs);
        builder.Finish(bundle.Value);
        return builder.SizedByteArray();
    }

    private static Offset<TrackerData>[] BuildTrackerDatas(FlatBufferBuilder builder, (float x, float y, float z, float w)[] rots)
    {
        var offsets = new Offset<TrackerData>[rots.Length];
        for (var i = 0; i < rots.Length; i++)
        {
            var (x, y, z, w) = rots[i];
            // Rotation is an inline struct — must be written between Start and End of the table.
            TrackerData.StartTrackerData(builder);
            TrackerData.AddRotation(builder, Quat.CreateQuat(builder, x, y, z, w));
            offsets[i] = TrackerData.EndTrackerData(builder);
        }
        return offsets;
    }

    [Fact]
    public void Reads_synthetic_tracker_rotations()
    {
        var bytes = BuildUpdateBundle(
            synthetic: new[] { (0.1f, 0.2f, 0.3f, 0.4f), (0.5f, 0.6f, 0.7f, 0.8f) },
            deviceTrackers: null);

        var result = SlimeVrMotionMonitor.ParseRotations(bytes);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.1f, result[0].X);
        Assert.Equal(0.2f, result[0].Y);
        Assert.Equal(0.3f, result[0].Z);
        Assert.Equal(0.4f, result[0].W);
        Assert.Equal(0.7f, result[1].Z);
    }

    [Fact]
    public void Ignores_device_trackers_when_synthetic_present()
    {
        // Synthetic is populated, so the per-device fallback must NOT run — otherwise the same
        // physical trackers get double-counted. This is the F2 gate.
        var bytes = BuildUpdateBundle(
            synthetic: new[] { (0.1f, 0.2f, 0.3f, 0.4f) },
            deviceTrackers: new[] { (0.9f, 0.9f, 0.9f, 0.9f), (0.8f, 0.8f, 0.8f, 0.8f) });

        var result = SlimeVrMotionMonitor.ParseRotations(bytes);

        Assert.Single(result);
        Assert.Equal(0.1f, result[0].X);
    }

    [Fact]
    public void Falls_back_to_device_trackers_when_synthetic_empty()
    {
        var bytes = BuildUpdateBundle(
            synthetic: null,
            deviceTrackers: new[] { (0.9f, 0.9f, 0.9f, 0.9f), (0.8f, 0.8f, 0.8f, 0.8f) });

        var result = SlimeVrMotionMonitor.ParseRotations(bytes);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.9f, result[0].X);
    }

    [Fact]
    public void Returns_empty_for_update_with_no_trackers()
    {
        var bytes = BuildUpdateBundle(synthetic: System.Array.Empty<(float, float, float, float)>(), deviceTrackers: null);

        var result = SlimeVrMotionMonitor.ParseRotations(bytes);

        Assert.Empty(result);
    }
}
