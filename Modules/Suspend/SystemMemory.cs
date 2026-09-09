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

    // --- hard fault rate -----------------------------------------------------
    // Available memory alone does not say whether anything is HURTING. Windows keeps genuinely
    // free memory at almost zero by design and fills the rest with reclaimable cache, so a small
    // "free" number is normal rather than alarming. What correlates with the stutter is paging:
    // measured on this machine, 297 hard faults/sec while VRChat stuttered, 64/sec once healthy.
    //
    // Read as a delta of the raw cumulative counter rather than the formatted one:
    // Win32_PerfFormattedData needs two samples to mean anything, and its first reading is a
    // meaningless accumulated value (97190/sec observed on a machine that was not paging at all).

    private static long _lastPagesInput = -1;
    private static DateTime _lastPagesInputAt;
    private static readonly object PagesLock = new();

    /// <summary>
    /// Hard faults per second since the previous call, or -1 when unavailable or on the first call
    /// — a rate needs two samples, and there is no honest answer from one.
    ///
    /// Counts every page read from disk, which includes memory-mapped file access and first touch
    /// of executable pages, not only pagefile reads under pressure. It is therefore corroboration
    /// for a low-memory reading rather than proof of thrashing on its own, which is why the caller
    /// requires both.
    /// </summary>
    public static double HardFaultsPerSecond()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT PagesInputPersec FROM Win32_PerfRawData_PerfOS_Memory");

            foreach (var o in searcher.Get())
            {
                var pages = Convert.ToInt64(o["PagesInputPersec"]);
                var now = DateTime.UtcNow;

                lock (PagesLock)
                {
                    var previous = _lastPagesInput;
                    var previousAt = _lastPagesInputAt;
                    _lastPagesInput = pages;
                    _lastPagesInputAt = now;

                    if (previous < 0) return -1;                 // first sample
                    var seconds = (now - previousAt).TotalSeconds;
                    if (seconds <= 0) return -1;
                    if (pages < previous) return -1;             // counter wrapped or reset

                    return (pages - previous) / seconds;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Memory", $"Could not read the hard fault rate: {ex.Message}");
        }

        return -1;
    }

    /// <summary>Forgets the previous sample, so the next call starts a fresh rate window. Called
    /// when a session begins, so a rate is never computed across a gap of hours.</summary>
    public static void ResetHardFaultSampling()
    {
        lock (PagesLock)
        {
            _lastPagesInput = -1;
            _lastPagesInputAt = default;
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
