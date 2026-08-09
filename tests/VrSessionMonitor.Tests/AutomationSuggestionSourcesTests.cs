using System.IO;
using System.Linq;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class AutomationSuggestionSourcesTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsm_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void ScanGroupIds_extracts_distinct_group_ids_from_logs()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "output_log_1.txt"),
            "2026.01.01 [Behaviour] Joining wrld_a:1~group(grp_aaaa)~groupAccessType(members)\n" +
            "2026.01.01 [Behaviour] Joining wrld_b:2~group(grp_bbbb)~groupAccessType(public)\n");
        File.WriteAllText(Path.Combine(dir, "output_log_2.txt"),
            "2026.01.02 [Behaviour] Joining wrld_c:3~group(grp_aaaa)~groupAccessType(members)\n"); // dup

        var ids = AutomationSuggestionSources.ScanGroupIds(dir);

        Assert.Equal(new[] { "grp_aaaa", "grp_bbbb" }, ids.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ScanGroupIds_missing_dir_returns_empty()
    {
        Assert.Empty(AutomationSuggestionSources.ScanGroupIds(Path.Combine(Path.GetTempPath(), "nope_" + Path.GetRandomFileName())));
    }

    [Fact]
    public void ScanOscBoolParams_collects_only_bool_param_names_distinct()
    {
        var dir = TempDir();
        var avatarsDir = Path.Combine(dir, "usr_1", "Avatars");
        Directory.CreateDirectory(avatarsDir);
        File.WriteAllText(Path.Combine(avatarsDir, "avtr_1.json"), """
        {
          "id": "avtr_1", "name": "A",
          "parameters": [
            { "name": "EdenApis", "input": {"address":"/avatar/parameters/EdenApis","type":"Bool"}, "output": {"address":"/avatar/parameters/EdenApis","type":"Bool"} },
            { "name": "VRCEmote", "input": {"address":"/avatar/parameters/VRCEmote","type":"Int"}, "output": {"address":"/avatar/parameters/VRCEmote","type":"Int"} }
          ]
        }
        """);
        var avatars2 = Path.Combine(dir, "usr_2", "Avatars");
        Directory.CreateDirectory(avatars2);
        File.WriteAllText(Path.Combine(avatars2, "avtr_2.json"), """
        { "id": "avtr_2", "name": "B", "parameters": [
            { "name": "EdenApis", "output": {"address":"/avatar/parameters/EdenApis","type":"Bool"} },
            { "name": "PartyMode", "input": {"address":"/avatar/parameters/PartyMode","type":"Bool"} }
        ] }
        """);

        var names = AutomationSuggestionSources.ScanOscBoolParams(dir);

        Assert.Equal(new[] { "EdenApis", "PartyMode" }, names.OrderBy(x => x).ToArray());
        Assert.DoesNotContain("VRCEmote", names);
    }

    [Fact]
    public void ScanOscBoolParams_missing_dir_returns_empty()
    {
        Assert.Empty(AutomationSuggestionSources.ScanOscBoolParams(Path.Combine(Path.GetTempPath(), "nope_" + Path.GetRandomFileName())));
    }
}
