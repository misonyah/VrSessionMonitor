using System.Linq;
using System.Threading;
using System.Windows.Forms;
using VrSessionMonitor.Modules;
using VrSessionMonitor.Tray;
using Xunit;

namespace VrSessionMonitor.Tests;

public class GroupChoicesTests
{
    [Fact]
    public void Cached_groups_show_by_name_sorted()
    {
        var choices = GroupChoices.Build(new string[0], new[]
        {
            new VrcGroupInfo("grp_b", "Zebra Club", null),
            new VrcGroupInfo("grp_a", "Sunset Lounge", null),
        });
        Assert.Equal(new[] { "Sunset Lounge", "Zebra Club" }, choices.Select(c => c.Name));
        Assert.Equal("grp_a", choices[0].Id);
    }

    [Fact]
    public void Configured_group_missing_from_cache_is_labelled_unknown_not_a_bare_id()
    {
        var choices = GroupChoices.Build(new[] { "grp_gone" }, new[] { new VrcGroupInfo("grp_a", "Alpha", null) });
        var gone = Assert.Single(choices, c => c.Id == "grp_gone");
        Assert.Equal("Unknown group (grp_gone)", gone.Name);
        Assert.Equal("Alpha", choices[0].Name);          // known groups first
    }

    [Fact]
    public void Configured_group_present_in_cache_appears_once_with_its_name()
    {
        var choices = GroupChoices.Build(new[] { "grp_a", "grp_a", "" }, new[] { new VrcGroupInfo("grp_a", "Alpha", null) });
        Assert.Equal("Alpha", Assert.Single(choices).Name);
    }

    [Fact]
    public void WhenHandleReady_defers_until_the_control_has_a_handle()
    {
        // Regression: the settings window is built hidden at app start; BeginInvoke before its
        // handle exists threw and the group names were silently never loaded.
        var ran = 0;
        var t = new Thread(() =>
        {
            using var c = new Control();
            UiDispatch.WhenHandleReady(c, () => ran++);
            Assert.Equal(0, ran);                        // no handle yet -> not run, not thrown
            c.CreateControl();                           // handle created -> runs now
            Application.DoEvents();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.Equal(1, ran);
    }
}
