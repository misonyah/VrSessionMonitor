using System.Threading.Tasks;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

public class FakeProcessLauncherTests
{
    [Fact]
    public async Task EnsureRunning_records_call_and_marks_running()
    {
        var fake = new FakeProcessLauncher();
        Assert.False(fake.IsRunning("VRChat"));

        var result = await fake.EnsureRunningAsync("VRChat", "vrchat.exe", null, 1000, 100);

        Assert.True(result.Success);
        Assert.Contains("VRChat", fake.EnsureRunningCalls);
        Assert.True(fake.IsRunning("VRChat"));
    }

    [Fact]
    public void Kill_records_call_and_clears_running()
    {
        var fake = new FakeProcessLauncher();
        fake.Running.Add("sr_runtime");

        fake.Kill("sr_runtime");

        Assert.Contains("sr_runtime", fake.KillCalls);
        Assert.False(fake.IsRunning("sr_runtime"));
    }
}
