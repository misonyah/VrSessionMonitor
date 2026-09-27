using System.Globalization;
using System.Text.RegularExpressions;

namespace VrSessionMonitor.Modules.Frame;

public sealed record FrameLinkStatus(
    bool StreamUp, string Band, int? Channel, int? WidthMhz, double? TxMbit, double? RxMbit,
    int? AckSignalDbm, long? TxRetries, long? TxFailed,
    bool InternetConnected, string InternetSsid, string InternetState,
    int? InternetFreqMhz = null, int? InternetWidthMhz = null)
{
    public string Summary()
    {
        var detail = InternetConnected && InternetFreqMhz is int f
            ? $" {FrameLinkStatusParser.BandFromMhz(f)}{(InternetWidthMhz is int w ? $" {w} MHz" : "")}"
            : "";
        var internet = $"internet: {(InternetConnected ? InternetSsid : InternetState)}{detail}";
        if (!StreamUp) return $"stream link down · {internet}";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Band} ch{Channel} {WidthMhz} MHz · {TxMbit:F0}/{RxMbit:F0} Mbit/s · {AckSignalDbm} dBm · {internet}");
    }
}

/// <summary>Parses the output of <see cref="RemoteScript"/> run on a Steam Frame. On the Frame the
/// stream link is the headset-side AP "wlanap" (6 GHz, to the USB dongle) and "wlan0" is the
/// internet radio — on the same chip, which is why wlan0 scanning for a missing network shows up as
/// stream stalls.</summary>
public static partial class FrameLinkStatusParser
{
    public const string RemoteScript =
        "echo '##WLANAP_INFO'; iw dev wlanap info; echo '##STATION'; iw dev wlanap station dump; " +
        "echo '##WLAN0_LINK'; iw dev wlan0 link; echo '##NMCLI'; nmcli -t -f DEVICE,STATE,CONNECTION dev";

    public static FrameLinkStatus Parse(string output)
    {
        var info = Section(output, "WLANAP_INFO");
        var station = Section(output, "STATION");
        var link = Section(output, "WLAN0_LINK");
        var nmcli = Section(output, "NMCLI");

        var ch = ChannelRe().Match(info);
        int? channel = ch.Success ? int.Parse(ch.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        int? mhz = ch.Success ? int.Parse(ch.Groups[2].Value, CultureInfo.InvariantCulture) : null;
        int? width = ch.Success ? int.Parse(ch.Groups[3].Value, CultureInfo.InvariantCulture) : null;

        var streamUp = station.Contains("Station ", StringComparison.Ordinal);
        var ssid = SsidRe().Match(link);
        var nm = NmWlan0Re().Match(nmcli);
        var internetConnected = link.Contains("Connected to", StringComparison.Ordinal) && ssid.Success;

        return new FrameLinkStatus(
            StreamUp: streamUp,
            Band: mhz is int f ? BandFromMhz(f) : "?",
            Channel: channel, WidthMhz: width,
            TxMbit: Dbl(TxRateRe().Match(station)), RxMbit: Dbl(RxRateRe().Match(station)),
            AckSignalDbm: Int(AckRe().Match(station)),
            TxRetries: Long(RetriesRe().Match(station)), TxFailed: Long(FailedRe().Match(station)),
            InternetConnected: internetConnected,
            InternetSsid: ssid.Success ? ssid.Groups[1].Value.Trim() : "",
            InternetState: nm.Success ? nm.Groups[1].Value : "unknown",
            InternetFreqMhz: internetConnected ? Int(FreqRe().Match(link)) : null,
            InternetWidthMhz: internetConnected ? Int(LinkWidthRe().Match(link)) : null);
    }

    public static string BandFromMhz(int mhz) => mhz >= 5925 ? "6 GHz" : mhz >= 4900 ? "5 GHz" : "2.4 GHz";

    private static string Section(string text, string name)
    {
        var start = text.IndexOf($"##{name}", StringComparison.Ordinal);
        if (start < 0) return "";
        start += name.Length + 2;
        var end = text.IndexOf("\n##", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static double? Dbl(Match m) => m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    private static int? Int(Match m) => m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    private static long? Long(Match m) => m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"channel (\d+) \((\d+) MHz\), width: (\d+) MHz")] private static partial Regex ChannelRe();
    [GeneratedRegex(@"tx bitrate:\s*([\d.]+) MBit/s")] private static partial Regex TxRateRe();
    [GeneratedRegex(@"rx bitrate:\s*([\d.]+) MBit/s")] private static partial Regex RxRateRe();
    [GeneratedRegex(@"avg ack signal:\s*(-?\d+) dBm")] private static partial Regex AckRe();
    [GeneratedRegex(@"tx retries:\s*(\d+)")] private static partial Regex RetriesRe();
    [GeneratedRegex(@"tx failed:\s*(\d+)")] private static partial Regex FailedRe();
    [GeneratedRegex(@"SSID:\s*([^\r\n]+)")] private static partial Regex SsidRe();
    [GeneratedRegex(@"(?m)^wlan0:([^:\r\n]*):")] private static partial Regex NmWlan0Re();
    [GeneratedRegex(@"freq:\s*(\d+)")] private static partial Regex FreqRe();
    // `iw ... link` prints the width inside the bitrate line, e.g. "tx bitrate: 1200.9 MBit/s 80MHz HE-MCS 11".
    [GeneratedRegex(@"tx bitrate:[^\r\n]*?\s(\d+)MHz")] private static partial Regex LinkWidthRe();
}
