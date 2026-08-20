using VrSessionMonitor.Config;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// The per-light config value is stored as a plain string in the existing
/// Dictionary&lt;string,string&gt; action maps, so adding colour/brightness must not break configs
/// written before those existed. These tests pin that backward compatibility: a bare "On" must
/// keep meaning exactly what it meant before (no colour, full brightness).
/// </summary>
public class LightSettingTests
{
    [Theory]
    [InlineData("On", LightAction.On)]
    [InlineData("Off", LightAction.Off)]
    [InlineData("NoChange", LightAction.NoChange)]
    [InlineData("on", LightAction.On)]
    public void Legacy_bare_action_still_parses(string raw, LightAction expected)
    {
        var s = LightSetting.Parse(raw);

        Assert.Equal(expected, s.Action);
        Assert.Null(s.Rgb);
        Assert.Equal(100, s.BrightnessPct); // the previously hardcoded brightness_pct
    }

    [Fact]
    public void Unrecognised_value_falls_back_to_NoChange()
    {
        var s = LightSetting.Parse("gibberish");

        Assert.Equal(LightAction.NoChange, s.Action);
        Assert.Null(s.Rgb);
    }

    [Fact]
    public void Null_or_empty_falls_back_to_NoChange()
    {
        Assert.Equal(LightAction.NoChange, LightSetting.Parse(null).Action);
        Assert.Equal(LightAction.NoChange, LightSetting.Parse("").Action);
    }

    [Fact]
    public void Parses_action_with_colour_and_brightness()
    {
        var s = LightSetting.Parse("On|#FF8800|75");

        Assert.Equal(LightAction.On, s.Action);
        Assert.Equal(0xFF8800, s.Rgb);
        Assert.Equal(75, s.BrightnessPct);
    }

    [Fact]
    public void Parses_colour_without_brightness()
    {
        var s = LightSetting.Parse("On|#00FF7F");

        Assert.Equal(0x00FF7F, s.Rgb);
        Assert.Equal(100, s.BrightnessPct);
    }

    /// <summary>Brightness set but no colour — the empty middle field must not be read as a colour.</summary>
    [Fact]
    public void Parses_brightness_without_colour()
    {
        var s = LightSetting.Parse("On||40");

        Assert.Equal(LightAction.On, s.Action);
        Assert.Null(s.Rgb);
        Assert.Equal(40, s.BrightnessPct);
    }

    [Fact]
    public void Colour_hash_prefix_is_optional()
    {
        Assert.Equal(0xABCDEF, LightSetting.Parse("On|ABCDEF|100").Rgb);
    }

    [Theory]
    [InlineData("On|#FF8800|0", 1)]     // 0% would be indistinguishable from off
    [InlineData("On|#FF8800|-5", 1)]
    [InlineData("On|#FF8800|250", 100)]
    public void Brightness_is_clamped_to_a_usable_range(string raw, int expected)
    {
        Assert.Equal(expected, LightSetting.Parse(raw).BrightnessPct);
    }

    [Fact]
    public void Malformed_colour_is_ignored_rather_than_failing_the_whole_value()
    {
        var s = LightSetting.Parse("On|#ZZZZZZ|60");

        Assert.Equal(LightAction.On, s.Action); // action still honoured
        Assert.Null(s.Rgb);
        Assert.Equal(60, s.BrightnessPct);
    }

    /// <summary>A setting with no colour and default brightness serialises back to the bare legacy
    /// form, so enabling this feature doesn't rewrite every existing entry into a noisier format.</summary>
    [Fact]
    public void Default_setting_serialises_to_the_legacy_bare_form()
    {
        Assert.Equal("On", new LightSetting(LightAction.On, null, 100).ToConfigString());
        Assert.Equal("NoChange", new LightSetting(LightAction.NoChange, null, 100).ToConfigString());
    }

    [Fact]
    public void Round_trips_colour_and_brightness()
    {
        var original = new LightSetting(LightAction.On, 0xFF8800, 75);

        var round = LightSetting.Parse(original.ToConfigString());

        Assert.Equal(original, round);
    }

    [Fact]
    public void Round_trips_brightness_only()
    {
        var original = new LightSetting(LightAction.On, null, 40);

        Assert.Equal(original, LightSetting.Parse(original.ToConfigString()));
    }
}
