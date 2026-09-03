using System.Diagnostics;

namespace VrSessionMonitor.Modules.Suspend;

/// <summary>
/// Decides what must never be suspended.
///
/// A suspended process still holds every lock, handle and shared section it had. Anything waiting
/// on one of those blocks until it resumes, so freezing the wrong process does not slow the
/// machine down — it hangs whatever was waiting, potentially including the shell or the audio
/// stack. That makes this list load-bearing rather than advisory, which is why it is a pure
/// function with its own tests rather than a check buried in the caller.
///
/// Three categories are refused outright:
///
/// The VR chain itself. Suspending SteamVR, the compositor, Virtual Desktop, VRChat or the
/// trackers during a session is self-defeating and, for the compositor, will drop the headset.
///
/// This application. Suspending ourselves means nothing is left running to resume anything —
/// every frozen process would stay frozen until a reboot.
///
/// System and shell processes. Explorer, the service host, audio, the window manager. These hold
/// locks the rest of the desktop waits on.
/// </summary>
public static class SuspendSafety
{
    /// <summary>Processes central to a running VR session.</summary>
    private static readonly string[] VrChain =
    {
        "vrserver", "vrmonitor", "vrcompositor", "vrdashboard", "vrwebhelper", "steamvr",
        "VirtualDesktop.Streamer", "VirtualDesktop.Server", "VirtualDesktop.Service",
        "VRChat", "SlimeVR", "sr_runtime", "VRCFaceTracking", "VRCFaceTracking.ModuleProcess",
        "Baballonia.Desktop", "VRCOSC", "vhui64", "OVR Toolkit", "XSOverlay",
    };

    /// <summary>Shell, session and system processes that others block on.</summary>
    private static readonly string[] SystemCritical =
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "explorer", "dwm", "audiodg", "fontdrvhost", "ctfmon",
        "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "sihost", "taskhostw", "conhost", "WUDFHost", "spoolsv",
    };

    /// <summary>This app, so it can never freeze the thing that would resume everything else.</summary>
    public static readonly string Self = "VrSessionMonitor";

    /// <summary>Why a process may not be suspended, or null when it may.</summary>
    public static string? RefusalReason(string processName, int processId, int currentProcessId)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "no process name";

        if (processId == currentProcessId)
            return "this is VrSessionMonitor itself — suspending it would leave nothing able to resume anything";

        // PIDs 0 and 4 are the Idle process and the System process on Windows.
        if (processId is 0 or 4) return "a core system process";

        if (Matches(processName, Self))
            return "another VrSessionMonitor instance — it may be the one that resumes processes";

        foreach (var name in VrChain)
            if (Matches(processName, name))
                return $"part of the VR session ({name}) — suspending it would break the session it is meant to help";

        foreach (var name in SystemCritical)
            if (Matches(processName, name))
                return $"a system or shell process ({name}) — other processes block on locks it holds";

        return null;
    }

    public static bool MaySuspend(string processName, int processId, int currentProcessId) =>
        RefusalReason(processName, processId, currentProcessId) is null;

    /// <summary>Process names arrive without .exe and in whatever case the OS reports.</summary>
    private static bool Matches(string actual, string candidate) =>
        string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase);

    public static int CurrentProcessId => Environment.ProcessId;
}
