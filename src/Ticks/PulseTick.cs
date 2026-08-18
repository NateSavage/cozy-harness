using System.Text.Json.Serialization;
using CozyHarness.Config;
using CozyHarness.Domain;
using CozyHarness.Llm;
using CozyHarness.Prompts;
using CozyHarness.Retrieval;
using CozyHarness.Storage;

namespace CozyHarness.Ticks;

/// <summary>
/// The cheapest tick. One question: does anything need attention?
///
/// Most pulses should answer "nothing". That is the system working, not idling —
/// so this tick writes no episode at all when the answer is no, keeping idleness
/// genuinely free rather than merely unpunished.
/// </summary>
public sealed class PulseTick : ITick {
    public TickType Type => TickType.Pulse;

    private readonly LlamaClient _llm;
    private readonly IndexDb _db;
    private readonly ContextBuilder _ctx;
    private readonly LlmConfig _cfg;
    private readonly int _maxWorkPerDay;

    public PulseTick(LlamaClient llm, IndexDb db, ContextBuilder ctx, LlmConfig cfg, int maxWorkPerDay) {
        _llm = llm; _db = db; _ctx = ctx; _cfg = cfg; _maxWorkPerDay = maxWorkPerDay;
    }

    public async Task<TickOutcome> RunAsync(CancellationToken ct) {
        var unread = _db.UnhandledMessageCount();
        var unconsumed = _db.UnconsumedObservationCount();
        var stale = _db.GoalsPastRenewal().Count;
        var active = _db.ActiveGoals();
        var workToday = _db.WorkTicksToday();

        // Cheap outs, no model call needed.
        if (active.Count == 0 && unconsumed == 0 && unread == 0)
            return TickOutcome.Nothing("nothing to attend to, and no goals yet");

        if (workToday >= _maxWorkPerDay)
            return TickOutcome.Nothing("work ceiling reached for today");

        var oldest = active.FirstOrDefault();
        var untouchedFor = oldest.LastTouched is not null && DateTimeOffset.TryParse(oldest.LastTouched, out var lt)
            ? ContextBuilder.Ago(lt) : "never touched";

        var p = _ctx.BeginStable(Seeds.PulseSystem, includeSelfModel: false);
        _ctx.AddText(p, $"""
            ## Right now

            - unread messages: {unread}
            - things read but not yet looked at: {unconsumed}
            - active goals: {active.Count}{(stale > 0 ? $" ({stale} past renewal)" : "")}
            - oldest untouched goal: {(active.Count > 0 ? $"\"{oldest.Title}\", {untouchedFor}" : "none")}
            - work ticks so far today: {workToday}

            """, 400);

        var result = await _llm.CompleteJsonAsync<PulseDecision>(
            p.Build("""
                Reply with JSON only:
                {"wake": "nothing" | "work" | "intake" | "reflect", "why": "<short>"}
                """),
            _cfg.Slots["pulse"], _cfg.MaxTokensPulse, ct);

        var decision = result?.Wake?.ToLowerInvariant() ?? "nothing";

        return decision switch {
            "work"    => new TickOutcome { Summary = $"waking work: {result?.Why}", Wake = TickType.Work, Silent = true },
            "intake"  => new TickOutcome { Summary = $"waking intake: {result?.Why}", Wake = TickType.Intake, Silent = true },
            "reflect" => new TickOutcome { Summary = $"waking reflect: {result?.Why}", Wake = TickType.ReflectDaily, Silent = true },
            _         => TickOutcome.Nothing(result?.Why ?? "nothing needed attention"),
        };
    }

    private sealed class PulseDecision {
        [JsonPropertyName("wake")] public string? Wake { get; set; }
        [JsonPropertyName("why")] public string? Why { get; set; }
    }
}
