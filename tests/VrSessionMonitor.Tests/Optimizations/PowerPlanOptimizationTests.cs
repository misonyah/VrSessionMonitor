// tests/VrSessionMonitor.Tests/Optimizations/PowerPlanOptimizationTests.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Optimizations;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

public class PowerPlanOptimizationTests
{
    private const string ListWithUltimate = @"Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *
Power Scheme GUID: e9a42b02-d5df-448d-aa00-03f14749eb61  (Ultimate Performance)
";
    private const string ListWithAmdBalanced = @"Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *
Power Scheme GUID: 9f245d2a-1111-2222-3333-444455556666  (AMD Ryzen Balanced)
";
    private const string ListWithNeither = "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\r\n";

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D", true)]
    [InlineData("AMD Ryzen 9 7950X3D", true)]
    [InlineData("Intel Core i7-12700K", false)]
    [InlineData("AMD Ryzen 7 7700X", false)]
    public void IsAmdX3D_matches_X3D_in_cpu_name(string cpuName, bool expected)
    {
        Assert.Equal(expected, PowerPlanOptimization.IsAmdX3D(cpuName));
    }

    private static (PowerPlanOptimization Opt, List<string> Calls) BuildWithList(string listOutput, string activeOutput, string cpuName)
    {
        var calls = new List<string>();
        Task<string> Run(string args)
        {
            calls.Add(args);
            if (args == "/list") return Task.FromResult(listOutput);
            if (args == "/getactivescheme") return Task.FromResult(activeOutput);
            return Task.FromResult("");
        }
        var opt = new PowerPlanOptimization(() => cpuName, Run, _ => Task.FromResult(true));
        return (opt, calls);
    }

    [Fact]
    public async Task CheckAsync_returns_Applied_when_active_matches_ultimate_performance_for_non_X3D()
    {
        var (opt, _) = BuildWithList(ListWithUltimate, "Power Scheme GUID: e9a42b02-d5df-448d-aa00-03f14749eb61  (Ultimate Performance)\r\n", "Intel Core i7-12700K");
        Assert.Equal(OptimizationStatus.Applied, await opt.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_returns_NotApplied_when_active_scheme_differs()
    {
        var (opt, _) = BuildWithList(ListWithUltimate, "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n", "Intel Core i7-12700K");
        Assert.Equal(OptimizationStatus.NotApplied, await opt.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_returns_Unknown_when_target_scheme_not_resolvable()
    {
        var (opt, _) = BuildWithList(ListWithNeither, "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n", "Intel Core i7-12700K");
        Assert.Equal(OptimizationStatus.Unknown, await opt.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_targets_AMD_Ryzen_Balanced_for_X3D_cpu()
    {
        var (opt, _) = BuildWithList(ListWithAmdBalanced, "Power Scheme GUID: 9f245d2a-1111-2222-3333-444455556666  (AMD Ryzen Balanced)\r\n", "AMD Ryzen 7 9800X3D");
        Assert.Equal(OptimizationStatus.Applied, await opt.CheckAsync());
    }

    [Fact]
    public async Task ApplyAsync_captures_previous_active_scheme_and_sets_new_one()
    {
        var (opt, calls) = BuildWithList(ListWithUltimate, "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n", "Intel Core i7-12700K");
        var entry = new OptimizationEntry();

        await opt.ApplyAsync(entry);

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", entry.CapturedOriginalValues["power-plan"]);
        Assert.Contains("/setactive e9a42b02-d5df-448d-aa00-03f14749eb61", calls);
    }

    [Fact]
    public async Task RevertAsync_restores_captured_scheme()
    {
        var (opt, calls) = BuildWithList(ListWithUltimate, "irrelevant", "Intel Core i7-12700K");
        var entry = new OptimizationEntry();
        entry.CapturedOriginalValues["power-plan"] = "381b4222-f694-41f0-9685-ff5bb260df2e";

        await opt.RevertAsync(entry);

        Assert.Contains("/setactive 381b4222-f694-41f0-9685-ff5bb260df2e", calls);
        Assert.Empty(entry.CapturedOriginalValues);
    }

    [Fact]
    public async Task EnsureAccessGrantedAsync_grants_immediately_when_target_scheme_already_exists()
    {
        var (opt, _) = BuildWithList(ListWithUltimate, "irrelevant", "Intel Core i7-12700K");
        var entry = new OptimizationEntry();

        await opt.EnsureAccessGrantedAsync(entry);

        Assert.True(entry.AccessGranted);
    }

    [Fact]
    public async Task EnsureAccessGrantedAsync_runs_elevated_duplicatescheme_when_ultimate_missing()
    {
        var elevatedCalls = new List<string>();
        Task<string> Run(string args) => Task.FromResult(args == "/list" ? ListWithNeither : "");
        Task<bool> RunElevated(string args) { elevatedCalls.Add(args); return Task.FromResult(true); }
        var opt = new PowerPlanOptimization(() => "Intel Core i7-12700K", Run, RunElevated);
        var entry = new OptimizationEntry();

        await opt.EnsureAccessGrantedAsync(entry);

        Assert.Contains(elevatedCalls, c => c.Contains("-duplicatescheme"));
        Assert.True(entry.AccessGranted);
    }
}
