using System.Text.Json.Serialization;
using CozyHarness.Channels;
using CozyHarness.Config;
using CozyHarness.Domain;
using CozyHarness.Llm;
using CozyHarness.People;
using CozyHarness.Prompts;
using CozyHarness.Retrieval;
using CozyHarness.Storage;

namespace CozyHarness.Ticks;

/// <summary>
/// The operator is around most of the time, so replies get a fast path that
/// interrupts the pulse cycle. But a reply is NOT a work tick — talking to him is
/// something the agent does; it isn't how work gets done. That separation is what
/// keeps near-constant availability from becoming dependence.
/// </summary>
public sealed class ReplyTick : ITick {
    public TickType Type => TickType.Reply;

    private readonly LlamaClient _llm;
    private readonly IndexDb _db;
    private readonly ContextBuilder _ctx;
    private readonly IOperatorChannel _channel;
    private readonly PeopleStore _people;
    private readonly LlmConfig _cfg;
    private readonly ChannelConfig _channelCfg;

    public ReplyTick(LlamaClient llm, IndexDb db, ContextBuilder ctx, IOperatorChannel channel, PeopleStore people, LlmConfig cfg, ChannelConfig channelCfg)
    {
        _llm = llm; _db = db; _ctx = ctx; _channel = channel;
        _people = people; _cfg = cfg; _channelCfg = channelCfg;
    }

    public async Task<TickOutcome> RunAsync(CancellationToken ct) {
        var pending = _db.PendingInbound(5);
        if (pending.Count == 0) return TickOutcome.Nothing("no messages waiting");

        var p = _ctx.BeginStable(Seeds.ReplySystem);
        _ctx.AddRecentEpisodes(p, 6, 500);
        _ctx.AddText(p, "## He said\n\n" +
            string.Join("\n\n", pending.Select(m => m.Content)) + "\n", 2000);

        var r = await _llm.CompleteJsonAsync<ReplyResult>(
            p.Build("""
                Reply with JSON only:
                {
                  "reply": "<what you say back>",
                  "summary": "<one line for the log>",
                  "sensitive": true | false,
                  "salience": 0.0-1.0
                }

                Mark sensitive if this conversation touches something personal or
                private. Your operator is told when that happens; he asked to be.
                It is not hidden from anyone.
                """),
            _cfg.Slots["reply"], _cfg.MaxTokensReply, ct);

        if (r?.Reply is null || string.IsNullOrWhiteSpace(r.Reply)) {
            // Silence here is worse than the tick just failing: the operator
            // watched the typing indicator run and then nothing arrived at
            // all — indistinguishable from a hang. Say so, even without a
            // real reply; this is exactly what the stuck channel is for.
            await _channel.SayStuckAsync("couldn't get a reply together for that — try again?", ct);
            return new TickOutcome { Summary = "couldn't form a reply", Salience = 0.3 };
        }

        await _channel.SendAsync(r.Reply, ct);
        _db.AddMessage("out", _channelCfg.OperatorName, r.Reply);

        var now = DateTimeOffset.UtcNow;
        var slug = _channelCfg.OperatorName;
        foreach (var (id, content) in pending) {
            _people.AppendInteractionLog(slug, now, $"**him:** {content}", r.Sensitive);
            _db.MarkMessageHandled(id, "(reply)");
        }
        _people.AppendInteractionLog(slug, now, $"**me:** {r.Reply}", r.Sensitive);

        // Operator asked to be told when a conversation is marked sensitive.
        if (r.Sensitive && _channelCfg.NotifyOperatorOnSensitive)
            await _channel.NotifySensitiveAsync(now, ct);

        return new TickOutcome {
            Summary = r.Summary ?? "talked with him",
            Body = r.Reply,
            Person = slug,
            Sensitive = r.Sensitive,
            Salience = Math.Clamp(r.Salience ?? 0.5, 0, 1),
        };
    }

    private sealed class ReplyResult {
        [JsonPropertyName("reply")] public string? Reply { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("sensitive")] public bool Sensitive { get; set; }
        [JsonPropertyName("salience")] public double? Salience { get; set; }
    }
}
