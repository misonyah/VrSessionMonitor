using System.Collections.Generic;
using VrSessionMonitor.Config;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class RepresentPolicyTests
{
    private static List<GroupAutomationEntry> Groups() => new()
    {
        new GroupAutomationEntry { GroupId = "grp_eden", DisplayName = "Eden", ParamName = "EdenApis", Represent = true },
        new GroupAutomationEntry { GroupId = "grp_off",  DisplayName = "Off",  ParamName = "X",        Represent = false },
    };

    [Fact]
    public void In_listed_represent_on_group_represents_that_group()
    {
        Assert.Equal("grp_eden", RepresentPolicy.ComputeDesiredRepresentedGroupId("grp_eden", Groups(), "grp_clan"));
    }

    [Fact]
    public void In_listed_represent_off_group_uses_fallback()
    {
        Assert.Equal("grp_clan", RepresentPolicy.ComputeDesiredRepresentedGroupId("grp_off", Groups(), "grp_clan"));
    }

    [Fact]
    public void In_unlisted_group_uses_fallback()
    {
        Assert.Equal("grp_clan", RepresentPolicy.ComputeDesiredRepresentedGroupId("grp_random", Groups(), "grp_clan"));
    }

    [Fact]
    public void No_group_uses_fallback()
    {
        Assert.Equal("grp_clan", RepresentPolicy.ComputeDesiredRepresentedGroupId(null, Groups(), "grp_clan"));
    }

    [Fact]
    public void No_fallback_and_not_in_represent_group_clears()
    {
        Assert.Null(RepresentPolicy.ComputeDesiredRepresentedGroupId(null, Groups(), ""));
        Assert.Null(RepresentPolicy.ComputeDesiredRepresentedGroupId("grp_off", Groups(), ""));
    }
}
