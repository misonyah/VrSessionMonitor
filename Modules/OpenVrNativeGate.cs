namespace VrSessionMonitor.Modules;

/// <summary>
/// Shared lock guarding every VR_InitInternal2 -> ... -> VR_ShutdownInternal cycle in the process.
/// OpenVR's VR_InitInternal2/VR_ShutdownInternal act on a single PROCESS-GLOBAL context, not
/// reference-counted per caller (see the same reasoning in HmdActivityMonitor's class doc). This
/// app has two independent call sites doing that stateless per-call Init/Shutdown pattern —
/// SteamVrNotifier.TryNotify (13 call sites across 6 monitors) and HmdActivityMonitor's own
/// polling loop — each running on its own background loop with no cross-monitor coordination.
/// Confirmed live 2026-08-14: with no shared lock, HmdActivityMonitor's 2s polling loop raced a
/// concurrent SteamVrNotifier.TryNotify call from FaceTrackingMonitor, one thread's
/// VR_ShutdownInternal tore down the context while the other was still calling through the
/// FnTable pointer it had already fetched, and the process crashed with an unhandled access
/// violation (0xc0000005) inside GetGenericInterface — not a catchable .NET exception, since it's
/// corrupted native state, exactly as this class's doc predicted before it actually happened.
/// Every Init/Shutdown cycle in the process must hold this lock for its full duration.
/// </summary>
internal static class OpenVrNativeGate
{
    internal static readonly object Lock = new();
}
