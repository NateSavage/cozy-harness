namespace CozyHarness.Domain;

/// <summary>State is the directory the file lives in. `mv` is the transition; git records the rename.</summary>
public enum GoalState {
    Proposed, 
    Active, 
    Dormant, 
    Abandoned, 
    Done
}

public enum GoalKind {
    /// <summary>Tracked across time, where the delta is the point.</summary>
    Longitudinal,
    /// <summary>Something it is currently bad at, with failures on the record.</summary>
    Craft,
    /// <summary>Attended to for no instrumental reason whatsoever. At least one must always be alive.</summary>
    Useless,
    RelationalOperator,
    RelationalOther,
}

public sealed record Goal {
    public required string Id { get; init; }          // e.g. "0042-tern-counts"
    public required string Title { get; init; }
    public required GoalState State { get; init; }
    public required GoalKind Kind { get; init; }
    public required DateTimeOffset Created { get; init; }
    public string? Description { get; init; }
    /// <summary>Episode path that spawned this goal.</summary>
    public string? OriginEpisode { get; init; }
    /// <summary>Decays to Dormant unless deliberately reaffirmed. Persistence requires an act, not inertia.</summary>
    public DateTimeOffset? RenewBy { get; init; }
    public DateTimeOffset? LastTouched { get; init; }
    public DateTimeOffset? Closed { get; init; }
    /// <summary>Required for Abandoned and Done. The most human text in the tree.</summary>
    public string? ClosedWhy { get; init; }

    public static string DirectoryFor(GoalState s) => s switch {
        GoalState.Proposed  => "goals/proposed",
        GoalState.Active    => "goals/active",
        GoalState.Dormant   => "goals/dormant",
        GoalState.Abandoned => "goals/abandoned",
        GoalState.Done      => "goals/done",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public string RelativePath => $"{DirectoryFor(State)}/{Id}.md";
}
