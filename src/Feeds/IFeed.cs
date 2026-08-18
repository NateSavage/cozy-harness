namespace CozyHarness.Feeds;

public readonly record struct FeedItem(string? Reference, string Content);

public interface IFeed {
    string Name { get; }
    bool ShouldPoll(DateTimeOffset now);
    Task<IReadOnlyList<FeedItem>> PollAsync(CancellationToken ct);
}
