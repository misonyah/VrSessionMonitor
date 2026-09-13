using System.Runtime.InteropServices;

namespace VrSessionMonitor.Tray;

/// <summary>
/// Stops the scroll wheel from changing a control's value.
///
/// WinForms hands the wheel to whichever ComboBox or NumericUpDown is under the pointer, so
/// scrolling a settings page silently edits whatever it passes over - a different VRChat group, a
/// different audio device, a different memory threshold - with nothing to show it happened. Both
/// were reported after doing exactly that.
///
/// An application message filter rather than a MouseWheel handler on each control: several of these
/// are built at runtime (the Automation grid's editing control, the Home Assistant area and light
/// pickers, the audio device lists), so per-control wiring would keep missing newly added ones.
/// This sees every wheel message regardless of who created the control.
///
/// The wheel is not swallowed - it is handed to the nearest scrolling ancestor, so the page moves
/// under the pointer the way it would have if the control were not there. An OPEN dropdown is left
/// alone: scrolling its list is the one case where the wheel genuinely belongs to it.
/// </summary>
internal sealed class ScrollWheelValueGuard : IMessageFilter
{
    private const int WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;

        // Both controls are several native windows, and the wheel usually arrives at an inner child
        // (a ComboBox's edit, a NumericUpDown's edit) rather than the control itself - hence
        // FromChildHandle, and hence walking up from whatever that resolves to.
        var target = Control.FromChildHandle(m.HWnd);
        while (target is not null and not ComboBox and not NumericUpDown) target = target.Parent;

        switch (target)
        {
            case ComboBox { DroppedDown: true }:
                return false; // its list is open; the wheel really is for choosing an item
            case ComboBox or NumericUpDown:
                break;
            default:
                return false;
        }

        for (Control? parent = target.Parent; parent is not null; parent = parent.Parent)
        {
            // A DataGridView scrolls rows without being a ScrollableControl, and a combo cell's
            // editing control lives inside one, so it has to count as a scroll target too.
            var scrolls = parent is DataGridView
                || (parent is ScrollableControl { AutoScroll: true } s && (s.VerticalScroll.Visible || s.HorizontalScroll.Visible));
            if (!scrolls) continue;

            SendMessage(parent.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
            return true;
        }

        // Nothing to scroll into - still swallow it, since changing the value was never wanted.
        return true;
    }
}
