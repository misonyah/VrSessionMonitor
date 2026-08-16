using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using VrSessionMonitor.Config;
using VrSessionMonitor.Optimizations;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

public class RegistryValueOptimizationTests
{
    private static RegistryValueTarget Target(object desired = null!) => new(
        OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue", desired ?? 1, RegistryValueKind.DWord);

    [Fact]
    public async Task CheckAsync_returns_NotApplied_when_value_missing()
    {
        var registry = new FakeRegistryAccessor();
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => new[] { Target() }, registry);

        Assert.Equal(OptimizationStatus.NotApplied, await opt.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_returns_Applied_when_value_matches_desired()
    {
        var registry = new FakeRegistryAccessor();
        registry.Seed(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue", 1);
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => new[] { Target() }, registry);

        Assert.Equal(OptimizationStatus.Applied, await opt.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_returns_Unknown_when_only_some_targets_match()
    {
        var registry = new FakeRegistryAccessor();
        var targets = new[]
        {
            new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "A", 1, RegistryValueKind.DWord),
            new RegistryValueTarget(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "B", 1, RegistryValueKind.DWord),
        };
        registry.Seed(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "A", 1); // only A matches, B is missing
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => targets, registry);

        Assert.Equal(OptimizationStatus.Unknown, await opt.CheckAsync());
    }

    [Fact]
    public async Task ApplyAsync_writes_desired_value_and_captures_original()
    {
        var registry = new FakeRegistryAccessor();
        registry.Seed(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue", 0);
        var target = Target(1);
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => new[] { target }, registry);
        var entry = new OptimizationEntry();

        await opt.ApplyAsync(entry);

        Assert.Equal(1, registry.GetValue(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue"));
        Assert.Equal("0", entry.CapturedOriginalValues[target.StorageKey]);
    }

    [Fact]
    public async Task ApplyAsync_captures_null_when_value_did_not_previously_exist()
    {
        var registry = new FakeRegistryAccessor();
        var target = Target(1);
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => new[] { target }, registry);
        var entry = new OptimizationEntry();

        await opt.ApplyAsync(entry);

        Assert.Null(entry.CapturedOriginalValues[target.StorageKey]);
    }

    [Fact]
    public async Task RevertAsync_restores_captured_value()
    {
        var registry = new FakeRegistryAccessor();
        var target = Target(1);
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => new[] { target }, registry);
        var entry = new OptimizationEntry();
        entry.CapturedOriginalValues[target.StorageKey] = "0";
        registry.Seed(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue", 1);

        await opt.RevertAsync(entry);

        Assert.Equal(0, registry.GetValue(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue"));
        Assert.Empty(entry.CapturedOriginalValues);
    }

    [Fact]
    public async Task RevertAsync_deletes_value_when_it_did_not_previously_exist()
    {
        var registry = new FakeRegistryAccessor();
        var target = Target(1);
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => new[] { target }, registry);
        var entry = new OptimizationEntry();
        entry.CapturedOriginalValues[target.StorageKey] = null;
        registry.Seed(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue", 1);

        await opt.RevertAsync(entry);

        Assert.Contains((OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue"), registry.DeleteCalls);
    }

    [Fact]
    public async Task ApplyAsync_does_not_overwrite_an_already_captured_original_on_reapply()
    {
        var registry = new FakeRegistryAccessor();
        registry.Seed(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue", 0);
        var target = Target(1);
        var opt = new RegistryValueOptimization("id", "Test", OptimizationCategory.Registry, () => new[] { target }, registry);
        var entry = new OptimizationEntry();

        await opt.ApplyAsync(entry); // captures "0"
        registry.SetValue(OptRegistryHive.LocalMachine, @"SOFTWARE\Test\Path", "TestValue", 1, RegistryValueKind.DWord);
        await opt.ApplyAsync(entry); // re-apply must not overwrite the captured "0" with "1"

        Assert.Equal("0", entry.CapturedOriginalValues[target.StorageKey]);
    }
}
