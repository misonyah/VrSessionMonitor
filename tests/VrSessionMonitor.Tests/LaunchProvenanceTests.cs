using VrSessionMonitor.Modules;
using VrSessionMonitor.Tests.Fakes;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>"Started manually" is decided by PID identity, not by process name: an app whose
/// process is running under a PID we never launched was started by someone else. This is what lets
/// the monitor apply window rules to a hand-started VRChat and still label it honestly.</summary>
public class LaunchProvenanceTests
{
    [Fact]
    public void Not_running_when_there_is_no_pid()
    {
        var p = new LaunchProvenance();

        Assert.Equal(AppStartOrigin.NotRunning, p.Classify("VRChat", null));
    }

    [Fact]
    public void Unknown_pid_is_manual()
    {
        var p = new LaunchProvenance();

        Assert.Equal(AppStartOrigin.Manual, p.Classify("VRChat", 1234));
    }

    [Fact]
    public void Pid_we_launched_is_managed()
    {
        var p = new LaunchProvenance();
        p.RecordLaunched("VRChat", 1234);

        Assert.Equal(AppStartOrigin.Managed, p.Classify("VRChat", 1234));
    }

    /// <summary>The app was restarted by hand after we launched it — same name, different PID —
    /// so it must read as manual, not inherit the old verdict.</summary>
    [Fact]
    public void Different_pid_under_the_same_name_is_manual()
    {
        var p = new LaunchProvenance();
        p.RecordLaunched("VRChat", 1234);

        Assert.Equal(AppStartOrigin.Manual, p.Classify("VRChat", 5678));
    }

    [Fact]
    public void Processes_are_tracked_independently()
    {
        var p = new LaunchProvenance();
        p.RecordLaunched("VRChat", 1234);

        Assert.Equal(AppStartOrigin.Manual, p.Classify("VRCOSC", 1234));
    }

    [Fact]
    public void Forget_clears_the_record()
    {
        var p = new LaunchProvenance();
        p.RecordLaunched("VRChat", 1234);

        p.Forget("VRChat");

        Assert.Equal(AppStartOrigin.Manual, p.Classify("VRChat", 1234));
    }

    [Fact]
    public void Relaunch_replaces_the_recorded_pid()
    {
        var p = new LaunchProvenance();
        p.RecordLaunched("VRChat", 1234);
        p.RecordLaunched("VRChat", 5678);

        Assert.Equal(AppStartOrigin.Managed, p.Classify("VRChat", 5678));
        Assert.Equal(AppStartOrigin.Manual, p.Classify("VRChat", 1234));
    }

    [Fact]
    public void Process_names_are_case_insensitive()
    {
        var p = new LaunchProvenance();
        p.RecordLaunched("VRChat", 1234);

        Assert.Equal(AppStartOrigin.Managed, p.Classify("vrchat", 1234));
    }

    /// <summary>Regression test for FakeProcessLauncher reproducing the real launcher's Trap 2:
    /// the "already running, skipping launch" path must never record provenance, since that
    /// process was started by someone else. Without this guard, EnsureRunningAsync against a
    /// pre-seeded "already running" process would misclassify it as Managed instead of Manual —
    /// which is the exact scenario Task 6's consumer tests need to exercise.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Fake_launcher_does_not_record_provenance_for_an_already_running_process()
    {
        var fake = new FakeProcessLauncher();
        fake.Running.Add("VRChat");
        fake.ProcessIds["VRChat"] = 9999;

        await fake.EnsureRunningAsync("VRChat", "C:\\fake\\VRChat.exe", null, 1000, 100);

        Assert.Equal(AppStartOrigin.Manual, fake.Provenance.Classify("VRChat", 9999));
    }
}
