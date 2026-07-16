using VrSessionMonitor.Tray;

namespace VrSessionMonitor;

static class Program
{
    // "Global\" makes this visible across sessions (RDP, fast user switching), not just the
    // current one — the standard convention for a single-instance lock. Needed now that a
    // "Start with Windows" toggle exists: without this, a manual launch racing the startup
    // launch (or a second logon) would run two instances fighting over the same downstream
    // processes, the exact double-launch problem ProcessLauncher was built to prevent within a
    // single instance.
    private const string SingleInstanceMutexName = @"Global\VrSessionMonitor-SingleInstance";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("VR Session Monitor is already running (check the system tray).",
                "VR Session Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
