namespace CozyHarness.Domain;

/// <summary>
/// Privacy is a property of provenance, not content. Where a fact came from is
/// mechanical to record; whether it is sensitive is not. The disclosure rule
/// derives from the class.
/// </summary>
public enum Provenance {
    /// <summary>Their posts, public repos, things said openly. Travels freely.</summary>
    Public,
    /// <summary>Told to the agent directly. Travels only with their consent.</summary>
    Learned,
    /// <summary>
    /// The agent's own impressions and inferences. Travels at its discretion —
    /// EXCEPT that inferences drawn from Learned input inherit the restriction.
    /// </summary>
    SelfObserved,
}

public sealed record Person {
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public bool IsOperator { get; init; }
    public DateTimeOffset FirstMet { get; init; }

    public string Directory => $"people/{Slug}";
    public string PathFor(Provenance p) => p switch {
        Provenance.Public       => $"{Directory}/profile.md",
        Provenance.Learned      => $"{Directory}/learned.md",
        Provenance.SelfObserved => $"{Directory}/observed.md",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };
}

/// <summary>A single recorded fact about a person, with its provenance chain.</summary>
public sealed record PersonFact {
    public required string PersonSlug { get; init; }
    public required Provenance Source { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Recorded { get; init; }
    /// <summary>
    /// True when this is a SelfObserved inference derived from Learned input.
    /// Inherits the non-disclosure restriction. This is the only part of the
    /// privacy model that requires real reasoning by the model.
    /// </summary>
    public bool DerivedFromPrivate { get; init; }
    public bool Sensitive { get; init; }

    public bool MayTravelTo(string otherPersonSlug) {
        if (otherPersonSlug == PersonSlug) return true;       // telling them their own thing
        return Source switch {
            Provenance.Public       => true,
            Provenance.Learned      => false,
            Provenance.SelfObserved => !DerivedFromPrivate,
            _ => false,
        };
    }
}
