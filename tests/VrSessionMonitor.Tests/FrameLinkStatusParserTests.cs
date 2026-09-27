using System.IO;
using VrSessionMonitor.Modules.Frame;
using Xunit;

namespace VrSessionMonitor.Tests;

public class FrameLinkStatusParserTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "frame-link-2026-09-27.txt"));

    [Fact]
    public void Parses_real_capture()
    {
        var s = FrameLinkStatusParser.Parse(Fixture());
        Assert.True(s.StreamUp);
        Assert.Equal("6 GHz", s.Band);
        Assert.Equal(37, s.Channel);
        Assert.Equal(160, s.WidthMhz);
        Assert.Equal(2161.3, s.TxMbit);
        Assert.Equal(1441.3, s.RxMbit);
        Assert.Equal(-50, s.AckSignalDbm);
        Assert.Equal(0, s.TxRetries);
        Assert.False(s.InternetConnected);
        Assert.Equal("disconnected", s.InternetState);
        Assert.Equal("6 GHz ch37 160 MHz · 2161/1441 Mbit/s · -50 dBm · internet: disconnected", s.Summary());
    }

    [Fact]
    public void Internet_connected_reads_ssid()
    {
        var text = Fixture()
            .Replace("Not connected.", "Connected to 11:22:33:44:55:66 (on wlan0)\n\tSSID: HomeHotspot\n\tfreq: 5180\n\ttx bitrate: 1200.9 MBit/s 80MHz HE-MCS 11 HE-NSS 2")
            .Replace("wlan0:disconnected:", "wlan0:connected:HomeHotspot");
        var s = FrameLinkStatusParser.Parse(text);
        Assert.True(s.InternetConnected);
        Assert.Equal("HomeHotspot", s.InternetSsid);
        Assert.Equal(5180, s.InternetFreqMhz);
        Assert.Equal(80, s.InternetWidthMhz);
        Assert.EndsWith("internet: HomeHotspot 5 GHz 80 MHz", s.Summary());
    }

    [Fact]
    public void No_station_means_stream_down()
    {
        var text = Fixture();
        text = text[..text.IndexOf("##STATION")] + "##STATION\n" + text[text.IndexOf("##WLAN0_LINK")..];
        var s = FrameLinkStatusParser.Parse(text);
        Assert.False(s.StreamUp);
        Assert.StartsWith("stream link down", s.Summary());
    }

    [Fact]
    public void Narrow_5ghz_link_is_reported()
    {
        var s = FrameLinkStatusParser.Parse(Fixture().Replace("channel 37 (6135 MHz), width: 160 MHz", "channel 149 (5745 MHz), width: 80 MHz"));
        Assert.Equal("5 GHz", s.Band);
        Assert.Equal(80, s.WidthMhz);
    }

    [Fact]
    public void Empty_output_is_all_unknown()
    {
        var s = FrameLinkStatusParser.Parse("");
        Assert.False(s.StreamUp);
        Assert.False(s.InternetConnected);
        Assert.Equal("unknown", s.InternetState);
    }

    [Theory]
    [InlineData(2437, "2.4 GHz")]
    [InlineData(5180, "5 GHz")]
    [InlineData(6135, "6 GHz")]
    public void BandFromMhz(int mhz, string band) => Assert.Equal(band, FrameLinkStatusParser.BandFromMhz(mhz));
}
