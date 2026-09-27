namespace VrSessionMonitor.Modules;

/// <summary>Read-only view of the headset's reachability plus its transition event, so consumers
/// (SessionOrchestrator, the VirtualHere/SRanipal lifecycle manager) can depend on an abstraction
/// and tests can supply a fake with no real pings.</summary>
public interface IHeadsetMonitor
{
    bool IsOnline { get; }
    string RespondingIp { get; }
    /// <summary>Id of the headset profile that answered the last successful ping; "" when offline.</summary>
    string ActiveHeadsetId { get; }
    event EventHandler<HeadsetStateChangedEventArgs> StateChanged;
}
