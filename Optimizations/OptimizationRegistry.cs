using System.Net.NetworkInformation;
using Microsoft.Win32;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

/// <summary>Builds the v1 checklist (11 checks) from
/// docs/superpowers/specs/2026-08-16-optimizations-tab-design.md's "v1 checklist" table. Adding a
/// 12th check later means adding one more entry here — Id strings are persistence keys, so never
/// change an existing one.</summary>
public static class OptimizationRegistry
{
    public static List<IOptimization> BuildAll(
        IRegistryAccessor registry, IServiceController services,
        Func<string> getCpuName, Func<string, Task<string>> runPowercfg, Func<string, Task<bool>> runElevatedPowercfg)
    {
        const string multimediaProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

        return new List<IOptimization>
        {
            new RegistryValueOptimization("mmcss-responsiveness", "MMCSS System Responsiveness", OptimizationCategory.Registry,
                () => new[] { new RegistryValueTarget(OptRegistryHive.LocalMachine, multimediaProfile, "SystemResponsiveness", 0, RegistryValueKind.DWord) },
                registry),

            new RegistryValueOptimization("network-throttling-index", "Network Throttling Index", OptimizationCategory.Registry,
                () => new[] { new RegistryValueTarget(OptRegistryHive.LocalMachine, multimediaProfile, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord) },
                registry),

            new RegistryValueOptimization("games-task-scheduling", "Games Task Scheduling (Priority/GPU Priority/Category)", OptimizationCategory.Registry,
                () => new[]
                {
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, multimediaProfile + @"\Tasks\Games", "Priority", 6, RegistryValueKind.DWord),
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, multimediaProfile + @"\Tasks\Games", "GPU Priority", 8, RegistryValueKind.DWord),
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, multimediaProfile + @"\Tasks\Games", "Scheduling Category", "High", RegistryValueKind.String),
                },
                registry),

            new RegistryValueOptimization("global-timer-resolution", "Global Timer Resolution", OptimizationCategory.Registry,
                () => new[] { new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord) },
                registry),

            new RegistryValueOptimization("disable-game-dvr", "Disable Game DVR/Capture", OptimizationCategory.Registry,
                () => new[]
                {
                    new RegistryValueTarget(OptRegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord),
                    new RegistryValueTarget(OptRegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord),
                },
                registry),

            new RegistryValueOptimization("vr-process-priority-boost", "VR Process Priority Boost (IFEO)", OptimizationCategory.Registry,
                () => new[] { "vrserver.exe", "vrmonitor.exe", "VRChat.exe" }.Select(exe =>
                    new RegistryValueTarget(OptRegistryHive.LocalMachine,
                        $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exe}\PerfOptions",
                        "CpuPriorityClass", 3, RegistryValueKind.DWord)),
                registry),

            new RegistryValueOptimization("tcp-low-latency-tuning", "TCP Low-Latency Tuning (per active adapter)", OptimizationCategory.Registry,
                () => GetActiveAdapterInterfaceGuids().SelectMany(guid => new[]
                {
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}", "TCPNoDelay", 1, RegistryValueKind.DWord),
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}", "TcpAckFrequency", 1, RegistryValueKind.DWord),
                }),
                registry),

            new RegistryValueOptimization("pause-windows-update", "Pause Windows Update during session", OptimizationCategory.Registry,
                () => new[]
                {
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord),
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", 2, RegistryValueKind.DWord),
                    new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, RegistryValueKind.DWord),
                },
                registry),

            new PowerPlanOptimization(getCpuName, runPowercfg, runElevatedPowercfg),

            new UsbSelectiveSuspendOptimization(runPowercfg),

            new ServiceStateOptimization("stray-vm-services-stopped", "Stray VM/WSL Services Stopped", new[] { "vmms", "vboxdrv", "WslService" }, services),
        };
    }

    private static IEnumerable<string> GetActiveAdapterInterfaceGuids()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug("Optimizations", $"Enumerating network adapters for TCP tuning failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}
