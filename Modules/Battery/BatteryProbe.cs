using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VrSessionMonitor.Modules.Battery;

/// <summary>One device's last known battery state.</summary>
public sealed record DeviceBattery(
    string Serial,
    string Assignment,
    double Percent,
    bool Charging,
    DateTime SampledAtUtc);

/// <summary>
/// Reads tracked-device battery levels from OpenVR.
///
/// RUNS IN A CHILD PROCESS, deliberately. This uses IVRSystem's raw function table — the exact
/// interop that crashed this whole application with an unhandled access violation (0xc0000005 in
/// coreclr.dll) on 2026-07-29, which is why HmdActivityMonitor is still disabled. A native AV
/// cannot be caught by try/catch, so no amount of defensive code inside the process helps: the
/// only real protection is for the crash to land somewhere that does not matter. The parent spawns
/// this, reads JSON from stdout, and treats a crashed or silent child as "no reading this time".
///
/// The property indices are OpenVR's own well-known constants, not vtable positions, so unlike the
/// activity-level call they do not depend on counting function slots correctly.
/// </summary>
public static class BatteryProbe
{
    private const string FnTablePrefix = "FnTable:";
    private const string IVRSystem_Version = "IVRSystem_023";
    private const int EVRApplicationType_Background = 3;
    private const int EVRInitError_None = 0;
    private const uint MaxTrackedDeviceCount = 64;

    // OpenVR ETrackedDeviceProperty values.
    private const uint Prop_DeviceIsCharging_Bool = 1001;
    private const uint Prop_DeviceBatteryPercentage_Float = 1002;
    private const uint Prop_DeviceProvidesBatteryStatus_Bool = 1004;
    private const uint Prop_SerialNumber_String = 1002 + 1; // 1003
    private const uint Prop_RenderModelName_String = 1013;
    private const uint Prop_ControllerType_String = 4073;

    [DllImport("openvr_api", EntryPoint = "VR_InitInternal2", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint InitInternal2(ref int peError, int eApplicationType, string? pStartupInfo);

    [DllImport("openvr_api", EntryPoint = "VR_ShutdownInternal", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ShutdownInternal();

    [DllImport("openvr_api", EntryPoint = "VR_GetGenericInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetGenericInterface([MarshalAs(UnmanagedType.LPStr)] string pchInterfaceVersion, ref int peError);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void _Unused();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int _GetTrackedDeviceClass(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool _IsTrackedDeviceConnected(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool _GetBoolTrackedDeviceProperty(uint deviceIndex, uint prop, ref int error);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate float _GetFloatTrackedDeviceProperty(uint deviceIndex, uint prop, ref int error);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint _GetStringTrackedDeviceProperty(uint deviceIndex, uint prop, StringBuilder value, uint bufferSize, ref int error);

    /// <summary>
    /// IVRSystem's table up to the property accessors. The 15 leading slots are typed with one
    /// shared no-op delegate purely to occupy the right number of pointer-sized positions —
    /// Marshal.PtrToStructure only cares about each field's marshaled SIZE, and every function
    /// pointer is IntPtr-sized regardless of its signature. Ordering follows openvr_api.cs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IVRSystemFnTable
    {
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
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetTrackedDeviceActivityLevel;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused ApplyTransform;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetTrackedDeviceIndexForControllerRole;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetControllerRoleForTrackedDeviceIndex;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _GetTrackedDeviceClass GetTrackedDeviceClass;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _IsTrackedDeviceConnected IsTrackedDeviceConnected;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _GetBoolTrackedDeviceProperty GetBoolTrackedDeviceProperty;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _GetFloatTrackedDeviceProperty GetFloatTrackedDeviceProperty;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetInt32TrackedDeviceProperty;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetUint64TrackedDeviceProperty;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetMatrix34TrackedDeviceProperty;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _Unused GetArrayTrackedDeviceProperty;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal _GetStringTrackedDeviceProperty GetStringTrackedDeviceProperty;
    }

    /// <summary>Reads every connected device that reports a battery, and writes the result to
    /// stdout as JSON. Called only in the child process.</summary>
    public static int RunAsChildProcess(string openVrApiDllPath)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            var readings = Read(openVrApiDllPath);
            Console.WriteLine(JsonSerializer.Serialize(readings));
            return 0;
        }
        catch (Exception ex)
        {
            // stderr so it can never be mistaken for a reading.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static List<DeviceBattery> Read(string openVrApiDllPath)
    {
        var result = new List<DeviceBattery>();

        if (File.Exists(openVrApiDllPath)) LoadLibrary(openVrApiDllPath);

        var initError = EVRInitError_None;
        var token = InitInternal2(ref initError, EVRApplicationType_Background, null);
        if (initError != EVRInitError_None || token == 0) return result; // SteamVR not running

        try
        {
            var ifaceError = EVRInitError_None;
            var pInterface = GetGenericInterface(FnTablePrefix + IVRSystem_Version, ref ifaceError);
            if (pInterface == IntPtr.Zero || ifaceError != EVRInitError_None) return result;

            var fn = (IVRSystemFnTable)Marshal.PtrToStructure(pInterface, typeof(IVRSystemFnTable))!;
            var now = DateTime.UtcNow;

            for (uint i = 0; i < MaxTrackedDeviceCount; i++)
            {
                if (!fn.IsTrackedDeviceConnected(i)) continue;

                var error = 0;
                if (!fn.GetBoolTrackedDeviceProperty(i, Prop_DeviceProvidesBatteryStatus_Bool, ref error) || error != 0)
                    continue; // wired devices and base stations have no battery to report

                error = 0;
                var percent = fn.GetFloatTrackedDeviceProperty(i, Prop_DeviceBatteryPercentage_Float, ref error);
                if (error != 0) continue;

                error = 0;
                var charging = fn.GetBoolTrackedDeviceProperty(i, Prop_DeviceIsCharging_Bool, ref error);

                result.Add(new DeviceBattery(
                    ReadString(fn, i, Prop_SerialNumber_String),
                    DescribeDevice(fn, i),
                    Math.Round(percent * 100, 0),
                    charging,
                    now));
            }
        }
        finally
        {
            try { ShutdownInternal(); } catch { /* best effort */ }
        }

        return result;
    }

    /// <summary>A human-usable label. Controller type is the most specific thing available (it is
    /// what distinguishes a Tundra tracker from a Vive one); the render model name is the
    /// fallback, since a bare device index means nothing to anyone.</summary>
    private static string DescribeDevice(IVRSystemFnTable fn, uint index)
    {
        var controllerType = ReadString(fn, index, Prop_ControllerType_String);
        if (controllerType.Length > 0) return controllerType;

        var model = ReadString(fn, index, Prop_RenderModelName_String);
        return model.Length > 0 ? model : $"device {index}";
    }

    private static string ReadString(IVRSystemFnTable fn, uint index, uint prop)
    {
        try
        {
            var error = 0;
            var buffer = new StringBuilder(256);
            var length = fn.GetStringTrackedDeviceProperty(index, prop, buffer, 256, ref error);
            return error == 0 && length > 0 ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
    }
}
