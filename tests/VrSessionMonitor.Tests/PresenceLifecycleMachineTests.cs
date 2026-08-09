using System;
using System.Threading.Tasks;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class PresenceLifecycleMachineTests
{
    private sealed class Harness
    {
        public bool Present;
        public bool Running;
        public int EnsureCalls;
        public int ShutdownCalls;
        public int RunningTicks;
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public PresenceLifecycleMachine Machine = null!;

        public PresenceLifecycleMachine Build(int shutdownDelayMs = 30000)
        {
            Machine = new PresenceLifecycleMachine(
                presenceSignal: () => Present,
                isRunning: () => Running,
                ensureRunning: () => { EnsureCalls++; Running = true; return Task.CompletedTask; },
                shutdown: () => { ShutdownCalls++; Running = false; },
                shutdownDelayMs: shutdownDelayMs,
                clock: () => Now,
                runningTick: () => { RunningTicks++; return Task.CompletedTask; });
            return Machine;
        }
    }

    [Fact]
    public async Task Presence_gained_launches_and_enters_running()
    {
        var h = new Harness(); h.Build();
        h.Present = true;
        await h.Machine.TickAsync();
        Assert.Equal(PresenceState.Running, h.Machine.State);
        Assert.Equal(1, h.EnsureCalls);
        Assert.True(h.Running);
    }

    [Fact]
    public async Task Running_crash_recovers_when_process_dies()
    {
        var h = new Harness(); h.Build();
        h.Present = true;
        await h.Machine.TickAsync();     // -> Running, launched
        h.Running = false;               // process died while still present
        await h.Machine.TickAsync();     // should relaunch, stay Running
        Assert.Equal(PresenceState.Running, h.Machine.State);
        Assert.Equal(2, h.EnsureCalls);
    }

    [Fact]
    public async Task Running_tick_runs_only_when_present_and_alive()
    {
        var h = new Harness(); h.Build();
        h.Present = true;
        await h.Machine.TickAsync();     // launch
        await h.Machine.TickAsync();     // alive+present -> runningTick
        Assert.Equal(1, h.RunningTicks);
    }

    [Fact]
    public async Task Presence_lost_starts_countdown_then_kills_after_delay()
    {
        var h = new Harness(); h.Build(shutdownDelayMs: 30000);
        h.Present = true;
        await h.Machine.TickAsync();     // Running
        h.Present = false;
        await h.Machine.TickAsync();     // -> ShuttingDown, countdown starts at Now
        Assert.Equal(PresenceState.ShuttingDown, h.Machine.State);
        Assert.Equal(0, h.ShutdownCalls);

        h.Now = h.Now.AddSeconds(29);
        await h.Machine.TickAsync();     // not yet
        Assert.Equal(0, h.ShutdownCalls);

        h.Now = h.Now.AddSeconds(2);     // total 31s >= 30s
        await h.Machine.TickAsync();     // kill
        Assert.Equal(1, h.ShutdownCalls);
        Assert.Equal(PresenceState.Idle, h.Machine.State);
    }

    [Fact]
    public async Task Presence_regained_mid_countdown_cancels_shutdown()
    {
        var h = new Harness(); h.Build(shutdownDelayMs: 30000);
        h.Present = true;
        await h.Machine.TickAsync();     // Running
        h.Present = false;
        await h.Machine.TickAsync();     // ShuttingDown
        h.Now = h.Now.AddSeconds(10);
        h.Present = true;                // came back
        await h.Machine.TickAsync();     // -> Running, no kill
        Assert.Equal(PresenceState.Running, h.Machine.State);
        Assert.Equal(0, h.ShutdownCalls);
    }

    [Fact]
    public async Task Leftover_process_at_idle_startup_is_shut_down()
    {
        var h = new Harness(); h.Build(shutdownDelayMs: 30000);
        h.Present = false;
        h.Running = true;                // process already up at startup, no presence
        await h.Machine.TickAsync();     // Idle sees leftover -> ShuttingDown
        Assert.Equal(PresenceState.ShuttingDown, h.Machine.State);
        h.Now = h.Now.AddSeconds(31);
        await h.Machine.TickAsync();     // kill
        Assert.Equal(1, h.ShutdownCalls);
        Assert.Equal(PresenceState.Idle, h.Machine.State);
    }

    [Fact]
    public async Task Zero_delay_kills_on_next_tick()
    {
        var h = new Harness(); h.Build(shutdownDelayMs: 0);
        h.Present = true;
        await h.Machine.TickAsync();     // Running
        h.Present = false;
        await h.Machine.TickAsync();     // ShuttingDown, elapsed 0 >= 0 -> kill same tick
        Assert.Equal(1, h.ShutdownCalls);
        Assert.Equal(PresenceState.Idle, h.Machine.State);
    }

    [Fact]
    public async Task Shutdown_countdown_remaining_reports_time_left()
    {
        var h = new Harness(); h.Build(shutdownDelayMs: 30000);
        h.Present = true; await h.Machine.TickAsync();
        h.Present = false; await h.Machine.TickAsync(); // countdown starts
        h.Now = h.Now.AddSeconds(10);
        var remaining = h.Machine.ShutdownCountdownRemaining();
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, 19.5, 20.5);
    }

    [Fact]
    public async Task Process_gone_on_its_own_during_countdown_returns_to_idle_without_kill()
    {
        var h = new Harness(); h.Build(shutdownDelayMs: 30000);
        h.Present = true; await h.Machine.TickAsync(); // Running
        h.Present = false; await h.Machine.TickAsync(); // ShuttingDown
        h.Running = false;                              // exited on its own
        await h.Machine.TickAsync();
        Assert.Equal(PresenceState.Idle, h.Machine.State);
        Assert.Equal(0, h.ShutdownCalls);
    }
}
