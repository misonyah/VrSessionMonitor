using System.Runtime.InteropServices;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Posts a SteamVR in-headset toast notification via OpenVR's IVRNotifications interface — the
/// same API SteamVR itself uses for its own notifications. Struct/delegate layouts below are
/// copied verbatim from Valve's official headers/openvr_api.cs (ValveSoftware/openvr repo) to
/// avoid ABI-mismatch crashes from hand-rolled interop; only unused enum members were trimmed
/// (safe — C# enums are plain int wrappers with no layout risk, unlike the structs/delegates).
///
/// Uses EVRApplicationType.VRApplication_Background, which per OpenVR's own documented
/// contract will NOT launch SteamVR if it isn't already running — VR_Init just fails with
/// Init_NoServerForBackgroundApp, which is treated as "SteamVR not running, skip" rather than an
/// error. This gives "only notify if SteamVR is running" for free from the API itself, no
/// separate process-presence check needed.
///
/// Stateless per call: Init -> CreateNotification -> Shutdown for every notification, rather than
/// holding a long-lived OpenVR session open for the app's whole lifetime. Slightly more overhead
/// per call, but avoids having to track/re-establish session state as SteamVR comes and goes
/// across a long-running monitor process.
/// </summary>
public static class SteamVrNotifier
{
    #region Verbatim-layout interop (from ValveSoftware/openvr headers/openvr_api.cs)

    [StructLayout(LayoutKind.Sequential)]
    private struct IVRNotifications
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int _CreateNotification(ulong ulOverlayHandle, ulong ulUserValue, int type, IntPtr pchText, int style, ref NotificationBitmap_t pImage, ref uint pNotificationId);
        [MarshalAs(UnmanagedType.FunctionPtr)]
        internal _CreateNotification CreateNotification;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int _RemoveNotification(uint notificationId);
        [MarshalAs(UnmanagedType.FunctionPtr)]
        internal _RemoveNotification RemoveNotification;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationBitmap_t
    {
        public IntPtr m_pImageData;
        public int m_nWidth;
        public int m_nHeight;
        public int m_nBytesPerPixel;
    }

    private const string FnTablePrefix = "FnTable:";
    private const string IVRNotifications_Version = "IVRNotifications_002";
    private const int EVRApplicationType_Background = 3;
    private const int EVRInitError_None = 0;
    private const int EVRInitError_NoServerForBackgroundApp = 121;
    private const int EVRNotificationType_Transient = 0;
    private const int EVRNotificationStyle_Application = 100;

    [DllImport("openvr_api", EntryPoint = "VR_InitInternal2", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint InitInternal2(ref int peError, int eApplicationType, string? pStartupInfo);

    [DllImport("openvr_api", EntryPoint = "VR_ShutdownInternal", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ShutdownInternal();

    [DllImport("openvr_api", EntryPoint = "VR_GetGenericInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetGenericInterface([MarshalAs(UnmanagedType.LPStr)] string pchInterfaceVersion, ref int peError);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    #endregion

    private static bool _dllLoadAttempted;
    private static bool _dllLoaded;

    /// <summary>Attempts to show a transient SteamVR toast notification. Fires and forgets —
    /// never throws, logs and returns false on any failure (SteamVR not running, DLL not found,
    /// interop error). Safe to call from any thread; each call does its own Init/Shutdown cycle.</summary>
    public static bool TryNotify(MonitorConfig config, string message)
    {
        if (!EnsureDllLoaded(config)) return false;

        var initError = EVRInitError_None;
        uint token;
        try
        {
            token = InitInternal2(ref initError, EVRApplicationType_Background, null);
        }
        catch (Exception ex)
        {
            Log.Debug("SteamVrNotifier", $"VR_InitInternal2 threw: {ex.Message}");
            return false;
        }

        if (initError == EVRInitError_NoServerForBackgroundApp)
        {
            Log.Trace("SteamVrNotifier", "SteamVR isn't running — skipping notification.");
            return false;
        }

        if (initError != EVRInitError_None || token == 0)
        {
            Log.Debug("SteamVrNotifier", $"OpenVR init failed with error code {initError} — skipping notification.");
            return false;
        }

        try
        {
            var ifaceError = EVRInitError_None;
            var pInterface = GetGenericInterface(FnTablePrefix + IVRNotifications_Version, ref ifaceError);
            if (pInterface == IntPtr.Zero || ifaceError != EVRInitError_None)
            {
                Log.Debug("SteamVrNotifier", $"Could not get IVRNotifications interface (error {ifaceError}).");
                return false;
            }

            var fnTable = (IVRNotifications)Marshal.PtrToStructure(pInterface, typeof(IVRNotifications))!;

            var textPtr = IntPtr.Zero;
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(message + "\0");
                textPtr = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, textPtr, bytes.Length);

                var blankIcon = new NotificationBitmap_t();
                uint notificationId = 0;
                var result = fnTable.CreateNotification(0, 0, EVRNotificationType_Transient, textPtr, EVRNotificationStyle_Application, ref blankIcon, ref notificationId);

                if (result != 0)
                {
                    Log.Debug("SteamVrNotifier", $"CreateNotification returned error code {result}.");
                    return false;
                }

                Log.Trace("SteamVrNotifier", $"Notification sent: \"{message}\"");
                return true;
            }
            finally
            {
                if (textPtr != IntPtr.Zero) Marshal.FreeHGlobal(textPtr);
            }
        }
        catch (Exception ex)
        {
            Log.Error("SteamVrNotifier", "Sending SteamVR notification threw", ex);
            return false;
        }
        finally
        {
            try { ShutdownInternal(); } catch { /* best effort */ }
        }
    }

    private static bool EnsureDllLoaded(MonitorConfig config)
    {
        if (_dllLoadAttempted) return _dllLoaded;
        _dllLoadAttempted = true;

        var path = config.Paths.OpenVrApiDllPath;
        if (!File.Exists(path))
        {
            Log.Warn("SteamVrNotifier", $"openvr_api.dll not found at {path} — SteamVR notifications disabled.");
            return false;
        }

        var handle = LoadLibrary(path);
        _dllLoaded = handle != IntPtr.Zero;
        if (!_dllLoaded)
            Log.Warn("SteamVrNotifier", $"LoadLibrary failed for {path} (Win32 error {Marshal.GetLastWin32Error()}) — SteamVR notifications disabled.");
        else
            Log.Info("SteamVrNotifier", $"Loaded openvr_api.dll from {path}.");

        return _dllLoaded;
    }
}
