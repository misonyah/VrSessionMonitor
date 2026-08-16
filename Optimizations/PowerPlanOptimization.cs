// Optimizations/PowerPlanOptimization.cs
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Optimizations;

/// <summary>High Performance / Ultimate Performance power-plan check, with the AMD X3D exception
/// (guide: "AMD X3D CPUs are the exception — use 'AMD Ryzen Balanced' instead"). The
/// getCpuName/runPowercfg/runElevatedPowercfg delegates are the testability seam — production
/// wiring passes CpuInfo.GetName and PowercfgRunner's static methods.</summary>
public sealed class PowerPlanOptimization : IOptimization
{
    private const string UltimatePerformanceName = "Ultimate Performance";
    private const string AmdRyzenBalancedName = "AMD Ryzen Balanced";
    private const string UltimatePerformanceTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string CapturedKey = "power-plan";

    private readonly Func<string> _getCpuName;
    private readonly Func<string, Task<string>> _runPowercfg;
    private readonly Func<string, Task<bool>> _runElevatedPowercfg;

    public PowerPlanOptimization(Func<string> getCpuName, Func<string, Task<string>> runPowercfg, Func<string, Task<bool>> runElevatedPowercfg)
    {
        _getCpuName = getCpuName;
        _runPowercfg = runPowercfg;
        _runElevatedPowercfg = runElevatedPowercfg;
    }

    public string Id => "power-plan";
    public string DisplayName => "Power Plan (High Performance / Ultimate Performance)";
    public OptimizationCategory Category => OptimizationCategory.Power;

    public static bool IsAmdX3D(string cpuName) => cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> ResolveDesiredSchemeGuidAsync()
    {
        var list = await _runPowercfg("/list").ConfigureAwait(false);
        var schemeName = IsAmdX3D(_getCpuName()) ? AmdRyzenBalancedName : UltimatePerformanceName;
        return PowercfgParser.FindSchemeGuidByName(list, schemeName);
    }

    public async Task<OptimizationStatus> CheckAsync()
    {
        var desired = await ResolveDesiredSchemeGuidAsync().ConfigureAwait(false);
        if (desired is null) return OptimizationStatus.Unknown;

        var activeOutput = await _runPowercfg("/getactivescheme").ConfigureAwait(false);
        var active = PowercfgParser.ParseActiveSchemeGuid(activeOutput);
        return string.Equals(active, desired, StringComparison.OrdinalIgnoreCase) ? OptimizationStatus.Applied : OptimizationStatus.NotApplied;
    }

    public async Task EnsureAccessGrantedAsync(OptimizationEntry entry)
    {
        if (entry.AccessGranted) return;

        if (!IsAmdX3D(_getCpuName()))
        {
            var list = await _runPowercfg("/list").ConfigureAwait(false);
            if (PowercfgParser.FindSchemeGuidByName(list, UltimatePerformanceName) is null)
            {
                var ok = await _runElevatedPowercfg($"-duplicatescheme {UltimatePerformanceTemplateGuid}").ConfigureAwait(false);
                if (!ok)
                {
                    Log.Warn("Optimizations", $"Elevated 'powercfg -duplicatescheme' for Ultimate Performance failed or was declined — '{DisplayName}' will keep retrying.");
                    return;
                }
            }
        }
        // AMD X3D path needs no elevation here — "AMD Ryzen Balanced" is provided by the chipset
        // driver, not created by this app; if it's missing, CheckAsync/ApplyAsync just read Unknown.

        entry.AccessGranted = true;
    }

    public async Task ApplyAsync(OptimizationEntry entry)
    {
        var activeOutput = await _runPowercfg("/getactivescheme").ConfigureAwait(false);
        var previous = PowercfgParser.ParseActiveSchemeGuid(activeOutput);
        if (previous is not null) entry.CapturedOriginalValues[CapturedKey] = previous;

        var desired = await ResolveDesiredSchemeGuidAsync().ConfigureAwait(false);
        if (desired is null)
        {
            Log.Warn("Optimizations", $"Could not resolve a target power scheme for '{DisplayName}' — leaving the active scheme unchanged.");
            return;
        }

        await _runPowercfg($"/setactive {desired}").ConfigureAwait(false);
    }

    public async Task RevertAsync(OptimizationEntry entry)
    {
        if (!entry.CapturedOriginalValues.TryGetValue(CapturedKey, out var previous) || previous is null) return;
        await _runPowercfg($"/setactive {previous}").ConfigureAwait(false);
        entry.CapturedOriginalValues.Remove(CapturedKey);
    }
}
