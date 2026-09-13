using System.Runtime.InteropServices;

namespace VrSessionMonitor.Tray;

/// <summary>
/// Stops the scroll wheel from changing a dropdown's value.
///
/// WinForms gives a focused-or-hovered ComboBox the wheel, so scrolling a settings page that
/// happens to have the pointer over a dropdown silently changes the setting instead of scrolling -
/// which is how you end up in a different VRChat group, or watching a different audio device, with
/// no idea you did it. Reported after exactly that.
///
/// An application message filter rather than a MouseWheel handler on each control: dropdowns here
/// are built at runtime (the Automation grid's editing control, the Home Assistant area and light
/// pickers, the audio device lists), so per-control wiring would keep missing new ones. This sees
/// every wheel message regardless of who created the control.
///
/// The wheel is not swallowed - it is handed to the nearest scrolling ancestor, so the page moves
/// under the pointer the way it would have if the dropdown were not there. An OPEN dropdown is left
/// alone: scrolling its list is the one case where the wheel genuinely belongs to it.
/// </summary>
internal sealed class DropdownWheelGuard : IMessageFilter
{
    private const int WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;

        // FromChildHandle, not FromHandle: a ComboBox is several native windows, and the wheel
        // usually arrives at its child edit control rather than the ComboBox itself.
        if (Control.FromChildHandle(m.HWnd) is not ComboBox combo) return false;

        // Its list is open - the wheel is genuinely for choosing an item.
        if (combo.DroppedDown) return false;

        for (Control? parent = combo.Parent; parent is not null; parent = parent.Parent)
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
