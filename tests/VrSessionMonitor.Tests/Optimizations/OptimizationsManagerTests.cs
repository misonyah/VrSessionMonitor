using System.IO;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Optimizations;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

public class OptimizationsManagerTests
{
    private static (OptimizationsManager Mgr, FakeOptimization Opt, string ConfigPath) Build()
    {
        var opt = new FakeOptimization { Id = "test-check" };
        var config = new MonitorConfig();
        var path = Path.Combine(Path.GetTempPath(), $"vrsm-opt-test-{Guid.NewGuid()}.json");
        var mgr = new OptimizationsManager(config, path, new List<IOptimization> { opt });
        return (mgr, opt, path);
    }

    [Fact]
    public void Constructor_seeds_a_default_Off_entry_for_every_registered_check()
    {
        var (mgr, opt, _) = Build();
        var entry = mgr.GetEntry(opt.Id);
        Assert.Equal(OptimizationMode.Off, entry.Mode);
    }

    [Fact]
    public async Task SetModeAsync_to_Manual_grants_access_and_sets_mode()
    {
        var (mgr, opt, _) = Build();
        await mgr.SetModeAsync(opt, OptimizationMode.Manual);

        Assert.Equal(OptimizationMode.Manual, mgr.GetEntry(opt.Id).Mode);
        Assert.Contains("EnsureAccessGranted", opt.Calls);
    }

    [Fact]
    public async Task SetModeAsync_falls_back_to_Off_when_grant_fails()
    {
        var (mgr, opt, _) = Build();
        opt.GrantSucceeds = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.SetModeAsync(opt, OptimizationMode.Auto));

        Assert.Equal(OptimizationMode.Off, mgr.GetEntry(opt.Id).Mode);
    }

    [Fact]
    public async Task SetModeAsync_to_Off_does_not_request_access()
    {
        var (mgr, opt, _) = Build();
        await mgr.SetModeAsync(opt, OptimizationMode.Off);
        Assert.DoesNotContain("EnsureAccessGranted", opt.Calls);
    }

    [Fact]
    public async Task ApplyManualAsync_calls_Apply_when_access_is_granted()
    {
        var (mgr, opt, _) = Build();
        await mgr.ApplyManualAsync(opt);
        Assert.Contains("Apply", opt.Calls);
    }

    [Fact]
    public async Task HandleSteamVrRunningChangedAsync_true_applies_only_Auto_mode_entries()
    {
        var (mgr, opt, _) = Build();
        await mgr.SetModeAsync(opt, OptimizationMode.Auto);
        opt.Calls.Clear();
        opt.StatusToReturn = OptimizationStatus.NotApplied;

        await mgr.HandleSteamVrRunningChangedAsync(true);

        Assert.Contains("Apply", opt.Calls);
    }

    [Fact]
    public async Task HandleSteamVrRunningChangedAsync_true_skips_apply_when_already_Applied()
    {
        var (mgr, opt, _) = Build();
        await mgr.SetModeAsync(opt, OptimizationMode.Auto);
        opt.Calls.Clear();
        opt.StatusToReturn = OptimizationStatus.Applied;

        await mgr.HandleSteamVrRunningChangedAsync(true);

        Assert.DoesNotContain("Apply", opt.Calls);
    }

    [Fact]
    public async Task HandleSteamVrRunningChangedAsync_false_reverts_Auto_mode_entries()
    {
        var (mgr, opt, _) = Build();
        await mgr.SetModeAsync(opt, OptimizationMode.Auto);
        opt.Calls.Clear();

        await mgr.HandleSteamVrRunningChangedAsync(false);

        Assert.Contains("Revert", opt.Calls);
    }

    [Fact]
    public async Task HandleSteamVrRunningChangedAsync_ignores_Manual_and_Off_entries()
    {
        var (mgr, opt, _) = Build();
        await mgr.SetModeAsync(opt, OptimizationMode.Manual);
        opt.Calls.Clear();

        await mgr.HandleSteamVrRunningChangedAsync(true);
        await mgr.HandleSteamVrRunningChangedAsync(false);

        Assert.DoesNotContain("Apply", opt.Calls);
        Assert.DoesNotContain("Revert", opt.Calls);
    }

    [Fact]
    public async Task HandleSteamVrRunningChangedAsync_does_not_throw_when_an_optimization_throws()
    {
        var (mgr, opt, _) = Build();
        await mgr.SetModeAsync(opt, OptimizationMode.Auto);
        opt.ThrowOnApply = new InvalidOperationException("boom");

        var ex = await Record.ExceptionAsync(() => mgr.HandleSteamVrRunningChangedAsync(true));

        Assert.Null(ex);
    }
}
