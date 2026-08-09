using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class SessionOrchestratorGraphTests
{
    [Fact]
    public void Idle_permits_start_only()
    {
        var sm = SessionOrchestrator.BuildStateMachine(SessionState.Idle);
        Assert.True(sm.CanFire(SessionTrigger.StartSession));
        Assert.False(sm.CanFire(SessionTrigger.StreamConfirmed));
    }

    [Fact]
    public void LaunchingApps_permits_both_flap_skip_and_await_stream()
    {
        var sm = SessionOrchestrator.BuildStateMachine(SessionState.LaunchingApps);
        Assert.True(sm.CanFire(SessionTrigger.PingFlapDetected));
        Assert.True(sm.CanFire(SessionTrigger.AwaitStream));
    }

    [Fact]
    public void WaitingForStream_routes_confirmed_and_timeout_differently()
    {
        var confirmed = SessionOrchestrator.BuildStateMachine(SessionState.WaitingForStream);
        confirmed.Fire(SessionTrigger.StreamConfirmed);
        Assert.Equal(SessionState.LaunchingApps, confirmed.State);

        var timedOut = SessionOrchestrator.BuildStateMachine(SessionState.WaitingForStream);
        timedOut.Fire(SessionTrigger.StreamTimedOut);
        Assert.Equal(SessionState.Complete, timedOut.State);
    }

    [Fact]
    public void Fault_is_permitted_from_every_working_state()
    {
        foreach (var s in new[] { SessionState.HeadsetDetected, SessionState.PreflightChecks,
                                  SessionState.LaunchingApps, SessionState.WaitingForStream,
                                  SessionState.LaunchingVrChat, SessionState.LaunchingSlimeVr,
                                  SessionState.LaunchingVrOverlay })
        {
            var sm = SessionOrchestrator.BuildStateMachine(s);
            Assert.True(sm.CanFire(SessionTrigger.Fault));
            sm.Fire(SessionTrigger.Fault);
            Assert.Equal(SessionState.Failed, sm.State);
        }
    }

    [Fact]
    public void Full_launch_chain_reaches_complete()
    {
        var sm = SessionOrchestrator.BuildStateMachine(SessionState.Idle);
        sm.Fire(SessionTrigger.StartSession);   // HeadsetDetected
        sm.Fire(SessionTrigger.BeginPreflight); // PreflightChecks
        sm.Fire(SessionTrigger.PreflightDone);  // LaunchingApps
        sm.Fire(SessionTrigger.AwaitStream);    // WaitingForStream
        sm.Fire(SessionTrigger.StreamConfirmed);// LaunchingApps (Steam)
        sm.Fire(SessionTrigger.VrChatPhase);    // LaunchingVrChat
        sm.Fire(SessionTrigger.SlimeVrPhase);   // LaunchingSlimeVr
        sm.Fire(SessionTrigger.VrOverlayPhase); // LaunchingVrOverlay
        sm.Fire(SessionTrigger.ChainComplete);  // Complete
        Assert.Equal(SessionState.Complete, sm.State);
    }
}
