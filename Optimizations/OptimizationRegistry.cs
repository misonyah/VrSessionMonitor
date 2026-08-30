using System.Net.NetworkInformation;
using Microsoft.Win32;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

/// <summary>Builds the optimization checklist. Started as the 11 in
/// docs/superpowers/specs/2026-08-16-optimizations-tab-design.md's "v1 checklist" table; adding
/// another means adding one more entry here — Id strings are persistence keys, so never change an
/// existing one.
///
/// Deliberately NOT included: MSI mode for the GPU
/// (Enum\PCI\...\MessageSignaledInterruptProperties\MSISupported). It is already enabled by
/// default on both GPUs here, and the widely-shared benchmarks attributed to it actually come from
/// raising the GPU's interrupt DevicePriority alongside it — which can starve other devices on the
/// same controller. On this machine that means the USB tree the face tracker and SlimeVR dongles
/// live on, so the risk lands exactly where a VR session would notice it.</summary>
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
                registry,
                // Grant the parent ONCE with inheritance instead of each adapter's own subkey:
                // Windows creates a subkey per adapter, so per-target granting produced a fresh UAC
                // prompt every time one appeared. See RegistryValueOptimization's constructor doc.
                inheritedGrantPath: @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"),

            // Hardware-Accelerated GPU Scheduling. 2 = on, 1 = off; the value only takes effect
            // after a reboot.
            //
            // Targets ENABLED, but this is the least clear-cut check here and is really about
            // VISIBILITY: a driver update or a Windows feature update can silently flip it, and
            // then a session that used to be smooth is not, with nothing to point at. HAGS is
            // genuinely bidirectional — it lowers submission latency and is what Virtual Desktop's
            // encoder path prefers, while some SteamVR setups stutter with it on. If a session
            // regresses after enabling it, revert this one first; that it can be reverted from the
            // same row is the point.
            new RegistryValueOptimization("gpu-hardware-scheduling", "Hardware-Accelerated GPU Scheduling (reboot required)", OptimizationCategory.Registry,
                () => new[] { new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord) },
                registry),

            // Multi-Plane Overlay. Setting OverlayTestMode to 5 is the documented way to switch MPO
            // off, and it is the standard fix for DWM flicker and stutter on mixed-refresh-rate
            // multi-monitor setups — exactly the shape of this machine, which drives a headset
            // alongside desktop displays.
            //
            // Not free: MPO exists so the compositor can hand planes straight to the display engine,
            // which saves power and a copy. Disabling it costs that. Worth it only if flicker or
            // desktop stutter is actually being seen — this is a fix for a symptom, not a
            // default-on tune, and it applies at the next DWM restart or reboot.
            new RegistryValueOptimization("disable-mpo", "Disable Multi-Plane Overlay (fixes DWM flicker/stutter)", OptimizationCategory.Registry,
                () => new[] { new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord) },
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

    /// <summary>
    /// Physical adapters only — deliberately NOT every non-loopback interface that happens to be up.
    ///
    /// Confirmed live 2026-08-21: the broader filter matched every transient virtual adapter (VPN,
    /// the PC's own hotspot, Hyper-V/WSL switches, Virtual Desktop), each of which owns a
    /// GUID-named registry subkey. The granted-target list had grown to 44 entries while only 4
    /// interfaces were actually up, and every newly-appearing adapter triggered another elevation
    /// prompt. Filtering to real hardware is also simply more correct: VR traffic reaches the
    /// headset over Ethernet or Wi-Fi, so tuning a WSL switch's TCP stack achieves nothing.
    /// </summary>
    private static IEnumerable<string> GetActiveAdapterInterfaceGuids()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                  or NetworkInterfaceType.GigabitEthernet
                                                  or NetworkInterfaceType.FastEthernetT
                                                  or NetworkInterfaceType.FastEthernetFx
                                                  or NetworkInterfaceType.Wireless80211)
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
