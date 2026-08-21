using System.Diagnostics;
using System.Runtime.InteropServices;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Applies a ManagedApp's window rules to a running process. All operations are best-effort: a
/// window that vanishes mid-operation, or a foreground change Windows refuses, is logged and
/// skipped — failing to minimise a window must never break the launch that preceded it.
/// </summary>
public static class WindowController
{
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const int SW_SHOWNORMAL = 1;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Applies the rules once the process has a main window. Returns false if no window
    /// appeared within the timeout (logged, not thrown) — a headless or tray-only app is a normal
    /// outcome here, not an error.</summary>
    public static async Task<bool> ApplyAsync(int processId, AppWindowState state, bool bringToFront,
        bool keepInBackground, int? targetMonitor, int timeoutMs = 10000)
    {
        if (state == AppWindowState.Unchanged && !bringToFront && !keepInBackground && targetMonitor is null)
            return true; // nothing configured — don't even wait for a window

        var handle = await WaitForMainWindowAsync(processId, timeoutMs).ConfigureAwait(false);
        if (handle == IntPtr.Zero)
        {
            Log.Debug("WindowController", $"PID {processId} had no main window within {timeoutMs}ms — skipping window rules.");
            return false;
        }

        try
        {
            // Monitor placement first: moving a maximised window doesn't relocate it, so position
            // while it's restored, then apply the final state.
            if (targetMonitor is int monitor) MoveToMonitor(handle, monitor);

            switch (state)
            {
                case AppWindowState.Minimized: ShowWindow(handle, SW_MINIMIZE); break;
                case AppWindowState.Maximized: ShowWindow(handle, SW_MAXIMIZE); break;
                case AppWindowState.Normal: ShowWindow(handle, SW_SHOWNORMAL); break;
                case AppWindowState.Fullscreen: ApplyFullScreenBounds(handle, targetMonitor); break;
                case AppWindowState.Unchanged: default: break;
            }

            // BringToFront deliberately wins over KeepInBackground: an explicit request to see the
            // window beats an explicit request to hide it.
            if (bringToFront) ForceForeground(handle);
            else if (keepInBackground) SetWindowPos(handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("WindowController", $"Applying window rules to PID {processId} threw: {ex.Message}");
            return false;
        }
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(int processId, int timeoutMs)
    {
        var elapsed = 0;
        const int pollMs = 250;
        while (elapsed < timeoutMs)
        {
            try
            {
                using var proc = Process.GetProcessById(processId);
                proc.Refresh();
                if (proc.MainWindowHandle != IntPtr.Zero) return proc.MainWindowHandle;
            }
            // GetProcessById throws ArgumentException when the pid is already gone;
            // MainWindowHandle throws InvalidOperationException when the process exits between
            // that lookup and the property read. Both mean the same thing to the caller — there is
            // no window to act on — and neither is an error worth propagating, since this whole
            // path is best-effort by design.
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return IntPtr.Zero;
            }

            await Task.Delay(pollMs).ConfigureAwait(false);
            elapsed += pollMs;
        }
        return IntPtr.Zero;
    }

    /// <summary>Windows refuses SetForegroundWindow from a background process unless the calling
    /// thread shares input state with the current foreground window — hence the AttachThreadInput
    /// dance. Still best-effort: the OS can decline regardless.</summary>
    private static void ForceForeground(IntPtr handle)
    {
        var foreground = GetForegroundWindow();
        if (foreground == handle) return;

        ShowWindow(handle, SW_RESTORE); // a minimised window can't take focus
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();

        var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            if (!SetForegroundWindow(handle))
                Log.Debug("WindowController", "SetForegroundWindow was refused by the OS — leaving window where it is.");
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static void MoveToMonitor(IntPtr handle, int monitorIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (monitorIndex < 1 || monitorIndex > screens.Length)
        {
            Log.Debug("WindowController", $"Target monitor {monitorIndex} doesn't exist ({screens.Length} attached) — leaving window where it is.");
            return;
        }

        var bounds = screens[monitorIndex - 1].WorkingArea;
        ShowWindow(handle, SW_RESTORE); // can't reposition a maximised/minimised window meaningfully
        MoveWindow(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
    }

    /// <summary>Resizes the window to fill the target monitor's full bounds, including the area
    /// behind the taskbar — which is what distinguishes this from Maximized (SW_MAXIMIZE respects
    /// the working area and leaves the taskbar visible).
    ///
    /// Deliberately does NOT strip window chrome: true borderless would mean rewriting GWL_STYLE,
    /// which requires saving and restoring each window's original style to avoid stranding an app
    /// permanently borderless. That state isn't tracked anywhere, so the title bar stays. It also
    /// does not touch an app's own internal fullscreen mode — VRChat and most others expose that
    /// through their own launch args or settings.</summary>
    private static void ApplyFullScreenBounds(IntPtr handle, int? targetMonitor)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = targetMonitor is int m && m >= 1 && m <= screens.Length
            ? screens[m - 1]
            : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];

        var bounds = screen.Bounds; // full bounds, not working area — cover the taskbar
        ShowWindow(handle, SW_RESTORE);
        MoveWindow(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
    }
}
