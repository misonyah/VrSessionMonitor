using System;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// VRChat raises its AFK parameter whenever it loses focus, which includes opening the SteamVR
/// dashboard. These pin the asymmetry that makes the hold usable: going AFK waits, coming back
/// never does.
/// </summary>
public class AfkHoldGateTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(30);

    private sealed class FakeClock
    {
        private DateTime _now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    [Fact]
    public void A_brief_steam_menu_visit_never_counts_as_afk()
    {
        // The whole point: signal goes up, comes back down inside the hold, nothing happens.
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        Assert.False(gate.Signal(true));   // no immediate flip
        clock.Advance(12);                 // the median AFK period in the real logs
        Assert.False(gate.Tick());
        Assert.False(gate.Signal(false));  // cleared before the hold elapsed
        Assert.False(gate.IsAfk);
        Assert.False(gate.IsPending);
    }

    [Fact]
    public void Real_afk_still_registers_once_the_hold_elapses()
    {
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        gate.Signal(true);
        Assert.True(gate.IsPending);

        clock.Advance(29);
        Assert.False(gate.Tick());
        Assert.False(gate.IsAfk);

        clock.Advance(1);
        Assert.True(gate.Tick());
        Assert.True(gate.IsAfk);
        Assert.False(gate.IsPending); // resolved, so the caller can stop ticking
    }

    [Fact]
    public void Coming_back_is_immediate_and_never_held()
    {
        // A delayed clear would leave the lights wrong for the length of the hold.
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        gate.Signal(true);
        clock.Advance(31);
        gate.Tick();
        Assert.True(gate.IsAfk);

        Assert.True(gate.Signal(false));
        Assert.False(gate.IsAfk);
    }

    [Fact]
    public void A_repeated_signal_does_not_restart_the_hold()
    {
        // Otherwise a chatty source could keep the hold alive forever and AFK would never fire.
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        gate.Signal(true);
        clock.Advance(20);
        gate.Signal(true);
        clock.Advance(11); // 31s since the FIRST signal
        Assert.True(gate.Tick());
        Assert.True(gate.IsAfk);
    }

    [Fact]
    public void Menu_visit_then_real_afk_still_works()
    {
        // A cancelled hold must not poison the next one.
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        gate.Signal(true);
        clock.Advance(5);
        gate.Signal(false);      // menu closed

        gate.Signal(true);       // now genuinely away
        clock.Advance(31);
        Assert.True(gate.Tick());
        Assert.True(gate.IsAfk);
    }

    [Fact]
    public void Tick_is_idempotent_once_afk_is_believed()
    {
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        gate.Signal(true);
        clock.Advance(31);
        Assert.True(gate.Tick());
        Assert.False(gate.Tick()); // no second transition to react to
        Assert.False(gate.Tick());
    }

    [Fact]
    public void Zero_hold_restores_the_previous_immediate_behaviour()
    {
        var clock = new FakeClock();
        var gate = new AfkHoldGate(TimeSpan.Zero, clock.Now);

        Assert.True(gate.Signal(true));
        Assert.True(gate.IsAfk);
        Assert.True(gate.Signal(false));
        Assert.False(gate.IsAfk);
    }

    [Fact]
    public void Reset_drops_a_pending_hold_so_it_cannot_fire_into_the_next_session()
    {
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        gate.Signal(true);
        gate.Reset();

        clock.Advance(600);
        Assert.False(gate.Tick());
        Assert.False(gate.IsAfk);
    }

    [Fact]
    public void Signal_reports_change_only_on_real_transitions()
    {
        // The caller drives light changes off this return value, so a spurious true would repaint
        // the lights for nothing.
        var clock = new FakeClock();
        var gate = new AfkHoldGate(Hold, clock.Now);

        Assert.False(gate.Signal(false)); // already not AFK
        Assert.False(gate.Signal(true));  // starts a hold, not a change
        clock.Advance(31);
        Assert.True(gate.Tick());
        Assert.False(gate.Signal(true));  // already AFK
    }
}
