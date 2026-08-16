using VrSessionMonitor.Config;

namespace VrSessionMonitor.Optimizations;

/// <summary>Disables USB Selective Suspend on the current power scheme (AC and DC). Needs no
/// elevation — powercfg's AC/DC value-index commands operate on the calling user's own active
/// scheme without an admin prompt.</summary>
public sealed class UsbSelectiveSuspendOptimization : IOptimization
{
    private const string UsbSubgroupGuid = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SelectiveSuspendSettingGuid = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string CapturedKey = "usb-selective-suspend";

    private readonly Func<string, Task<string>> _runPowercfg;

    public UsbSelectiveSuspendOptimization(Func<string, Task<string>> runPowercfg) => _runPowercfg = runPowercfg;

    public string Id => "usb-selective-suspend";
    public string DisplayName => "USB Selective Suspend Disabled";
    public OptimizationCategory Category => OptimizationCategory.Power;

    private Task<string> QueryAsync() => _runPowercfg($"/q SCHEME_CURRENT {UsbSubgroupGuid} {SelectiveSuspendSettingGuid}");

    public async Task<OptimizationStatus> CheckAsync()
    {
        var current = PowercfgParser.ParseCurrentAcValueIndex(await QueryAsync().ConfigureAwait(false));
        return current switch { 0 => OptimizationStatus.Applied, null => OptimizationStatus.Unknown, _ => OptimizationStatus.NotApplied };
    }

    public Task EnsureAccessGrantedAsync(OptimizationEntry entry)
    {
        entry.AccessGranted = true;
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(OptimizationEntry entry)
    {
        var previous = PowercfgParser.ParseCurrentAcValueIndex(await QueryAsync().ConfigureAwait(false));
        if (previous is int p) entry.CapturedOriginalValues[CapturedKey] = p.ToString();

        await _runPowercfg($"/setacvalueindex SCHEME_CURRENT {UsbSubgroupGuid} {SelectiveSuspendSettingGuid} 0").ConfigureAwait(false);
        await _runPowercfg($"/setdcvalueindex SCHEME_CURRENT {UsbSubgroupGuid} {SelectiveSuspendSettingGuid} 0").ConfigureAwait(false);
        await _runPowercfg("/setactive SCHEME_CURRENT").ConfigureAwait(false);
    }

    public async Task RevertAsync(OptimizationEntry entry)
    {
        if (!entry.CapturedOriginalValues.TryGetValue(CapturedKey, out var captured) || !int.TryParse(captured, out var value)) return;

        await _runPowercfg($"/setacvalueindex SCHEME_CURRENT {UsbSubgroupGuid} {SelectiveSuspendSettingGuid} {value}").ConfigureAwait(false);
        await _runPowercfg($"/setdcvalueindex SCHEME_CURRENT {UsbSubgroupGuid} {SelectiveSuspendSettingGuid} {value}").ConfigureAwait(false);
        await _runPowercfg("/setactive SCHEME_CURRENT").ConfigureAwait(false);
        entry.CapturedOriginalValues.Remove(CapturedKey);
    }
}
