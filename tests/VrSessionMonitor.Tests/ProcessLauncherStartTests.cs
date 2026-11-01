using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>Regression from 2026-11-01: Process.Start with UseShellExecute=true never returned from
/// ShellExecuteEx while launching VRCFaceTracking. The launch lock for that name stayed held for the
/// rest of the run, so every later relaunch (including the face-tracking auto-heal's) silently never
/// happened. The process name each test uses is unique, so the shared static lock and the real
/// process-list checks can't collide with anything running on the machine.</summary>
public class ProcessLauncherStartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VrSmLauncherTests_" + Guid.NewGuid().ToString("N"));

    public ProcessLauncherStartTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FakeTarget(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "");
        return path;
    }

    private static string UniqueName() => "VrSmNoSuchProcess_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Launches_an_exe_directly_rather_than_through_the_shell()
    {
        var calls = new List<ProcessStartInfo>();
        var launcher = new ProcessLauncher(psi => { calls.Add(psi); return null; }, TimeSpan.FromSeconds(5));

        await launcher.EnsureRunningAsync(UniqueName(), FakeTarget("app.exe"), null, startupTimeoutMs: 20, pollIntervalMs: 10);

        var psi = Assert.Single(calls);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public async Task Falls_back_to_the_shell_when_the_exe_needs_elevation()
    {
        var calls = new List<ProcessStartInfo>();
        var launcher = new ProcessLauncher(psi =>
        {
            calls.Add(psi);
            if (!psi.UseShellExecute) throw new Win32Exception(740); // ERROR_ELEVATION_REQUIRED
            return null;
        }, TimeSpan.FromSeconds(5));

        await launcher.EnsureRunningAsync(UniqueName(), FakeTarget("admin.exe"), null, startupTimeoutMs: 20, pollIntervalMs: 10);

        Assert.Equal(2, calls.Count);
        Assert.True(calls[1].UseShellExecute);
    }

    [Fact]
    public async Task Uses_the_shell_for_targets_that_are_not_executables()
    {
        var calls = new List<ProcessStartInfo>();
        var launcher = new ProcessLauncher(psi => { calls.Add(psi); return null; }, TimeSpan.FromSeconds(5));

        await launcher.EnsureRunningAsync(UniqueName(), FakeTarget("app.lnk"), null, startupTimeoutMs: 20, pollIntervalMs: 10);

        Assert.True(Assert.Single(calls).UseShellExecute);
    }

    [Fact]
    public async Task Suppressing_uac_still_launches_directly_with_run_as_invoker()
    {
        var calls = new List<ProcessStartInfo>();
        var launcher = new ProcessLauncher(psi => { calls.Add(psi); return null; }, TimeSpan.FromSeconds(5));

        await launcher.EnsureRunningAsync(UniqueName(), FakeTarget("app.exe"), null, startupTimeoutMs: 20, pollIntervalMs: 10,
            suppressUacPrompt: true);

        var psi = Assert.Single(calls);
        Assert.False(psi.UseShellExecute);
        Assert.Equal("RunAsInvoker", psi.EnvironmentVariables["__COMPAT_LAYER"]);
    }

    [Fact]
    public async Task A_start_call_that_never_returns_does_not_hold_the_launch_lock()
    {
        var name = UniqueName();
        var target = FakeTarget("hangs.exe");
        using var never = new ManualResetEventSlim(false);
        var calls = 0;
        var launcher = new ProcessLauncher(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) never.Wait(); // the first launch hangs forever
            return null;
        }, TimeSpan.FromMilliseconds(100));

        try
        {
            // Task.Run so a start call that blocks synchronously can't hang the test itself.
            var first = Task.Run(() => launcher.EnsureRunningAsync(name, target, null, startupTimeoutMs: 20, pollIntervalMs: 10));
            Assert.Same(first, await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.False((await first).Success);

            // Reaching the start call again proves the lock was released; the old code made this
            // second call wait 30s for the lock and then give up without launching.
            await launcher.EnsureRunningAsync(name, target, null, startupTimeoutMs: 20, pollIntervalMs: 10);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            never.Set();
        }
    }
}
