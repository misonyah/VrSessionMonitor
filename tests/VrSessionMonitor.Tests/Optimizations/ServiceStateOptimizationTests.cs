using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Optimizations;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

public class ServiceStateOptimizationTests
{
    private static ServiceStateOptimization Build(FakeServiceController services) =>
        new("stray-vm-services-stopped", "Stray VM/WSL Services Stopped", new[] { "vmms", "vboxdrv", "WslService" }, services);

    [Fact]
    public async Task CheckAsync_returns_Applied_when_none_running()
    {
        var services = new FakeServiceController();
        var opt = Build(services);
        Assert.Equal(OptimizationStatus.Applied, await opt.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_returns_NotApplied_when_any_is_running()
    {
        var services = new FakeServiceController();
        services.ExistingServices.Add("vmms");
        services.RunningServices.Add("vmms");
        var opt = Build(services);
        Assert.Equal(OptimizationStatus.NotApplied, await opt.CheckAsync());
    }

    [Fact]
    public async Task ApplyAsync_stops_every_running_service_in_the_group()
    {
        var services = new FakeServiceController();
        foreach (var s in new[] { "vmms", "vboxdrv" })
        {
            services.ExistingServices.Add(s);
            services.RunningServices.Add(s);
        }
        var opt = Build(services);
        var entry = new OptimizationEntry();

        await opt.ApplyAsync(entry);

        Assert.Contains("vmms", services.StopCalls);
        Assert.Contains("vboxdrv", services.StopCalls);
        Assert.DoesNotContain("WslService", services.StopCalls); // never existed — nothing to stop
    }

    [Fact]
    public async Task RevertAsync_never_restarts_anything()
    {
        var services = new FakeServiceController();
        var opt = Build(services);
        var entry = new OptimizationEntry();

        await opt.RevertAsync(entry);

        Assert.Empty(services.StartCalls);
    }

    [Fact]
    public async Task EnsureAccessGrantedAsync_grants_for_every_existing_service_in_the_group()
    {
        var services = new FakeServiceController();
        services.ExistingServices.Add("vmms");
        services.ExistingServices.Add("vboxdrv");
        services.GrantSucceedsFor.Add("vmms");
        services.GrantSucceedsFor.Add("vboxdrv");
        var opt = Build(services);
        var entry = new OptimizationEntry();

        await opt.EnsureAccessGrantedAsync(entry);

        Assert.True(entry.AccessGranted);
        Assert.Contains("vmms", services.GrantCalls);
        Assert.Contains("vboxdrv", services.GrantCalls);
        Assert.DoesNotContain("WslService", services.GrantCalls); // doesn't exist — nothing to grant on
    }

    [Fact]
    public async Task EnsureAccessGrantedAsync_leaves_AccessGranted_false_if_any_grant_fails()
    {
        var services = new FakeServiceController();
        services.ExistingServices.Add("vmms");
        services.ExistingServices.Add("vboxdrv");
        services.GrantSucceedsFor.Add("vmms"); // vboxdrv's grant will fail (not in GrantSucceedsFor)
        var opt = Build(services);
        var entry = new OptimizationEntry();

        await opt.EnsureAccessGrantedAsync(entry);

        Assert.False(entry.AccessGranted);
    }

    /// <summary>Proves the fix for the "grant-once model doesn't handle dynamic target sets"
    /// finding: stray-vm-services-stopped set AccessGranted=true permanently the moment none of
    /// the 3 services existed yet, so a service installed LATER (e.g. WSL enabled after the app
    /// was already configured) would never get its own elevation grant requested. With per-target
    /// tracking via GrantedTargets, a newly-appeared service must trigger a fresh grant request
    /// for itself only — an already-granted service must NOT be re-requested.</summary>
    [Fact]
    public async Task EnsureAccessGrantedAsync_only_regrants_for_a_newly_appeared_service()
    {
        var services = new FakeServiceController();
        services.ExistingServices.Add("vmms");
        services.GrantSucceedsFor.Add("vmms");
        var opt = Build(services);
        var entry = new OptimizationEntry();

        await opt.EnsureAccessGrantedAsync(entry); // grants vmms only
        Assert.True(entry.AccessGranted);
        Assert.Contains("vmms", services.GrantCalls);
        services.GrantCalls.Clear();

        // vboxdrv "installed" later, after the first grant already succeeded.
        services.ExistingServices.Add("vboxdrv");
        services.GrantSucceedsFor.Add("vboxdrv");

        await opt.EnsureAccessGrantedAsync(entry);

        Assert.Contains("vboxdrv", services.GrantCalls);
        Assert.DoesNotContain("vmms", services.GrantCalls); // already granted — not re-requested
        Assert.True(entry.AccessGranted);
    }
}
