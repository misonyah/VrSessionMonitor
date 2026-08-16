namespace VrSessionMonitor.Optimizations;

/// <summary>Testability seam over Windows service query/start/stop — mirrors IProcessLauncher's
/// role for processes. WindowsServiceController is the real implementation; FakeServiceController
/// (test project) is in-memory.</summary>
public interface IServiceController
{
    /// <summary>True if the named service exists at all (Stopped still counts).</summary>
    bool Exists(string serviceName);
    /// <summary>True if the named service exists and is currently Running.</summary>
    bool IsRunning(string serviceName);
    Task StopAsync(string serviceName);
    Task StartAsync(string serviceName);
    /// <summary>One-time elevated grant (mirrors SRanipalServicePermissions' sc-sdset dance) so
    /// future Stop/StartAsync calls need no elevation. Returns true on success.</summary>
    Task<bool> GrantControlPermissionAsync(string serviceName);
}
