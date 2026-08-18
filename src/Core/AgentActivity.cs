using CozyHarness.Domain;

namespace CozyHarness.Core;

/// <summary>
/// What the agent is doing right now, if anything — shared between the
/// scheduler (which sets it) and anything that wants to show or interrupt it
/// (a channel). Only heavy ticks (work/intake/reflect/chore) show up here; the
/// pulse and replies are fast enough, and run without the heavy lock, so
/// there's nothing meaningful to report or interrupt.
///
/// Thread-safe: `Begin`/`SetDetail`/`End` are called from the scheduler loop,
/// `TryInterrupt` from wherever a channel reacts to the operator (Discord's
/// gateway thread, a button click, a console line).
/// </summary>
public sealed class AgentActivity {
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public TickType? CurrentTick { get; private set; }
    public string? Detail { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    /// <summary>Worth being left alone for — reflected as Do Not Disturb rather than the usual online/away. See MarkImportant.</summary>
    public bool Important { get; private set; }

    /// <summary>Fired after Begin/SetDetail/End, off any lock, so a channel can refresh a status display.</summary>
    public event Action? Changed;

    /// <summary>
    /// Called by the scheduler when a heavy tick starts. Returns a token source
    /// linked to <paramref name="outer"/> — cancel it via <see cref="TryInterrupt"/>
    /// without touching the app's own shutdown token.
    /// </summary>
    internal CancellationTokenSource Begin(TickType type, CancellationToken outer) {
        CancellationTokenSource cts;
        lock (_lock) {
            _cts = cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            CurrentTick = type;
            Detail = null;
            Important = false;
            StartedAt = DateTimeOffset.UtcNow;
        }
        Changed?.Invoke();
        return cts;
    }

    /// <summary>Called by a tick, once it knows what it's actually doing, to sharpen the summary.</summary>
    public void SetDetail(string detail) {
        lock (_lock) {
            if (CurrentTick is null) return;
            Detail = detail;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Called by a tick that's worth being left alone for. Currently: any
    /// WorkTick, unconditionally — a chore explicitly isn't this (see
    /// Seeds.ChoreSystem: "nothing here that needs to matter"), and goals are
    /// the agent's own self-directed pursuits in a way chores aren't. Reset
    /// automatically at End(), same as Detail.
    /// </summary>
    public void MarkImportant() {
        lock (_lock) {
            if (CurrentTick is null) return;
            Important = true;
        }
        Changed?.Invoke();
    }

    internal void End() {
        lock (_lock) {
            _cts?.Dispose();
            _cts = null;
            CurrentTick = null;
            Detail = null;
            Important = false;
            StartedAt = null;
        }
        Changed?.Invoke();
    }

    /// <summary>True and cancels the in-flight heavy tick, if one is running; false if there's nothing to interrupt.</summary>
    public bool TryInterrupt() {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _cts; }
        if (cts is null) return false;
        try { cts.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }   // finished between the read above and here
    }

    /// <summary>Plain-language description for a human. "nothing — idle right now" when there's nothing running.</summary>
    public string Summary() {
        TickType? type;
        string? detail;
        DateTimeOffset? started;
        lock (_lock) { type = CurrentTick; detail = Detail; started = StartedAt; }

        if (type is null) return "nothing — idle right now";

        var what = detail ?? type switch {
            TickType.Work          => "working on a goal",
            TickType.Intake        => "reading the world",
            TickType.ReflectDaily  => "reflecting on the day",
            TickType.ReflectWeekly => "reflecting on the week",
            TickType.Chore         => "working through a chore",
            _                       => type.ToString()!.ToLowerInvariant(),
        };

        var mins = started is { } s ? (int)(DateTimeOffset.UtcNow - s).TotalMinutes : 0;
        var since = mins < 1 ? "just started" : $"started {mins} min ago";
        return $"{what} ({since})";
    }
}
