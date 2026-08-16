// tests/VrSessionMonitor.Tests/Optimizations/UsbSelectiveSuspendOptimizationTests.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Config;
using VrSessionMonitor.Optimizations;
using Xunit;

namespace VrSessionMonitor.Tests.Optimizations;

public class UsbSelectiveSuspendOptimizationTests
{
    private static (UsbSelectiveSuspendOptimization Opt, List<string> Calls, Dictionary<string, string> Responses) Build()
    {
        var calls = new List<string>();
        var responses = new Dictionary<string, string>();
        Task<string> Run(string args)
        {
            calls.Add(args);
            return Task.FromResult(responses.TryGetValue(args, out var r) ? r : "");
        }
        return (new UsbSelectiveSuspendOptimization(Run), calls, responses);
    }

    private const string QueryArgs = "/q SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    [Fact]
    public async Task CheckAsync_returns_Applied_when_index_is_zero()
    {
        var (opt, _, responses) = Build();
        responses[QueryArgs] = "Current AC Power Setting Index: 0x00000000\r\n";
        Assert.Equal(OptimizationStatus.Applied, await opt.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_returns_NotApplied_when_index_is_nonzero()
    {
        var (opt, _, responses) = Build();
        responses[QueryArgs] = "Current AC Power Setting Index: 0x00000001\r\n";
        Assert.Equal(OptimizationStatus.NotApplied, await opt.CheckAsync());
    }

    [Fact]
    public async Task ApplyAsync_captures_previous_index_and_sets_zero()
    {
        var (opt, calls, responses) = Build();
        responses[QueryArgs] = "Current AC Power Setting Index: 0x00000001\r\n";
        var entry = new OptimizationEntry();

        await opt.ApplyAsync(entry);

        Assert.Equal("1", entry.CapturedOriginalValues["usb-selective-suspend"]);
        Assert.Contains(calls, c => c.Contains("setacvalueindex") && c.EndsWith(" 0"));
        Assert.Contains(calls, c => c.Contains("setdcvalueindex") && c.EndsWith(" 0"));
    }

    [Fact]
    public async Task RevertAsync_restores_captured_index()
    {
        var (opt, calls, _) = Build();
        var entry = new OptimizationEntry();
        entry.CapturedOriginalValues["usb-selective-suspend"] = "1";

        await opt.RevertAsync(entry);

        Assert.Contains(calls, c => c.Contains("setacvalueindex") && c.EndsWith(" 1"));
        Assert.Empty(entry.CapturedOriginalValues);
    }
}
