namespace VrSessionMonitor.Tray;

/// <summary>
/// A FlowLayoutPanel that double-buffers its painting. WinForms layout panels paint ungated by
/// default, so rebuilding the Status tab's freeze table (clear and re-add rows) flashes. Buffering
/// composites the new contents off-screen and blits them in one go, so neither the in-place text
/// updates on each 5s tick nor the occasional full rebuild shows a visible redraw.
/// </summary>
public sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}
