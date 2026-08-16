using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Optimizations;

namespace VrSessionMonitor.Tests.Fakes;

public sealed class FakeOptimization : IOptimization
{
    public string Id { get; init; } = "fake";
    public string DisplayName { get; init; } = "Fake";
    public OptimizationCategory Category { get; init; } = OptimizationCategory.Registry;

    public OptimizationStatus StatusToReturn = OptimizationStatus.NotApplied;
    public bool GrantSucceeds = true;
    public Exception? ThrowOnApply;
    public Exception? ThrowOnRevert;

    public readonly List<string> Calls = new();

    public Task<OptimizationStatus> CheckAsync() { Calls.Add("Check"); return Task.FromResult(StatusToReturn); }

    public Task EnsureAccessGrantedAsync(OptimizationEntry entry)
    {
        Calls.Add("EnsureAccessGranted");
        if (GrantSucceeds) entry.AccessGranted = true;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(OptimizationEntry entry)
    {
        Calls.Add("Apply");
        if (ThrowOnApply is not null) throw ThrowOnApply;
        return Task.CompletedTask;
    }

    public Task RevertAsync(OptimizationEntry entry)
    {
        Calls.Add("Revert");
        if (ThrowOnRevert is not null) throw ThrowOnRevert;
        return Task.CompletedTask;
    }
}
