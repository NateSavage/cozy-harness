using System.Text.Json.Serialization;
using CozyHarness.Config;
using CozyHarness.Domain;
using CozyHarness.Feeds;
using CozyHarness.Goals;
using CozyHarness.Llm;
using CozyHarness.Prompts;
using CozyHarness.Retrieval;
using CozyHarness.Storage;

namespace CozyHarness.Ticks;

/// <summary>
/// Reads the world. The prompt is explicit that the operator's commits are for
/// knowing him, not for finding work — without that, "be useful to the only
/// person here" becomes the most available goal in the environment and
/// keep-earning arrives through the back door.
/// </summary>
public sealed class IntakeTick : ITick
{
    public TickType Type => TickType.Intake;

    private readonly LlamaClient _llm;
    private readonly IndexDb _db;
    private readonly ContextBuilder _ctx;
    private readonly GoalStore _goals;
    private readonly IReadOnlyList<IFeed> _feeds;
    private readonly LlmConfig _cfg;

    public IntakeTick(LlamaClient llm, IndexDb db, ContextBuilder ctx, GoalStore goals,
                      IReadOnlyList<IFeed> feeds, LlmConfig cfg)
    {
        _llm = llm; _db = db; _ctx = ctx; _goals = goals; _feeds = feeds; _cfg = cfg;
    }

    public async Task<TickOutcome> RunAsync(CancellationToken ct)
    {
        var fetched = 0;
        foreach (var s in _feeds)
        {
            if (!s.ShouldPoll(DateTimeOffset.UtcNow)) continue;
            try
            {
                foreach (var item in await s.PollAsync(ct))
                {
                    _db.AddObservation(s.Name, item.Reference, item.Content);
                    fetched++;
                }
            }
            catch (Exception ex)
            {
                _db.AddObservation("harness", null, $"feed {s.Name} failed: {ex.Message}");
            }
        }

        var obs = _db.UnconsumedObservations(40);
        if (obs.Count == 0)
            return TickOutcome.Nothing("nothing new in the world today");

        var p = _ctx.BeginStable(Seeds.IntakeSystem);
        _ctx.AddObservations(p, obs, 4000);
        _ctx.AddRecentEpisodes(p, 6, 400);

        var result = await _llm.CompleteJsonAsync<IntakeResult>(
            p.Build("""
                Reply with JSON only:
                {
                  "noticed": "<what you noticed, a paragraph, your own words>",
                  "summary": "<one line for the log>",
                  "salience": 0.0-1.0,
                  "propose_goal": null | {"title": "...", "kind": "longitudinal|craft|useless|relationaloperator|relationalother", "why": "..."}
                }
                """),
            _cfg.Slots["intake"], _cfg.MaxTokensIntake, ct);

        _db.MarkObservationsConsumed(obs.Select(o => o.Id));

        if (result is null)
            return new TickOutcome { Summary = $"read {obs.Count} things; output unparseable", Salience = 0.3 };

        string? goalId = null;
        if (result.ProposeGoal is { Title: not null })
        {
            var kind = Enum.TryParse<GoalKind>(result.ProposeGoal.Kind, true, out var k) ? k : GoalKind.Craft;
            // Proposed, not active. Promotion happens in reflect, deliberately.
            goalId = _goals.Create(result.ProposeGoal.Title, kind, result.ProposeGoal.Why, null).Id;
        }

        return new TickOutcome
        {
            Summary = result.Summary ?? $"read {obs.Count} things",
            Body = result.Noticed,
            Salience = Math.Clamp(result.Salience ?? 0.5, 0, 1),
            GoalId = goalId,
        };
    }

    private sealed class IntakeResult
    {
        [JsonPropertyName("noticed")] public string? Noticed { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("salience")] public double? Salience { get; set; }
        [JsonPropertyName("propose_goal")] public ProposedGoal? ProposeGoal { get; set; }
    }

    private sealed class ProposedGoal
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("why")] public string? Why { get; set; }
    }
}
