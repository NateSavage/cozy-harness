using System.Text.Json.Serialization;
using CozyHarness.Config;
using CozyHarness.Domain;
using CozyHarness.Goals;
using CozyHarness.Llm;
using CozyHarness.Prompts;
using CozyHarness.Retrieval;
using CozyHarness.Storage;

namespace CozyHarness.Ticks;

/// <summary>
/// Daily: one paragraph about the day. Weekly: the important one — no external
/// work at all, only rereading. This produces no visible output and is the
/// easiest thing in the build to skip. It is where development happens.
/// </summary>
public sealed class ReflectTick : ITick {
    public TickType Type => _weekly ? TickType.ReflectWeekly : TickType.ReflectDaily;

    private readonly bool _weekly;
    private readonly LlamaClient _llm;
    private readonly IndexDb _db;
    private readonly ContextBuilder _ctx;
    private readonly GoalStore _goals;
    private readonly AgentTree _tree;
    private readonly LlmConfig _cfg;

    public ReflectTick(bool weekly, LlamaClient llm, IndexDb db, ContextBuilder ctx, GoalStore goals, AgentTree tree, LlmConfig cfg)
    {
        _weekly = weekly; _llm = llm; _db = db; _ctx = ctx; _goals = goals; _tree = tree; _cfg = cfg;
    }

    public async Task<TickOutcome> RunAsync(CancellationToken ct)
        => _weekly ? await WeeklyAsync(ct) : await DailyAsync(ct);

    private async Task<TickOutcome> DailyAsync(CancellationToken ct) {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var eps = _db.EpisodesBetween(since, DateTimeOffset.UtcNow, 30);

        var p = _ctx.BeginStable(Seeds.ReflectDailySystem);
        var lines = eps.Count == 0
            ? "Nothing was recorded today. Most days are like this.\n"
            : string.Join("\n", eps.Select(e => $"- {e.Summary}"));
        _ctx.AddText(p, $"## Today\n\n{lines}\n", 1500);

        var r = await _llm.CompleteAsync(
            p.Build("Write one paragraph. No headings, no lists, no summary of tasks."),
            _cfg.Slots["reflect"], 400, 0.8, ct: ct);

        return new TickOutcome {
            Summary = eps.Count == 0 ? "a quiet day" : $"end of day: {eps.Count} things happened",
            Body = r.Text.Trim(),
            Salience = 0.4,
            TokensUsed = r.Tokens,
            DidNothing = eps.Count == 0,
        };
    }

    private async Task<TickOutcome> WeeklyAsync(CancellationToken ct) {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var eps = _db.EpisodesBetween(since, DateTimeOffset.UtcNow, 60);
        var active = _db.ActiveGoals();
        var decayed = _goals.DecayStale();
        var singleSource = _db.SingleSourceBeliefCandidates();

        var p = _ctx.BeginStable(Seeds.ReflectWeeklySystem);

        _ctx.AddText(p, "## The week\n\n" + string.Join("\n", eps.Select(e => $"- {e.Summary}")) + "\n", 3000);

        var goalLines = active.Select(g => {
            var touched = g.LastTouched is not null && DateTimeOffset.TryParse(g.LastTouched, out var d)
                ? ContextBuilder.Ago(d) : "never";
            return $"- **{g.Title}** ({g.Kind}) — last touched {touched}";
        });
        _ctx.AddText(p, "## Goals currently active\n\n" + string.Join("\n", goalLines) + "\n", 800);

        if (decayed.Count > 0)
            _ctx.AddText(p, "## Went dormant this week (unreaffirmed)\n\n" +
                string.Join("\n", decayed.Select(g => $"- {g.Title}")) + "\n", 300);

        if (!_goals.HasEnoughUselessGoals())
            _ctx.AddText(p, """
                ## Worth noticing

                Nothing in your active goals is useless. Everything you're doing is
                for something. That may be fine, or it may mean your goals have
                quietly become a task list.

                """, 200);

        if (singleSource.Count > 0)
            _ctx.AddText(p, "## Beliefs resting on one person\n\n" +
                string.Join("\n", singleSource.Select(s => $"- {s.Count} things you know only because {s.Person} told you")) + "\n", 300);

        var r = await _llm.CompleteJsonAsync<WeeklyResult>(
            p.Build("""
                Reply with JSON only:
                {
                  "reflection": "<what you actually think, several paragraphs>",
                  "summary": "<one line>",
                  "promote": ["<goal id>", ...],
                  "abandon": [{"id": "<goal id>", "why": "<reason>"}],
                  "rewrite_self_model": null | "<the new text, only if something genuinely changed>"
                }
                """),
            _cfg.Slots["reflect"], _cfg.MaxTokensReflect, ct);

        if (r is null)
            return new TickOutcome { Summary = "weekly reflection produced unparseable output", Salience = 0.5 };

        foreach (var id in r.Promote ?? new()) {
            var g = _goals.Load(id);
            if (g is { State: GoalState.Proposed or GoalState.Dormant })
                _goals.Transition(g, GoalState.Active);
        }

        foreach (var a in r.Abandon ?? new()) {
            if (a.Id is null || string.IsNullOrWhiteSpace(a.Why)) continue;  // no silent closures
            var g = _goals.Load(a.Id);
            if (g is not null) _goals.Transition(g, GoalState.Abandoned, a.Why);
        }

        if (!string.IsNullOrWhiteSpace(r.RewriteSelfModel)) {
            // Expensive on purpose: invalidates the warm prefix in every slot.
            File.WriteAllText(_tree.Abs("self/model.md"), r.RewriteSelfModel!.Trim() + "\n");
        }

        return new TickOutcome {
            Summary = r.Summary ?? "weekly reflection",
            Body = r.Reflection,
            Salience = 0.9,
        };
    }

    private sealed class WeeklyResult {
        [JsonPropertyName("reflection")] public string? Reflection { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("promote")] public List<string>? Promote { get; set; }
        [JsonPropertyName("abandon")] public List<AbandonEntry>? Abandon { get; set; }
        [JsonPropertyName("rewrite_self_model")] public string? RewriteSelfModel { get; set; }
    }

    private sealed class AbandonEntry {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("why")] public string? Why { get; set; }
    }
}
