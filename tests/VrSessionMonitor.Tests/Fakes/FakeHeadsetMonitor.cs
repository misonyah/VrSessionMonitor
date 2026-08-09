using System;
using VrSessionMonitor.Modules;

namespace VrSessionMonitor.Tests.Fakes;

public sealed class FakeHeadsetMonitor : IHeadsetMonitor
{
    public bool IsOnline { get; private set; }
    public string RespondingIp { get; set; } = "10.0.0.5";
    public event EventHandler<HeadsetStateChangedEventArgs>? StateChanged;

    /// <summary>Sets online state and raises StateChanged, mirroring the real transition edge.</summary>
    public void SetOnline(bool online)
    {
        if (online == IsOnline) return;
        IsOnline = online;
        StateChanged?.Invoke(this, new HeadsetStateChangedEventArgs { IsOnline = online, Ip = RespondingIp });
    }
}
