using Stateless;

namespace VrSessionMonitor.Modules;

public enum PresenceState { Idle, Running, ShuttingDown }

/// <summary>
/// Shared 3-state skeleton for the "launch when a presence signal is true, shut down after a
/// sustained absence" pattern used by both VirtualHereSRanipalLifecycleManager and
/// VrcFaceTrackingLifecycleManager. Replaces each manager's hand-rolled bool + DateTime? idle
/// timer with a Stateless graph, so an unexpected transition throws instead of silently no-oping.
///
/// States:
///   Idle         - presence false and nothing running (rest). A leftover process detected here
///                  (e.g. at startup) routes to ShuttingDown so it still gets cleaned up.
///   Running      - presence true: ensure the process is up (crash-recover if it died) and run
///                  the optional runningTick (e.g. VRCFaceTracking's max-uptime check).
///   ShuttingDown - presence gone but a process is still up: count down shutdownDelayMs, then kill.
///
/// Driven by TickAsync() called on the manager's existing poll loop. The idle countdown is a
/// wall-clock comparison against an injected clock so tests are deterministic.
/// </summary>
public sealed class PresenceLifecycleMachine
{
    private enum Trigger { Gained, Lost, LeftoverDetected, Settled }

    private readonly StateMachine<PresenceState, Trigger> _sm;
    private readonly Func<bool> _presenceSignal;
    private readonly Func<bool> _isRunning;
    private readonly Func<Task> _ensureRunning;
    private readonly Action _shutdown;
    private readonly Action? _runningTick;
    private readonly int _shutdownDelayMs;
    private readonly Func<DateTime> _clock;

    private DateTime? _idleSince;

    public PresenceState State => _sm.State;

    public PresenceLifecycleMachine(
        Func<bool> presenceSignal,
        Func<bool> isRunning,
        Func<Task> ensureRunning,
        Action shutdown,
        int shutdownDelayMs,
        Func<DateTime>? clock = null,
        Action? runningTick = null)
    {
        _presenceSignal = presenceSignal;
        _isRunning = isRunning;
        _ensureRunning = ensureRunning;
        _shutdown = shutdown;
        _runningTick = runningTick;
        _shutdownDelayMs = shutdownDelayMs;
        _clock = clock ?? (() => DateTime.UtcNow);

        _sm = new StateMachine<PresenceState, Trigger>(PresenceState.Idle);

        _sm.Configure(PresenceState.Idle)
            .OnEntry(() => _idleSince = null)
            .Permit(Trigger.Gained, PresenceState.Running)
            .Permit(Trigger.LeftoverDetected, PresenceState.ShuttingDown)
            .Ignore(Trigger.Settled)
            .Ignore(Trigger.Lost);

        _sm.Configure(PresenceState.Running)
            .OnEntryAsync(async () => { _idleSince = null; await _ensureRunning().ConfigureAwait(false); })
            .Permit(Trigger.Lost, PresenceState.ShuttingDown)
            .Ignore(Trigger.Gained);

        _sm.Configure(PresenceState.ShuttingDown)
            .OnEntry(() => _idleSince = _clock())
            .Permit(Trigger.Gained, PresenceState.Running)
            .Permit(Trigger.Settled, PresenceState.Idle)
            .Ignore(Trigger.Lost);
    }

    public async Task TickAsync()
    {
        var present = _presenceSignal();

        switch (_sm.State)
        {
            case PresenceState.Idle:
                if (present)
                {
                    await _sm.FireAsync(Trigger.Gained).ConfigureAwait(false);
                }
                else if (_isRunning())
                {
                    // Freshly entered ShuttingDown; re-evaluate immediately so a zero (or
                    // already-elapsed) shutdownDelayMs kills within this same tick rather than
                    // waiting for the next poll.
                    await _sm.FireAsync(Trigger.LeftoverDetected).ConfigureAwait(false);
                    await EvaluateShuttingDownAsync(present).ConfigureAwait(false);
                }
                break;

            case PresenceState.Running:
                if (!present)
                {
                    // Same same-tick re-evaluation as above: entering ShuttingDown here must not
                    // wait a full extra tick before the zero-delay case can kill.
                    await _sm.FireAsync(Trigger.Lost).ConfigureAwait(false);
                    await EvaluateShuttingDownAsync(present).ConfigureAwait(false);
                }
                else if (!_isRunning())
                {
                    await _ensureRunning().ConfigureAwait(false); // crash recovery, stay Running
                }
                else
                {
                    _runningTick?.Invoke();
                }
                break;

            case PresenceState.ShuttingDown:
                await EvaluateShuttingDownAsync(present).ConfigureAwait(false);
                break;
        }
    }

    private async Task EvaluateShuttingDownAsync(bool present)
    {
        if (present)
        {
            await _sm.FireAsync(Trigger.Gained).ConfigureAwait(false);
        }
        else if (!_isRunning())
        {
            await _sm.FireAsync(Trigger.Settled).ConfigureAwait(false); // exited on its own
        }
        else if (_idleSince is DateTime since &&
                 (_clock() - since).TotalMilliseconds >= _shutdownDelayMs)
        {
            _shutdown();
            await _sm.FireAsync(Trigger.Settled).ConfigureAwait(false);
        }
    }

    /// <summary>Time left before the idle shutdown fires, or null when not counting down.</summary>
    public TimeSpan? ShutdownCountdownRemaining()
    {
        if (_sm.State != PresenceState.ShuttingDown || _idleSince is not DateTime since)
            return null;

        var remaining = TimeSpan.FromMilliseconds(_shutdownDelayMs) - (_clock() - since);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
