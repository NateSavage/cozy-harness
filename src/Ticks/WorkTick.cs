using System.Text.Json.Serialization;
using CozyHarness.Channels;
using CozyHarness.Config;
using CozyHarness.Core;
using CozyHarness.Domain;
using CozyHarness.Goals;
using CozyHarness.Llm;
using CozyHarness.Prompts;
using CozyHarness.Retrieval;
using CozyHarness.Storage;

namespace CozyHarness.Ticks;

/// <summary>
/// Serves one goal. Two hard rules, both enforced here rather than left to the prompt:
///   1. A work tick may abandon its own goal with a reason. Not a failure.
///   2. A work tick may not RESOLVE by asking the operator. It can message him,
///      but it still has to write down what it did on its own.
/// </summary>
public sealed class WorkTick : ITick {
    public TickType Type => TickType.Work;

    private readonly LlamaClient _llm;
    private readonly IndexDb _db;
    private readonly ContextBuilder _ctx;
    private readonly GoalStore _goals;
    private readonly IOperatorChannel _channel;
    private readonly LlmConfig _cfg;
    private readonly AgentActivity _activity;

    public WorkTick(LlamaClient llm, IndexDb db, ContextBuilder ctx, GoalStore goals, IOperatorChannel channel,
                     LlmConfig cfg, AgentActivity activity)
    {
        _llm = llm; _db = db; _ctx = ctx; _goals = goals; _channel = channel; _cfg = cfg; _activity = activity;
    }

    public async Task<TickOutcome> RunAsync(CancellationToken ct) {
        // Least-recently-touched active goal. Deliberately not "most important":
        // rotation beats prioritisation at keeping a stack from collapsing to one groove.
        var candidates = _db.ActiveGoals();
        if (candidates.Count == 0)
            return TickOutcome.Nothing("no active goals to work on");

        var goal = _goals.Load(candidates[0].Id);
        if (goal is null)
            return TickOutcome.Nothing($"goal {candidates[0].Id} is in the index but not on disk");

        _activity.SetDetail($"working on \"{goal.Title}\"");
        _activity.MarkImportant();

        var budgetLeft = 5000;
        var p = _ctx.BeginStable(Seeds.WorkSystem);

        _ctx.AddGoal(p, goal, 400);
        _ctx.AddEpisodeBodies(p, goal.Id, 3, 1200);
        _ctx.AddRecentEpisodes(p, 8, 400, goal.Id);

        var obs = _db.UnconsumedObservations(20)
            .Where(o => Relevant(o.Content, goal))
            .Take(8).ToList();
        _ctx.AddObservations(p, obs, 1500);

        var outboundToday = _db.OutboundMessagesToday();
        _ctx.AddText(p, $"""
            ## Reaching my operator

            You've sent him {outboundToday} messages today. There's no hard limit;
            this is just so you can see it.

            """, 120);

        var result = await _llm.CompleteJsonAsync<WorkDecision>(
            p.Build("""
                Do the tick, then reply with JSON only:
                {
                  "did": "<what you actually did, in your own words, a few sentences>",
                  "summary": "<one line for the log>",
                  "outcome": "progress" | "waiting" | "abandon" | "done",
                  "why": "<required if outcome is abandon or done>",
                  "salience": 0.0-1.0,
                  "message_operator": "<optional, or null>",
                  "renew": true | false
                }
                """),
            _cfg.Slots["work"], _cfg.MaxTokensWork, ct);

        if (result is null)
            return new TickOutcome
            {
                Summary = $"work on \"{goal.Title}\" produced unparseable output",
                GoalId = goal.Id, Salience = 0.3,
            };

        _db.MarkObservationsConsumed(obs.Select(o => o.Id));

        // Enforcement, not suggestion: the message goes out, and the tick still
        // has to stand on what it did itself.
        if (!string.IsNullOrWhiteSpace(result.MessageOperator))
            await _channel.SendAsync(result.MessageOperator!, ct);

        var outcome = (result.Outcome ?? "progress").ToLowerInvariant();
        switch (outcome) {
            case "abandon" when !string.IsNullOrWhiteSpace(result.Why):
                _goals.Transition(goal, GoalState.Abandoned, result.Why);
                break;
            case "done" when !string.IsNullOrWhiteSpace(result.Why):
                _goals.Transition(goal, GoalState.Done, result.Why);
                break;
            default:
                if (result.Renew) _goals.Renew(goal);
                else _goals.Transition(goal, GoalState.Active);   // touches last_touched only
                break;
        }

        var body = result.Did ?? "";
        if (!string.IsNullOrWhiteSpace(result.MessageOperator))
            body += $"\n\n---\n\nI also sent him: {result.MessageOperator}";

        return new TickOutcome {
            Summary = result.Summary ?? $"worked on {goal.Title}",
            Body = body,
            GoalId = goal.Id,
            Salience = Math.Clamp(result.Salience ?? 0.5, 0, 1),
        };
    }

    /// <summary>Keyword overlap. Deliberately crude — add embeddings only once this has visibly failed.</summary>
    private static bool Relevant(string content, Goal g) {
        var terms = (g.Title + " " + (g.Description ?? ""))
            .Split(new[] { ' ', '\n', ',', '.', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4)
            .Select(w => w.ToLowerInvariant())
            .Distinct().ToList();
        var lower = content.ToLowerInvariant();
        return terms.Count == 0 || terms.Any(lower.Contains);
    }

    private sealed class WorkDecision {
        [JsonPropertyName("did")] public string? Did { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("outcome")] public string? Outcome { get; set; }
        [JsonPropertyName("why")] public string? Why { get; set; }
        [JsonPropertyName("salience")] public double? Salience { get; set; }
        [JsonPropertyName("message_operator")] public string? MessageOperator { get; set; }
        [JsonPropertyName("renew")] public bool Renew { get; set; }
    }
}
