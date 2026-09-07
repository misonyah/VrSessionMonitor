using System.Runtime.InteropServices;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Suspend;

/// <summary>
/// Free physical RAM, straight from the OS.
///
/// GlobalMemoryStatusEx rather than GC.GetGCMemoryInfo: the GC's view describes this process's
/// relationship with the heap, while the question here is machine-wide — how much physical memory
/// is left for VRChat, given everything else running. A negative return means it could not be read,
/// which callers treat as "do nothing" rather than as zero, since misreading it as zero would
/// freeze the user's applications for no reason.
/// </summary>
public static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    private const double BytesPerGb = 1024d * 1024d * 1024d;

    /// <summary>Free physical RAM in GB, or -1 when it could not be read.</summary>
    public static double FreePhysicalGb()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(status)) return -1;
            return status.ullAvailPhys / BytesPerGb;
        }
        catch (Exception ex)
        {
            Log.Debug("Memory", $"Could not read free physical memory: {ex.Message}");
            return -1;
        }
    }

    /// <summary>Total physical RAM in GB, or -1 when it could not be read.</summary>
    public static double TotalPhysicalGb()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(status)) return -1;
            return status.ullTotalPhys / BytesPerGb;
        }
        catch
        {
            return -1;
        }
    }
}
