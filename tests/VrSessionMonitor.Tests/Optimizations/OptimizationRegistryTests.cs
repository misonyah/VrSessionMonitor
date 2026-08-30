using System.Linq;
using System.Threading.Tasks;
using VrSessionMonitor.Optimizations;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

public class OptimizationRegistryTests
{
    private static System.Collections.Generic.List<IOptimization> Build() =>
        OptimizationRegistry.BuildAll(
            new FakeRegistryAccessor(), new FakeServiceController(),
            () => "Intel Core i7-12700K",
            _ => Task.FromResult(""),
            _ => Task.FromResult(true));

    [Fact]
    public void BuildAll_returns_every_check()
    {
        // Guards against a check being dropped by an edit. The count is expected to grow — bump it
        // deliberately when adding one, so a check silently disappearing still fails here.
        Assert.Equal(13, Build().Count);
    }

    [Fact]
    public void The_gpu_scheduling_and_mpo_checks_are_present()
    {
        // Both are registry keys a driver or feature update can flip back, so their value is
        // partly that they keep being checked, not only that they can be applied once.
        var ids = Build().Select(o => o.Id).ToList();

        Assert.Contains("gpu-hardware-scheduling", ids);
        Assert.Contains("disable-mpo", ids);
    }

    [Fact]
    public void BuildAll_returns_unique_ids()
    {
        var ids = Build().Select(o => o.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void BuildAll_includes_the_expected_ids()
    {
        var ids = Build().Select(o => o.Id).ToHashSet();
        foreach (var expected in new[]
        {
            "mmcss-responsiveness", "network-throttling-index", "games-task-scheduling",
            "global-timer-resolution", "disable-game-dvr", "vr-process-priority-boost",
            "tcp-low-latency-tuning", "pause-windows-update", "power-plan",
            "usb-selective-suspend", "stray-vm-services-stopped",
        })
            Assert.Contains(expected, ids);
    }

    [Fact]
    public async Task Every_check_reports_a_status_without_throwing()
    {
        foreach (var opt in Build())
            await opt.CheckAsync(); // must not throw against a fresh/empty fake backend
    }
}
