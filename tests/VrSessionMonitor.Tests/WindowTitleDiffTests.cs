using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>Several windows of one app share most of their title ("… - Unity 2022.3.22f1 &lt;DX11&gt;");
/// the freeze table shows only the part that tells them apart.</summary>
public class WindowTitleDiffTests
{
    [Fact]
    public void Strips_the_words_every_title_shares_at_the_start_and_end()
    {
        var shown = WindowTitleDiff.Distinguish(new[]
        {
            "ProjectA - SampleScene - Windows, Mac, Linux - Unity 2022.3.22f1 <DX11>",
            "ProjectB - Main - Android - Unity 2022.3.22f1 <DX11>",
        });

        Assert.Equal(new[] { "ProjectA - SampleScene - Windows, Mac, Linux", "ProjectB - Main - Android" }, shown);
    }

    [Fact]
    public void Strips_a_shared_prefix_too()
    {
        var shown = WindowTitleDiff.Distinguish(new[] { "Blender [C:/a/one.blend]", "Blender [C:/a/two.blend]" });

        Assert.Equal(new[] { "[C:/a/one.blend]", "[C:/a/two.blend]" }, shown);
    }

    [Fact]
    public void A_single_title_is_shown_in_full()
    {
        Assert.Equal(new[] { "ProjectA - Main - Unity 2022.3.22f1", "" },
            WindowTitleDiff.Distinguish(new[] { "ProjectA - Main - Unity 2022.3.22f1", "" }));
    }

    [Fact]
    public void Identical_titles_are_shown_in_full_rather_than_as_nothing()
    {
        Assert.Equal(new[] { "Untitled - Notes", "Untitled - Notes" },
            WindowTitleDiff.Distinguish(new[] { "Untitled - Notes", "Untitled - Notes" }));
    }

    [Fact]
    public void Untitled_processes_stay_empty_and_do_not_block_the_comparison()
    {
        var shown = WindowTitleDiff.Distinguish(new[] { "a.txt - Editor", "", "b.txt - Editor" });

        Assert.Equal(new[] { "a.txt", "", "b.txt" }, shown);
    }
}
