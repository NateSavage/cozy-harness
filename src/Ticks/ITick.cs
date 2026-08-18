using CozyHarness.Domain;

namespace CozyHarness.Ticks;

public interface ITick {
    TickType Type { get; }
    Task<TickOutcome> RunAsync(CancellationToken ct);
}

/// <summary>
/// A tick that did nothing is a success. There is no failure state here for
/// "produced no work" — only for "crashed", which is separate.
/// </summary>
public sealed record TickOutcome {
    public required string Summary { get; init; }
    public string? Body { get; init; }
    public bool DidNothing { get; init; }
    public string? GoalId { get; init; }
    public string? Person { get; init; }
    public bool Sensitive { get; init; }
    public double Salience { get; init; } = 0.5;
    public int TokensUsed { get; init; }
    /// <summary>Set when the pulse decides to wake something.</summary>
    public TickType? Wake { get; init; }
    /// <summary>Suppresses the episode write entirely — used by pulses that answer "nothing".</summary>
    public bool Silent { get; init; }

    public static TickOutcome Nothing(string why = "nothing needed attention") =>
        new() { Summary = why, DidNothing = true, Silent = true, Salience = 0.1 };
}
