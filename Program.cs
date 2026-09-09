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
    static void Main(string[] args)
    {
        // Child-process mode: read tracked-device batteries, print JSON, exit. Runs BEFORE the
        // single-instance check on purpose — it is a short-lived helper, not a second copy of the
        // app, and taking the mutex would make every sample fail while the tray is running.
        //
        // Out-of-process because this interop crashed the whole application with an access
        // violation on 2026-07-29; a native AV cannot be caught, so the only protection is for it
        // to land in a process nothing depends on.
        if (args.Length > 0 && args[0] == "--dump-battery")
        {
            var dllPath = args.Length > 1 ? args[1] : "";
            Environment.Exit(Modules.Battery.BatteryProbe.RunAsChildProcess(dllPath));
            return;
        }

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
