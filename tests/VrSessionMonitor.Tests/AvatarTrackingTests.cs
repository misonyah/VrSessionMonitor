using System.Collections.Generic;
using System.IO;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class AvatarTrackingTests
{
    private static string OscDir(out string avatars)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsm-osc-" + Path.GetRandomFileName());
        avatars = Path.Combine(dir, "usr_1", "Avatars");
        Directory.CreateDirectory(avatars);
        return dir;
    }

    [Fact]
    public void Avatar_change_message_yields_the_avatar_id()
    {
        Assert.True(AvatarTracker.TryParseAvatarChange("/avatar/change", "avtr_123", out var id));
        Assert.Equal("avtr_123", id);
        Assert.False(AvatarTracker.TryParseAvatarChange("/avatar/parameters/AFK", true, out _));
        Assert.False(AvatarTracker.TryParseAvatarChange("/avatar/change", 5, out _));
    }

    [Fact]
    public void Tracker_resolves_name_from_that_avatars_osc_config_and_raises_once_per_change()
    {
        var dir = OscDir(out var avatars);
        File.WriteAllText(Path.Combine(avatars, "avtr_a.json"), """{ "id": "avtr_a", "name": "Shinra", "parameters": [] }""");
        var t = new AvatarTracker(dir);
        var changes = new List<string>();
        t.Changed += (_, _) => changes.Add(t.CurrentAvatarId!);

        t.OnAvatarChange("avtr_a");
        t.OnAvatarChange("avtr_a");            // same avatar re-announced: no second event

        Assert.Equal("Shinra", t.CurrentAvatarName);
        Assert.Equal(new[] { "avtr_a" }, changes);
    }

    [Fact]
    public void Unknown_avatar_has_no_name_but_keeps_its_id()
    {
        var dir = OscDir(out _);
        var t = new AvatarTracker(dir);
        t.OnAvatarChange("avtr_unknown");
        Assert.Equal("avtr_unknown", t.CurrentAvatarId);
        Assert.Null(t.CurrentAvatarName);
    }

    [Fact]
    public void Suggestions_come_from_the_current_avatar_not_the_newest_file()
    {
        var dir = OscDir(out var avatars);
        File.WriteAllText(Path.Combine(avatars, "avtr_worn.json"), """{ "parameters": [ { "name": "Worn", "input": {"type":"Bool"} } ] }""");
        var other = Path.Combine(avatars, "avtr_other.json");
        File.WriteAllText(other, """{ "parameters": [ { "name": "Other", "input": {"type":"Bool"} } ] }""");
        File.SetLastWriteTimeUtc(other, System.DateTime.UtcNow.AddMinutes(5));   // newer, but not worn

        Assert.Equal(new[] { "Worn" }, AutomationSuggestionSources.ScanAvatarParams(dir, "avtr_worn"));
    }

    [Theory]
    [InlineData("""{"FULL_PATH":"/avatar/change","TYPE":"s","VALUE":["avtr_abc"]}""", "avtr_abc")]
    [InlineData("""{"FULL_PATH":"/avatar/change","VALUE":[]}""", null)]
    [InlineData("not json", null)]
    public void OscQuery_avatar_change_answer_is_parsed(string json, string? expected)
        => Assert.Equal(expected, VrChatOscQueryClient.ParseAvatarChangeValue(json));

    [Fact]
    public void Resend_after_avatar_change_is_the_active_groups_on_value()
    {
        var config = new MonitorConfig();
        config.VrChatGroupAutomation.Groups.Add(new GroupAutomationEntry { GroupId = "grp_a", ParamName = "Outfit", Value = "3" });
        Assert.Equal(("Outfit", (object)3), GroupAutomationResend.For(config, "grp_a"));
        Assert.Null(GroupAutomationResend.For(config, "grp_other"));
        Assert.Null(GroupAutomationResend.For(config, null));
    }
}
