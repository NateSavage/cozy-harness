namespace CozyHarness.Domain;

public sealed record Belief {
    public required string Id { get; init; }
    public required string Topic { get; init; }
    public required string Statement { get; init; }
    public required double Confidence { get; init; }
    public required DateTimeOffset Created { get; init; }
    public IReadOnlyList<string> SupportEpisodes { get; init; } = Array.Empty<string>();
    /// <summary>Id of the belief that replaced this one. Superseded, never deleted.</summary>
    public string? SupersededBy { get; init; }
    /// <summary>
    /// Where this came from. A belief traceable to one persistent stranger looks
    /// different from one the agent worked out itself. Surfaced in weekly reflect.
    /// </summary>
    public string? SourcePerson { get; init; }

    public string RelativePath => $"beliefs/{Topic}.md";
}
