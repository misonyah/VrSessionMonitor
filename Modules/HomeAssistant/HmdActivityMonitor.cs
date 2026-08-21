using System.Runtime.InteropServices;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;
using VrSessionMonitor.Modules;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
/// <summary>
/// Polls SteamVR/OpenVR's own HMD activity level (proximity sensor + motion, not this app's
/// business logic) to tell "headset on your face" apart from "headset present but not worn" —
/// e.g. AFK via taking the headset off without closing VRChat. Reuses the exact interop pattern
/// already proven in Modules/SteamVrNotifier.cs (same VR_InitInternal2/VR_GetGenericInterface/
/// VR_ShutdownInternal DllImports), including that class's stateless one-shot-per-call lifecycle:
/// every poll does its own full Init -> GetGenericInterface -> read -> Shutdown cycle rather than
/// caching a session across polls. That is deliberate and load-bearing — OpenVR's
/// VR_InitInternal2/VR_ShutdownInternal act on a single PROCESS-GLOBAL context that is not
/// reference-counted per caller, and SteamVrNotifier.TryNotify (13 call sites across 6 monitors,
/// all of which fire during a live VR session) ends every call with VR_ShutdownInternal. A cached
/// fn table would therefore be left pointing at a torn-down interface with no way to notice, and
/// calling through those stale native thunks corrupts process state rather than throwing a
/// catchable exception. Per-poll Init/Shutdown costs a little more per read and removes the whole
/// failure mode.
///
/// IVRSystem's real FnTable has 80+ functions; GetTrackedDeviceActivityLevel is confirmed to be
/// the 16th, verified against a real, working reference already on this machine
/// (C:\Users\<user>\git\Baballonia\src\Baballonia\SteamVR\openvr_api.cs — Baballonia is part of
/// this same VR stack). Marshal.PtrToStructure only cares about each field's marshaled SIZE —
/// every FunctionPtr field is IntPtr-sized regardless of the delegate's own parameter types — so
/// the 15 preceding functions (never invoked here) are typed with one shared no-op delegate
/// purely to occupy the right number of pointer-sized slots in the right order, rather than
/// porting a dozen unrelated OpenVR struct/enum types into this codebase just to satisfy
/// signatures nothing calls. IVRSystem_Version ("IVRSystem_023") was read directly from that
/// same reference file — if a future SteamVR update changes it, GetGenericInterface will fail
/// with a logged warning naming this constant, not crash.
/// </summary>
public sealed class HmdActivityMonitor : IDisposable
{
    #region OpenVR interop (IVRSystem, verified against Baballonia's vendored openvr_api.cs)

