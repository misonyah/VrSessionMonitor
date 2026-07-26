using System.Runtime.InteropServices;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
/// <summary>
/// Polls SteamVR/OpenVR's own HMD activity level (proximity sensor + motion, not this app's
/// business logic) to tell "headset on your face" apart from "headset present but not worn" —
/// e.g. AFK via taking the headset off without closing VRChat. Reuses the exact interop pattern
/// already proven in Modules/SteamVrNotifier.cs (same VR_InitInternal2/VR_GetGenericInterface/
/// VR_ShutdownInternal DllImports), but unlike that class's one-shot-per-call lifecycle, this
/// holds ONE OpenVR session open for the life of the poll loop, since we're reading state on an
/// interval rather than firing an isolated one-off notification.
///
/// IVRSystem's real FnTable has 80+ functions; GetTrackedDeviceActivityLevel is confirmed to be
/// the 15th, verified against a real, working reference already on this machine
/// (C:\Users\<user>\git\Baballonia\src\Baballonia\SteamVR\openvr_api.cs — Baballonia is part of
/// this same VR stack). Marshal.PtrToStructure only cares about each field's marshaled SIZE —
/// every FunctionPtr field is IntPtr-sized regardless of the delegate's own parameter types — so
/// the 14 preceding functions (never invoked here) are typed with one shared no-op delegate
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
    private bool _sessionOpen;
    private IVRSystemFnTable _fnTable;
    private int _consecutiveReadsAtCurrentLevel;

    public bool IsUserPresent { get; private set; } = true; // assume present until proven otherwise — never fire a false AFK before the first real read
    public event EventHandler<bool>? PresenceChanged;

    public HmdActivityMonitor(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
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
        if (!_sessionOpen && !TryOpenSession())
            return; // SteamVR not running (or DLL missing) — leave IsUserPresent at its last known value

        EDeviceActivityLevel level;
        try
        {
            level = _fnTable.GetTrackedDeviceActivityLevel(HmdDeviceIndex);
        }
        catch (Exception ex)
        {
            Log.Debug("HmdActivity", $"GetTrackedDeviceActivityLevel threw: {ex.Message} — closing session, will retry.");
            CloseSession();
            return;
        }

        // UserInteraction is the only level meaning "actually being worn and used right now" —
        // Idle/*_Timeout/Standby/Unknown all mean the runtime judged the headset not actively in
        // use, which is exactly what AFK should track.
        var present = level == EDeviceActivityLevel.UserInteraction;

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

    private bool TryOpenSession()
    {
        if (!EnsureDllLoaded()) return false;

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

        var ifaceError = EVRInitError_None;
        var pInterface = GetGenericInterface(FnTablePrefix + IVRSystem_Version, ref ifaceError);
        if (pInterface == IntPtr.Zero || ifaceError != EVRInitError_None)
        {
            Log.Warn("HmdActivity", $"Could not get IVRSystem interface (error {ifaceError}) — if this persists after a SteamVR update, {IVRSystem_Version} may need bumping to match the installed openvr_api.dll.");
            try { ShutdownInternal(); } catch { /* best effort */ }
            return false;
        }

        _fnTable = (IVRSystemFnTable)Marshal.PtrToStructure(pInterface, typeof(IVRSystemFnTable))!;
        _sessionOpen = true;
        Log.Info("HmdActivity", "OpenVR session opened for HMD activity polling.");
        return true;
    }

    private void CloseSession()
    {
        if (!_sessionOpen) return;
        try { ShutdownInternal(); } catch { /* best effort */ }
        _sessionOpen = false;
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
        CloseSession();
    }
}
#endif
