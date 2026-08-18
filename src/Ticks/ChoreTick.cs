using System.Text.Json.Serialization;
using CozyHarness.Chores;
using CozyHarness.Config;
using CozyHarness.Core;
using CozyHarness.Domain;
using CozyHarness.Llm;
using CozyHarness.Prompts;
using CozyHarness.Retrieval;
using CozyHarness.Storage;

namespace CozyHarness.Ticks;

/// <summary>
/// Works through one due item from a short, operator-authored list of routine
/// chores. Deliberately NOT a goal: no renewal, no kind, no claim on satisfying
/// the useless-goal requirement. Fires because a chore's own interval elapsed —
/// there's nothing for the pulse to judge here, so the scheduler runs this
/// directly rather than routing it through a wake decision.
///
/// A chore that stops making sense can be retired, same as a goal can be
/// abandoned — with a reason. Otherwise it just comes back on schedule.
/// </summary>
public sealed class ChoreTick : ITick {
    public TickType Type => TickType.Chore;

    private readonly LlamaClient _llm;
    private readonly IndexDb _db;
    private readonly ContextBuilder _ctx;
    private readonly ChoreStore _chores;
    private readonly LlmConfig _cfg;
    private readonly AgentActivity _activity;

    public ChoreTick(LlamaClient llm, IndexDb db, ContextBuilder ctx, ChoreStore chores, LlmConfig cfg, AgentActivity activity) {
        _llm = llm; _db = db; _ctx = ctx; _chores = chores; _cfg = cfg; _activity = activity;
    }

    public async Task<TickOutcome> RunAsync(CancellationToken ct) {
        var due = _db.DueChores(DateTimeOffset.UtcNow);
        if (due.Count == 0)
            return TickOutcome.Nothing("no chores due");

        var chore = _chores.Load(due[0]);
        if (chore is null)
            return TickOutcome.Nothing($"chore {due[0]} is in the index but not on disk");

        _activity.SetDetail($"the chore \"{chore.Title}\"");

        var p = _ctx.BeginStable(Seeds.ChoreSystem, includeSelfModel: false);
        _ctx.AddText(p, $"""
            ## Today's chore

            **{chore.Title}**
            {(string.IsNullOrWhiteSpace(chore.Description) ? "" : chore.Description.Trim())}

            Recurs every {FormatInterval(chore.Interval)}. Last done: {(chore.LastRun is { } lr ? ContextBuilder.Ago(lr) : "never")}.

            """, 400);

        var result = await _llm.CompleteJsonAsync<ChoreDecision>(
            p.Build("""
                Reply with JSON only:
                {
                  "did": "<what you actually did, a sentence or two>",
                  "summary": "<one line for the log>",
                  "salience": 0.0-1.0,
                  "retire": true | false,
                  "retire_why": "<required if retire is true>"
                }
                """),
            _cfg.Slots["chore"], _cfg.MaxTokensChore, ct);

        // The interval clock resets on attempt, parsed or not — an unparseable
        // chore shouldn't come back next cycle and crowd out everything else due.
        _chores.MarkRun(chore);

        if (result is null)
            return new TickOutcome {
                Summary = $"chore \"{chore.Title}\" produced unparseable output",
                Salience = 0.2,
            };

        if (result.Retire && !string.IsNullOrWhiteSpace(result.RetireWhy)) {
            _chores.Retire(chore, result.RetireWhy!);
            return new TickOutcome {
                Summary = $"retired chore \"{chore.Title}\": {result.RetireWhy}",
                Body = result.Did,
                Salience = Math.Clamp(result.Salience ?? 0.3, 0, 1),
            };
        }

        return new TickOutcome {
            Summary = result.Summary ?? $"did chore \"{chore.Title}\"",
            Body = result.Did,
            Salience = Math.Clamp(result.Salience ?? 0.2, 0, 1),
        };
    }

    private static string FormatInterval(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{t.TotalDays:0.#} days" : $"{t.TotalHours:0.#} hours";

    private sealed class ChoreDecision {
        [JsonPropertyName("did")] public string? Did { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("salience")] public double? Salience { get; set; }
        [JsonPropertyName("retire")] public bool Retire { get; set; }
        [JsonPropertyName("retire_why")] public string? RetireWhy { get; set; }
    }
}
