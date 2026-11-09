using System.Drawing;
using System.Windows.Forms;
using VrSessionMonitor.Tray;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Status lines whose text alternates between one and two lines (SteamVR's "running — next: …"
/// comes and goes on every refresh) made every row below jump. A held label keeps the tallest height
/// it has shown for as long as the window is open.
/// </summary>
public class StableHeightTests
{
    private static Label WrappingLabel()
    {
        var label = new Label { AutoSize = true, MaximumSize = new Size(80, 0), Text = "short" };
        new Panel { Size = new Size(400, 400) }.Controls.Add(label);
        return label;
    }

    [Fact]
    public void A_held_label_keeps_its_two_line_height_when_the_text_gets_short_again()
    {
        var label = WrappingLabel();
        StableHeight.Hold(label);
        var oneLine = label.Height;

        label.Text = "a much longer status text that wraps onto a second line";
        var twoLines = label.Height;
        label.Text = "short";

        Assert.True(twoLines > oneLine);
        Assert.Equal(twoLines, label.Height);
    }

    [Fact]
    public void An_unheld_label_shrinks_back_which_is_the_jump()
    {
        var label = WrappingLabel();
        var oneLine = label.Height;
        label.Text = "a much longer status text that wraps onto a second line";
        label.Text = "short";

        Assert.Equal(oneLine, label.Height);
    }

    [Fact]
    public void A_held_detail_line_once_shown_stays_in_the_layout_when_hidden_again()
    {
        var parent = new Panel();
        var label = WrappingLabel();
        parent.Controls.Add(label);
        label.Visible = false;
        StableHeight.Hold(label);

        label.Visible = true;
        label.Text = "Next: restart something";
        label.Visible = false;

        Assert.True(label.Visible);
        Assert.Equal("", label.Text);
        Assert.True(label.Height > 0);
    }

    [Fact]
    public void A_held_detail_line_never_shown_takes_no_space()
    {
        var parent = new Panel();
        var label = WrappingLabel();
        parent.Controls.Add(label);
        label.Visible = false;
        StableHeight.Hold(label);

        label.Text = "x";
        label.Visible = false;

        Assert.False(label.Visible);
    }

    [Fact]
    public void Hiding_the_whole_tab_does_not_clear_the_text()
    {
        var parent = new Panel();
        var label = WrappingLabel();
        parent.Controls.Add(label);
        StableHeight.Hold(label);
        label.Text = "running";

        parent.Visible = false;

        Assert.Equal("running", label.Text);
    }
}
