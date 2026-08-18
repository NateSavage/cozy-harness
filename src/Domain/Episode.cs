namespace CozyHarness.Domain;

public enum TickType {
    Pulse,
    Work,
    Intake,
    ReflectDaily,
    ReflectWeekly,
    Reply,
    Chore,
    Seam,
    Rollup
}

/// <summary>
/// Immutable ground truth. One file per episode; one git commit per tick. Nothing here is ever edited after the fact.
/// </summary>
public sealed record Episode {
    public required DateTimeOffset Timestamp { get; init; }
    public required TickType Type { get; init; }
    /// <summary>One line. Becomes the git commit message.</summary>
    public required string Summary { get; init; }
    public string? Body { get; init; }
    /// <summary>Explicit, and entirely fine. A tick that does nothing is a success.</summary>
    public bool DidNothing { get; init; }
    public string? GoalId { get; init; }
    /// <summary>The agent's own rating, 0-1. Used for retrieval weighting only.</summary>
    public double Salience { get; init; } = 0.5;
    public int TokensUsed { get; init; }
    /// <summary>Person slug if this episode involved someone.</summary>
    public string? Person { get; init; }
    public bool Sensitive { get; init; }

    public string RelativePath =>
        $"episodes/{Timestamp:yyyy}/{Timestamp:MM}/{Timestamp:dd-HHmm}-{Type.ToString().ToLowerInvariant()}.md";
}
