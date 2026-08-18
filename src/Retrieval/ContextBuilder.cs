using System.Text;
using CozyHarness.Domain;
using CozyHarness.Storage;

namespace CozyHarness.Retrieval;

/// <summary>
/// Fills a token budget in priority order and stops when full.
///
/// Ordering is not cosmetic: with a warm KV slot, one changed token invalidates
/// the cache from that point onward. So the stable prefix (system prompt,
/// self-model, situation) comes first and per-tick material comes last. A reflect
/// tick that rewrites the self-model costs a full re-prefill of every slot —
/// which is fine, it is weekly, and there is something fitting about
/// self-revision being the expensive operation.
/// </summary>
public sealed class ContextBuilder {
    private readonly AgentTree _tree;
    private readonly IndexDb _db;

    public ContextBuilder(AgentTree tree, IndexDb db) { _tree = tree; _db = db; }

    /// <summary>Crude but adequate. Replace with the tokenizer endpoint if you want precision.</summary>
    public static int Estimate(string s) => s.Length / 4;

    public PromptParts BeginStable(string systemPrompt, bool includeSelfModel = true) {
        var sb = new StringBuilder();
        sb.Append(systemPrompt.TrimEnd()).Append("\n\n");
        sb.Append("## My situation\n\n").Append(_tree.ReadSituation().Trim()).Append("\n\n");
        if (includeSelfModel)
            sb.Append("## Who I am, as of my last reflection\n\n").Append(_tree.ReadSelfModel().Trim()).Append("\n\n");
        return new PromptParts(sb.ToString());
    }

    public void AddGoal(PromptParts p, Goal g, int budget) {
        var s = new StringBuilder();
        s.Append("## The goal I'm working on\n\n");
        s.Append($"**{g.Title}** ({g.Kind.ToString().ToLowerInvariant()}, id `{g.Id}`)\n");
        s.Append($"Set {Ago(g.Created)}. Last touched {Ago(g.LastTouched ?? g.Created)}.\n");
        if (g.RenewBy is not null)
            s.Append($"Expires to dormant {Ago(g.RenewBy.Value)} unless I reaffirm it.\n");
        if (!string.IsNullOrWhiteSpace(g.Description))
            s.Append('\n').Append(Truncate(g.Description.Trim(), budget * 3)).Append('\n');
        p.AddVariable(s.ToString(), budget);
    }

    public void AddRecentEpisodes(PromptParts p, int count, int budget, string? goalId = null) {
        var eps = _db.RecentEpisodes(count, goalId);
        if (eps.Count == 0) return;

        var s = new StringBuilder();
        s.Append(goalId is null ? "## Recently\n\n" : "## What I've done on this goal\n\n");
        foreach (var (path, ts, summary, _) in eps)
        {
            var when = DateTimeOffset.TryParse(ts, out var d) ? Ago(d) : ts;
            s.Append($"- [{when}] {summary}\n");
        }
        p.AddVariable(s.ToString(), budget);
    }

    /// <summary>
    /// Renders the actual back-and-forth — both directions, in order — rather
    /// than just whatever's newest. A reply built from only the latest inbound
    /// message has no memory of the three exchanges immediately before it in
    /// the same conversation; this is what fixes that. See
    /// IndexDb.RecentConversation for how the conversation boundary itself
    /// (as opposed to what's rendered once you have it) gets decided.
    /// </summary>
    public void AddConversation(PromptParts p, IEnumerable<(string Direction, string Content, string Ts)> messages, int budget) {
        var list = messages.ToList();
        if (list.Count == 0) return;

        var s = new StringBuilder("## Our conversation\n\n");
        foreach (var (direction, content, ts) in list) {
            var when = DateTimeOffset.TryParse(ts, out var d) ? Ago(d) : ts;
            var speaker = direction == "out" ? "me" : "him";
            s.Append($"**{speaker}** ({when}): {content.Trim()}\n\n");
        }
        p.AddVariable(s.ToString(), budget);
    }

    /// <summary>Full text of the most recent episodes for a goal — summaries lose too much for work ticks.</summary>
    public void AddEpisodeBodies(PromptParts p, string goalId, int count, int budget) {
        var eps = _db.RecentEpisodes(count, goalId);
        if (eps.Count == 0) return;

        var s = new StringBuilder("## In more detail\n\n");
        foreach (var (path, ts, _, _) in eps)
        {
            var abs = _tree.Abs(path);
            if (!File.Exists(abs)) continue;
            var (_, body) = Frontmatter.Read(File.ReadAllText(abs));
            var when = DateTimeOffset.TryParse(ts, out var d) ? Ago(d) : ts;
            s.Append($"### {when}\n\n{Truncate(body.Trim(), budget)}\n\n");
        }
        p.AddVariable(s.ToString(), budget);
    }

    public void AddObservations(PromptParts p, IEnumerable<(long Id, string Source, string Content)> obs, int budget) {
        var list = obs.ToList();
        if (list.Count == 0) return;
        var s = new StringBuilder("## What came in\n\n");
        foreach (var (id, source, content) in list)
            s.Append($"- ({source}) {Truncate(content.Replace('\n', ' ').Trim(), 400)}\n");
        p.AddVariable(s.ToString(), budget);
    }

    public void AddText(PromptParts p, string section, int budget) => p.AddVariable(section, budget);

    private static string Truncate(string s, int approxTokens) {
        var max = Math.Max(0, approxTokens * 4);
        return s.Length <= max ? s : s[..max] + "\n[...truncated]";
    }

    /// <summary>
    /// Relative time, not timestamps. The agent has no felt duration; giving it
    /// "three weeks ago" rather than an ISO string is the cheapest way to make
    /// elapsed time legible to it.
    /// </summary>
    public static string Ago(DateTimeOffset t) {
        var d = DateTimeOffset.UtcNow - t;
        var future = d < TimeSpan.Zero;
        d = d.Duration();

        string unit = d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min"
            : d.TotalHours < 36 ? $"{(int)d.TotalHours} hours"
            : d.TotalDays < 14 ? $"{(int)d.TotalDays} days"
            : d.TotalDays < 70 ? $"{(int)(d.TotalDays / 7)} weeks"
            : $"{(int)(d.TotalDays / 30)} months";

        return future ? $"in {unit}" : $"{unit} ago";
    }
}

/// <summary>Stable prefix first (cacheable), variable material appended after.</summary>
public sealed class PromptParts {
    private readonly string _stable;
    private readonly StringBuilder _variable = new();
    public int VariableTokens { get; private set; }

    public PromptParts(string stable) => _stable = stable;

    public void AddVariable(string section, int budgetTokens) {
        var cost = ContextBuilder.Estimate(section);
        if (cost > budgetTokens) {
            var cut = Math.Min(section.Length, Math.Max(0, budgetTokens * 4));
            section = section[..cut] + "\n[...truncated]\n";
            cost = budgetTokens;
        }
        _variable.Append(section).Append('\n');
        VariableTokens += cost;
    }

    public string Build(string closingInstruction) =>
        _stable + _variable + "\n" + closingInstruction.TrimEnd() + "\n";
}
