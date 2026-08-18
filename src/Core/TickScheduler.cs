using CozyHarness.Channels;
using CozyHarness.Config;
using CozyHarness.Domain;
using CozyHarness.Storage;
using CozyHarness.Ticks;

namespace CozyHarness.Core;

/// <summary>
/// The heartbeat. Three properties matter more than the intervals:
///
///   BACKPRESSURE — if the previous heavy tick hasn't finished, the pulse skips
///   rather than queues. On CPU a queue becomes a spiral.
///
///   JITTER — the pulse interval varies, so ticks don't lockstep with cron
///   artifacts or with each other.
///
///   QUIET HOURS — overnight the pulse slows and work ticks don't fire. Partly
///   thermal, mostly because a day with a shape is more legible than a uniform grind.
/// </summary>
public sealed class TickScheduler {
    private readonly AgentClock _clock;
    private readonly ScheduleConfig _cfg;
    private readonly ChoreConfig _choreCfg;
    private readonly IndexDb _db;
    private readonly TickRunner _runner;
    private readonly Func<TickType, ITick> _factory;
    private readonly IOperatorChannel _channel;
    private readonly Random _rng = new();

    private readonly AgentActivity _activity;
    private readonly SemaphoreSlim _heavyLock = new(1, 1);
    private DateOnly _lastDailyReflect = DateOnly.MinValue;
    private DateOnly _lastWeeklyReflect = DateOnly.MinValue;
    private DateTimeOffset _lastIntake = DateTimeOffset.MinValue;
    private bool? _lastQuietHours;   // null so the very first cycle always syncs presence, whichever way it starts

    private readonly ErrorReporter _errors;

    public TickScheduler(AgentClock clock, ScheduleConfig cfg, ChoreConfig choreCfg, IndexDb db,
                          TickRunner runner, Func<TickType, ITick> factory, IOperatorChannel channel,
                          AgentActivity activity, ErrorReporter errors) {
        _clock = clock; _cfg = cfg; _choreCfg = choreCfg; _db = db; _runner = runner; _factory = factory;
        _channel = channel; _activity = activity; _errors = errors;
    }

    public async Task RunAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await UpdatePresenceAsync(ct);
                await OneCycleAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) {
                // Anything that escapes a full cycle is infrastructure, not tick
                // logic — TickRunner already reports and swallows tick failures
                // on its own, so this is a different, narrower class of thing:
                // episode writes, git, the index, or a bug in the scheduling
                // logic itself.
                Console.Error.WriteLine($"[scheduler] {ex.Message}");
                _errors.Report("a scheduler cycle failed", ex);
            }

            // Shutdown lands here far more often than mid-cycle — this is most
            // of the loop's time. Without its own catch, cancelling here would
            // throw straight out of RunAsync uncaught, and Program.cs's cleanup
            // after `await scheduler.RunAsync(...)` (closing the Discord
            // connection) would never run.
            try { await Task.Delay(NextInterval(), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Presence follows the clock (see AgentClock.IsQuietHours), not
    /// AgentActivity — being between ticks isn't the same thing as it being
    /// quiet hours. Only calls out to the channel on an actual transition,
    /// not every cycle. Its own try/catch, separate from RunAsync's, so a
    /// presence hiccup here is never mistaken for a real scheduler-cycle
    /// failure.
    /// </summary>
    private async Task UpdatePresenceAsync(CancellationToken ct) {
        var quiet = _clock.IsQuietHours();
        if (_lastQuietHours == quiet) return;
        _lastQuietHours = quiet;
        try { await _channel.SetAwayAsync(quiet, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort; a presence hiccup shouldn't take the scheduler down */ }
    }

    private async Task OneCycleAsync(CancellationToken ct) {
        // Scheduled ticks take precedence over anything the pulse might decide.
        var today = DateOnly.FromDateTime(_clock.OperatorNow.Date);

        if (_clock.IsWeeklyReflectHour() && _lastWeeklyReflect != today) {
            _lastWeeklyReflect = today;
            await RunHeavy(TickType.ReflectWeekly, ct);
            return;
        }

        if (_clock.IsDailyReflectHour() && _lastDailyReflect != today) {
            _lastDailyReflect = today;
            await RunHeavy(TickType.ReflectDaily, ct);
            return;
        }

        if (_clock.IsIntakeHour() && (_clock.UtcNow - _lastIntake).TotalHours > 6) {
            _lastIntake = _clock.UtcNow;
            await RunHeavy(TickType.Intake, ct);
            return;
        }

        // Routine, not urgent: a due chore never preempts reflect or intake above,
        // but there's nothing for the pulse to judge once something is simply due
        // — so, unlike work, this bypasses the pulse's wake decision entirely.
        // Capped per day so a long chore list can't crowd out everything else.
        if (_db.ChoreTicksToday() < _choreCfg.MaxChoresPerDay && _db.DueChores(_clock.UtcNow).Count > 0) {
            await RunHeavy(TickType.Chore, ct);
            return;
        }

        // Backpressure: a heavy tick is still running, so don't even pulse.
        if (_heavyLock.CurrentCount == 0) return;

        var pulse = await _runner.RunAsync(_factory(TickType.Pulse), ct);
        if (pulse.Wake is not { } wake) return;

        // Quiet hours: the pulse may still notice things, but work waits for morning.
        if (_clock.IsQuietHours() && wake == TickType.Work) return;

        await RunHeavy(wake, ct);
    }

    /// <summary>The fast path. Interrupts the pulse cycle so real conversation is possible.</summary>
    public async Task HandleInboundAsync(CancellationToken ct) {
        // Deliberately does NOT take the heavy lock: a reply should never wait
        // behind a work tick, and it isn't a work tick itself.
        await _runner.RunAsync(_factory(TickType.Reply), ct);
    }

    private async Task RunHeavy(TickType type, CancellationToken ct) {
        if (!await _heavyLock.WaitAsync(0, ct)) return;

        // A token scoped to just this one run: cancelling it (an operator
        // interrupt) must never look like the app's own shutdown token firing,
        // which is exactly what sharing `ct` directly would do — see
        // TickRunner.RunAsync for the other half of this.
        using var tickCts = _activity.Begin(type, ct);
        try { await _runner.RunAsync(_factory(type), tickCts.Token); }
        finally { _activity.End(); _heavyLock.Release(); }
    }

    private TimeSpan NextInterval() {
        int baseSeconds = _clock.IsQuietHours() ? _cfg.QuietPulseIntervalSeconds : _cfg.PulseIntervalSeconds;

        double jitter = 1.0 + (_rng.NextDouble() * 2 - 1) * _cfg.PulseJitter;
        return TimeSpan.FromSeconds(baseSeconds * jitter);
    }
}
