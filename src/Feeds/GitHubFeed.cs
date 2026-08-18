using System.Net.Http.Json;
using System.Text.Json;

namespace CozyHarness.Feeds;

/// <summary>
/// The operator's public activity. For knowing him — the intake prompt says so
/// explicitly, and it matters: without it, his commits become a task backlog.
/// </summary>
public sealed class GitHubFeed : IFeed {
    private readonly HttpClient _http;
    private readonly string _user;
    private DateTimeOffset _lastSeen = DateTimeOffset.UtcNow.AddDays(-1);

    public string Name => "github";

    public GitHubFeed(HttpClient http, string user) {
        _http = http;
        _user = user;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "agent-harness");
    }

    public bool ShouldPoll(DateTimeOffset now) => true;

    public async Task<IReadOnlyList<FeedItem>> PollAsync(CancellationToken ct) {
        var items = new List<FeedItem>();
        var json = await _http.GetFromJsonAsync<JsonElement>(
            $"https://api.github.com/users/{_user}/events/public", ct);

        if (json.ValueKind != JsonValueKind.Array) return items;

        var newest = _lastSeen;
        foreach (var e in json.EnumerateArray()) {
            if (!e.TryGetProperty("created_at", out var createdEl)) continue;
            if (!DateTimeOffset.TryParse(createdEl.GetString(), out var created)) continue;
            if (created <= _lastSeen) continue;
            if (created > newest) newest = created;

            var type = e.GetProperty("type").GetString() ?? "Event";
            var repo = e.TryGetProperty("repo", out var r) ? r.GetProperty("name").GetString() : "?";
            var detail = Describe(e, type);
            items.Add(new FeedItem($"{repo}@{created:yyyy-MM-ddTHH:mm}", $"{type} in {repo}: {detail}"));
        }
        _lastSeen = newest;
        return items;
    }

    private static string Describe(JsonElement e, string type) {
        if (!e.TryGetProperty("payload", out var p)) return type;
        if (type == "PushEvent" && p.TryGetProperty("commits", out var commits))
            return string.Join("; ", commits.EnumerateArray()
                .Select(c => c.GetProperty("message").GetString())
                .Where(m => m is not null).Take(5)!);
        if (p.TryGetProperty("action", out var a)) return a.GetString() ?? type;
        return type;
    }
}
