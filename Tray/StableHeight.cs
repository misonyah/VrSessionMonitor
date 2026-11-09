namespace VrSessionMonitor.Tray;

/// <summary>
/// Keeps a status label from shrinking while the window is open, so the rows below it stay put.
///
/// Several Status-tab lines alternate between one and two lines on the 5s refresh - SteamVR's
/// "running — next: …" appears and disappears with each pending action - and every switch made the
/// whole tab below jump. A held label keeps the tallest height it has shown. A detail line that is
/// hidden after having been shown keeps its place with empty text instead of leaving the layout.
/// </summary>
public static class StableHeight
{
    public static void Hold(Label label)
    {
        var shown = label.Visible && label.Text.Length > 0;
        var restoring = false;

        label.SizeChanged += (_, _) =>
        {
            if (label.Height > label.MinimumSize.Height)
                label.MinimumSize = new Size(label.MinimumSize.Width, label.Height);
        };

        label.VisibleChanged += (_, _) =>
        {
            if (restoring) return;
            if (label.Visible)
            {
                shown = true;
                return;
            }

            // Only the label's own hide, not its tab or window being hidden: the parent is still
            // visible in that case. A detail line never shown yet may still take no space.
            if (!shown || label.Parent is not { Visible: true }) return;
            restoring = true;
            try
            {
                label.Text = "";
                label.Visible = true;
            }
            finally { restoring = false; }
        };
    }
}
