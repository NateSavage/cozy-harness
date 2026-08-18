namespace CozyHarness.Domain;

/// <summary>State is aliveness: Active recurs on its own interval; Retired has stopped for good.</summary>
public enum ChoreState { Active, Retired }

/// <summary>
/// A small, bounded, recurring task — deliberately NOT a goal. It carries no
/// renewal, no kind, no claim on satisfying the useless-goal requirement; it
/// just comes due on its own interval and gets worked through. Keeping it
/// structurally separate from <see cref="Goal"/> is the point.
/// </summary>
public sealed record Chore {
    public required string Id { get; init; }          // e.g. "0003-check-mirror"
    public required string Title { get; init; }
    public required ChoreState State { get; init; }
    public required TimeSpan Interval { get; init; }
    public required DateTimeOffset Created { get; init; }
    /// <summary>What doing it actually means. Written once, by whoever authored the chore.</summary>
    public string? Description { get; init; }
    public DateTimeOffset? LastRun { get; init; }
    public DateTimeOffset? Closed { get; init; }
    /// <summary>Required to retire. Same rule as closing a goal: never let it go without a reason.</summary>
    public string? ClosedWhy { get; init; }

    public static string DirectoryFor(ChoreState s) => s switch {
        ChoreState.Active  => "chores/active",
        ChoreState.Retired => "chores/retired",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public string RelativePath => $"{DirectoryFor(State)}/{Id}.md";

    /// <summary>Next time this is due. Recomputed from whichever is more recent: last run, or creation.</summary>
    public DateTimeOffset DueBy => (LastRun ?? Created) + Interval;

    public bool IsDue(DateTimeOffset now) => State == ChoreState.Active && DueBy <= now;
}
