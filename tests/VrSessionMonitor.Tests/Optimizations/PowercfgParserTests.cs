// tests/VrSessionMonitor.Tests/Optimizations/PowercfgParserTests.cs
using VrSessionMonitor.Optimizations;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

public class PowercfgParserTests
{
    private const string ListOutput = @"Existing Power Schemes (* Active)
-----------------------------------
Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *
Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)
Power Scheme GUID: 9f245d2a-1111-2222-3333-444455556666  (AMD Ryzen Balanced)
";

    private const string ActiveOutput = "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n";

    [Fact]
    public void ParseActiveSchemeGuid_extracts_the_guid()
    {
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", PowercfgParser.ParseActiveSchemeGuid(ActiveOutput));
    }

    [Fact]
    public void ParseActiveSchemeGuid_returns_null_on_unexpected_output()
    {
        Assert.Null(PowercfgParser.ParseActiveSchemeGuid("garbage"));
    }

    [Fact]
    public void FindSchemeGuidByName_finds_a_named_scheme_case_insensitively()
    {
        Assert.Equal("9f245d2a-1111-2222-3333-444455556666", PowercfgParser.FindSchemeGuidByName(ListOutput, "amd ryzen balanced"));
    }

    [Fact]
    public void FindSchemeGuidByName_returns_null_when_not_present()
    {
        Assert.Null(PowercfgParser.FindSchemeGuidByName(ListOutput, "Ultimate Performance"));
    }

    [Fact]
    public void ParseCurrentAcValueIndex_extracts_hex_index()
    {
        var output = "Current AC Power Setting Index: 0x00000000\r\nCurrent DC Power Setting Index: 0x00000001\r\n";
        Assert.Equal(0, PowercfgParser.ParseCurrentAcValueIndex(output));
    }

    [Fact]
    public void ParseCurrentAcValueIndex_returns_null_on_unexpected_output()
    {
        Assert.Null(PowercfgParser.ParseCurrentAcValueIndex("garbage"));
    }
}
