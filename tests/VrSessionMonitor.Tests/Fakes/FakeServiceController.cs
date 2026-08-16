using System.Collections.Generic;
using System.Threading.Tasks;
using VrSessionMonitor.Optimizations;

namespace VrSessionMonitor.Tests.Fakes;

public sealed class FakeServiceController : IServiceController
{
    public readonly HashSet<string> ExistingServices = new();
    public readonly HashSet<string> RunningServices = new();
    public readonly List<string> StopCalls = new();
    public readonly List<string> StartCalls = new();
    public readonly HashSet<string> GrantSucceedsFor = new();
    public readonly List<string> GrantCalls = new();

    public bool Exists(string serviceName) => ExistingServices.Contains(serviceName);
    public bool IsRunning(string serviceName) => RunningServices.Contains(serviceName);

    public Task StopAsync(string serviceName)
    {
        StopCalls.Add(serviceName);
        RunningServices.Remove(serviceName);
        return Task.CompletedTask;
    }

    public Task StartAsync(string serviceName)
    {
        StartCalls.Add(serviceName);
        RunningServices.Add(serviceName);
        return Task.CompletedTask;
    }

    public Task<bool> GrantControlPermissionAsync(string serviceName)
    {
        GrantCalls.Add(serviceName);
        return Task.FromResult(GrantSucceedsFor.Contains(serviceName));
    }
}
