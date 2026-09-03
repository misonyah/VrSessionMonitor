using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// A suspended process keeps every lock and handle it held, so anything waiting on one blocks until
/// it resumes. Freezing the wrong process therefore does not slow the machine — it hangs whatever
/// was waiting, up to and including the shell. This list is the only thing preventing that.
/// </summary>
public class SuspendSafetyTests
{
    private const int Me = 1234;

    [Fact]
    public void An_ordinary_application_may_be_suspended()
    {
        Assert.True(SuspendSafety.MaySuspend("Code", 5000, Me));
        Assert.True(SuspendSafety.MaySuspend("Unity", 5001, Me));
        Assert.True(SuspendSafety.MaySuspend("chrome", 5002, Me));
    }

    [Theory]
    [InlineData("vrserver")]
    [InlineData("vrcompositor")]
    [InlineData("VRChat")]
    [InlineData("VirtualDesktop.Streamer")]
    [InlineData("SlimeVR")]
    [InlineData("sr_runtime")]
    [InlineData("VRCFaceTracking")]
    public void The_vr_chain_is_never_suspended(string name)
    {
        // Freezing the session you are trying to help is self-defeating, and freezing the
        // compositor drops the headset.
        Assert.False(SuspendSafety.MaySuspend(name, 5000, Me));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("dwm")]
    [InlineData("csrss")]
    [InlineData("audiodg")]
    [InlineData("svchost")]
    [InlineData("lsass")]
    public void System_and_shell_processes_are_never_suspended(string name)
    {
        Assert.False(SuspendSafety.MaySuspend(name, 5000, Me));
    }

    [Fact]
    public void This_process_is_never_suspended()
    {
        // Suspending ourselves leaves nothing running that could resume anything else — every
        // frozen process would stay frozen until a reboot.
        Assert.False(SuspendSafety.MaySuspend("Code", Me, Me));
        Assert.Contains("resume", SuspendSafety.RefusalReason("Code", Me, Me)!);
    }

    [Fact]
    public void Another_instance_of_this_app_is_never_suspended()
    {
        Assert.False(SuspendSafety.MaySuspend("VrSessionMonitor", 9999, Me));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void The_idle_and_system_pids_are_never_suspended(int pid)
    {
        Assert.False(SuspendSafety.MaySuspend("whatever", pid, Me));
    }

    [Fact]
    public void Matching_ignores_case()
    {
        // Process names arrive in whatever case the OS reports, so a case-sensitive list would let
        // "EXPLORER" through.
        Assert.False(SuspendSafety.MaySuspend("EXPLORER", 5000, Me));
        Assert.False(SuspendSafety.MaySuspend("vrchat", 5000, Me));
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        Assert.False(SuspendSafety.MaySuspend("", 5000, Me));
    }

    [Fact]
    public void A_refusal_always_explains_itself()
    {
        // The reason is logged and shown; an empty one would leave the user unable to tell whether
        // it refused or silently failed.
        foreach (var name in new[] { "explorer", "vrserver", "VrSessionMonitor" })
            Assert.False(string.IsNullOrWhiteSpace(SuspendSafety.RefusalReason(name, 5000, Me)));
    }
}
