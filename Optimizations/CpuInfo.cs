using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

public static class CpuInfo
{
    /// <summary>CPU display name via WMI (e.g. "AMD Ryzen 7 9800X3D 8-Core Processor"), falling
    /// back to the PROCESSOR_IDENTIFIER environment variable if WMI is unavailable.</summary>
    public static string GetName()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (System.Management.ManagementObject obj in searcher.Get())
                return obj["Name"]?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            Log.Debug("Optimizations", $"Reading CPU name via WMI failed: {ex.Message}");
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
    }
}
