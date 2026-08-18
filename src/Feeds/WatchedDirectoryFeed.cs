namespace CozyHarness.Feeds;

/// <summary>A folder the operator drops things into. The simplest possible world.</summary>
public sealed class WatchedDirectoryFeed : IFeed {
    private readonly string _dir;
    private readonly HashSet<string> _seen = new();

    public string Name => "dropbox";
    public WatchedDirectoryFeed(string dir) => _dir = dir;
    public bool ShouldPoll(DateTimeOffset now) => true;

    public Task<IReadOnlyList<FeedItem>> PollAsync(CancellationToken ct) {
        var items = new List<FeedItem>();
        if (!Directory.Exists(_dir)) return Task.FromResult<IReadOnlyList<FeedItem>>(items);

        foreach (var f in Directory.EnumerateFiles(_dir)) {
            if (!_seen.Add(f)) continue;
            try { items.Add(new FeedItem(Path.GetFileName(f), File.ReadAllText(f))); }
            catch (IOException) { _seen.Remove(f); }   // still being written; try next time
        }
        return Task.FromResult<IReadOnlyList<FeedItem>>(items);
    }
}