    [StructLayout(LayoutKind.Sequential)]
    private struct IVRSystemFnTable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void _Unused();
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetRecommendedRenderTargetSize;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetProjectionMatrix;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetProjectionRaw;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused ComputeDistortion;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetEyeToHeadTransform;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetTimeSinceLastVsync;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetD3D9AdapterIndex;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetDXGIOutputInfo;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetOutputDevice;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused IsDisplayOnDesktop;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused SetDisplayVisibility;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetDeviceToAbsoluteTrackingPose;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetSeatedZeroPoseToStandingAbsoluteTrackingPose;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetRawZeroPoseToStandingAbsoluteTrackingPose;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetSortedTrackedDeviceIndicesOfClass;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate EDeviceActivityLevel _GetTrackedDeviceActivityLevel(uint unDeviceId);
        [MarshalAs(UnmanagedType.FunctionPtr)]
        internal _GetTrackedDeviceActivityLevel GetTrackedDeviceActivityLevel;
    }

    private enum EDeviceActivityLevel
    {
        Unknown = -1,
        Idle = 0,
        UserInteraction = 1,
        UserInteraction_Timeout = 2,
        Standby = 3,
        Idle_Timeout = 4,
    }

    private const string FnTablePrefix = "FnTable:";
    private const string IVRSystem_Version = "IVRSystem_023";
    private const int EVRApplicationType_Background = 3;
    private const int EVRInitError_None = 0;
    private const int EVRInitError_NoServerForBackgroundApp = 121;
    private const uint HmdDeviceIndex = 0; // k_unTrackedDeviceIndex_Hmd — fixed by OpenVR, always 0

    [DllImport("openvr_api", EntryPoint = "VR_InitInternal2", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint InitInternal2(ref int peError, int eApplicationType, string? pStartupInfo);

    [DllImport("openvr_api", EntryPoint = "VR_ShutdownInternal", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ShutdownInternal();

    [DllImport("openvr_api", EntryPoint = "VR_GetGenericInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetGenericInterface([MarshalAs(UnmanagedType.LPStr)] string pchInterfaceVersion, ref int peError);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    #endregion

    private readonly MonitorConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _dllLoadAttempted;
    private bool _dllLoaded;
    private int _consecutiveReadsAtCurrentLevel;

    public bool IsUserPresent { get; private set; } = true; // assume present until proven otherwise — never fire a false AFK before the first real read
    public event EventHandler<bool>? PresenceChanged;

    public HmdActivityMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        if (!_config.HomeAssistant.Enabled)
        {
            Log.Info("HmdActivity", "Home Assistant disabled in config — not polling HMD activity.");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("HmdActivity", "Started HMD proximity polling.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            CheckOnce();
            try { await Task.Delay(_config.HomeAssistant.AfkPollIntervalMs, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void CheckOnce()
    {
        if (!TryReadActivityLevel(out var level))
            return; // SteamVR not running, DLL missing, or a transient read failure — leave IsUserPresent at its last known value

        // "Present" = the headset is being WORN, whether or not you're mid-motion. UserInteraction
        // is active movement; UserInteraction_Timeout is "was active within the last ~timeout window"
        // — you've gone still (reading the SteamVR dashboard/overlay, or just not moving) but are
        // STILL wearing it. Only Idle/Standby/Idle_Timeout/Unknown mean the headset is actually off
        // your head or idle-long, which is the real AFK to track. Treating UserInteraction_Timeout
        // as absent used to false-trigger AFK the moment you opened the SteamVR overlay.
        var present = level is EDeviceActivityLevel.UserInteraction or EDeviceActivityLevel.UserInteraction_Timeout;

        if (present == IsUserPresent)
        {
            _consecutiveReadsAtCurrentLevel = 0;
            return;
        }

        _consecutiveReadsAtCurrentLevel++;
        if (_consecutiveReadsAtCurrentLevel < _config.HomeAssistant.AfkConsecutiveReadsBeforeFlip)
        {
            Log.Debug("HmdActivity", $"Activity level suggests present={present} ({_consecutiveReadsAtCurrentLevel}/{_config.HomeAssistant.AfkConsecutiveReadsBeforeFlip} consecutive) — within debounce, not flipping yet.");
            return;
        }

        _consecutiveReadsAtCurrentLevel = 0;
        IsUserPresent = present;
        Log.Info("HmdActivity", $"HMD activity level -> {level}, present={present}");
        PresenceChanged?.Invoke(this, present);
    }

    /// <summary>One complete, self-contained OpenVR session: Init -> fetch IVRSystem -> read the
    /// activity level -> Shutdown, with nothing cached across calls (see the class doc for why the
    /// process-global OpenVR context makes a cached session unsafe here). Never throws; returns
    /// false for every "couldn't read it this time" case so the caller keeps its last known state.</summary>
    private bool TryReadActivityLevel(out EDeviceActivityLevel level)
    {
        level = EDeviceActivityLevel.Unknown;
        if (!EnsureDllLoaded()) return false;

        // Must hold OpenVrNativeGate.Lock for the whole Init->Shutdown cycle — see that class's
        // doc for the 2026-08-14 crash this fixes (this poll raced a concurrent
        // SteamVrNotifier.TryNotify call and one thread's VR_ShutdownInternal tore down the
        // process-global OpenVR context while the other was still using it).
        lock (OpenVrNativeGate.Lock)
        {
            var initError = EVRInitError_None;
            uint token;
            try
            {
                token = InitInternal2(ref initError, EVRApplicationType_Background, null);
            }
            catch (Exception ex)
            {
                Log.Debug("HmdActivity", $"VR_InitInternal2 threw: {ex.Message}");
                return false;
            }

            if (initError == EVRInitError_NoServerForBackgroundApp)
            {
                Log.Trace("HmdActivity", "SteamVR isn't running — will retry next poll.");
                return false;
            }

            if (initError != EVRInitError_None || token == 0)
            {
                Log.Debug("HmdActivity", $"OpenVR init failed with error code {initError}.");
                return false;
            }

            try
            {
                var ifaceError = EVRInitError_None;
                var pInterface = GetGenericInterface(FnTablePrefix + IVRSystem_Version, ref ifaceError);
                if (pInterface == IntPtr.Zero || ifaceError != EVRInitError_None)
                {
                    Log.Warn("HmdActivity", $"Could not get IVRSystem interface (error {ifaceError}) — if this persists after a SteamVR update, {IVRSystem_Version} may need bumping to match the installed openvr_api.dll.");
                    return false;
                }

                var fnTable = (IVRSystemFnTable)Marshal.PtrToStructure(pInterface, typeof(IVRSystemFnTable))!;
                level = fnTable.GetTrackedDeviceActivityLevel(HmdDeviceIndex);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("HmdActivity", $"GetTrackedDeviceActivityLevel threw: {ex.Message} — will retry next poll.");
                return false;
            }
            finally
            {
                try { ShutdownInternal(); } catch { /* best effort */ }
            }
        }
    }

    private bool EnsureDllLoaded()
    {
        if (_dllLoadAttempted) return _dllLoaded;
        _dllLoadAttempted = true;

        var path = _config.Paths.OpenVrApiDllPath;
        if (!File.Exists(path))
        {
            Log.Warn("HmdActivity", $"openvr_api.dll not found at {path} — HMD activity polling disabled.");
            return false;
        }

        var handle = LoadLibrary(path);
        _dllLoaded = handle != IntPtr.Zero;
        if (!_dllLoaded)
            Log.Warn("HmdActivity", $"LoadLibrary failed for {path} (Win32 error {Marshal.GetLastWin32Error()}).");
        return _dllLoaded;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        _cts?.Dispose();
        // No session to close — each poll opens and shuts down its own (see class doc).
    }
}
#endif
