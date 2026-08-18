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
    // Who to route the reply to — operator or a whitelisted sender (see
    // Program.cs's ReplyFactory). Purely routing: history, logging, and the
    // prompt below are still built as if talking to the operator, regardless
    // of who this actually is.
    private readonly ulong _replyToUserId;

    public ReplyTick(LlamaClient llm, IndexDb db, ContextBuilder ctx, IOperatorChannel channel, PeopleStore people,
                      LlmConfig cfg, ChannelConfig channelCfg, ulong replyToUserId)
    {
        _llm = llm; _db = db; _ctx = ctx; _channel = channel;
        _people = people; _cfg = cfg; _channelCfg = channelCfg; _replyToUserId = replyToUserId;
    }

    public async Task<TickOutcome> RunAsync(CancellationToken ct) {
        var isOperator = _replyToUserId == _channelCfg.OperatorUserId;
        // The Discord user id as a string — the scoping key for everything
        // below, so two different people talking to it around the same time
        // never see each other's messages woven into their own history.
        // Doubles as the PeopleStore/file-path key for non-operator contacts
        // — stable regardless of what displayName below resolves to right
        // now, unlike the operator's own key (their existing name-keyed
        // people/ directory predates any of this and isn't being migrated).
        var contactId = _replyToUserId.ToString();
        var peopleSlug = isOperator ? _channelCfg.OperatorName : contactId;
        // What to actually call them, separate from peopleSlug on purpose:
        // this can change over time (PeopleStore.SyncDiscordName,
        // SetPreferredName below) without touching where their history
        // lives. The operator's name is fixed by config, never auto-tracked.
        var displayName = isOperator
            ? _channelCfg.OperatorName
            : _people.CurrentName(contactId, _channelCfg.DisplayNameFor(_replyToUserId));

        var pending = _db.PendingInbound(contactId, 5);
        if (pending.Count == 0) return TickOutcome.Nothing("no messages waiting");

        var prompt = BuildPrompt(contactId, displayName);
        var r = await _llm.CompleteJsonAsync<ReplyResult>(prompt, _cfg.Slots["reply"], _cfg.MaxTokensReply, ct);

        // Real usage from the completion that just happened, not an estimate
        // — see LlamaClient._slotUsage. Checked here, before anything below
        // reads r.Reply, so the warning (if any) rides along in the same
        // message rather than as a separate, easy-to-miss follow-up, and so
        // what gets logged/recorded matches what was actually sent.
        if (r?.Reply is { Length: > 0 }) {
            var slot = _cfg.Slots["reply"];
            var used = _llm.LastKnownUsage(slot);
            var capacity = _cfg.MainContextSize / Math.Max(1, _cfg.MainSlots);
            if (used is not null && capacity > 0 && (double)used.Value / capacity >= _channelCfg.ContextWarningThreshold) {
                var pct = (int)(100.0 * used.Value / capacity);
                r.Reply += $"\n\n⚠️ *(heads up — this conversation is at {pct}% of what I can hold in context at once; the oldest parts may start dropping out of view soon.)*";
            }
        }

        if (r?.Reply is null || string.IsNullOrWhiteSpace(r.Reply)) {
            // Silence here is worse than the tick just failing: whoever's
            // waiting watched the typing indicator run and then nothing
            // arrived at all — indistinguishable from a hang. Say so, even
            // without a real reply. Routed to them directly (not
            // SayStuckAsync, which is operator-only) — the operator
            // shouldn't get a confusing "I'm stuck" about a conversation
            // they weren't even part of.
            await _channel.ReplyToAsync(_replyToUserId,
                "I think I'm stuck: couldn't get a reply together for that — try again?", ct);
            return new TickOutcome { Summary = "couldn't form a reply", Salience = 0.3 };
        }

        // Only for whitelisted others — see the class remarks on why the
        // operator's naming stays config-fixed. Recorded before building the
        // log entries below so a rename this same tick is reflected in them.
        if (!isOperator && !string.IsNullOrWhiteSpace(r.TheyWantToBeCalled)) {
            _people.SetPreferredName(contactId, r.TheyWantToBeCalled!);
            displayName = r.TheyWantToBeCalled!.Trim();
        }

        await _channel.ReplyToAsync(_replyToUserId, r.Reply, ct);
        _db.AddMessage("out", displayName, r.Reply, contactId);

        var now = DateTimeOffset.UtcNow;
        foreach (var (id, content) in pending) {
            _people.AppendInteractionLog(peopleSlug, now, $"**{displayName}:** {content}", r.Sensitive);
            _db.MarkMessageHandled(id, "(reply)");
        }
        _people.AppendInteractionLog(peopleSlug, now, $"**me:** {r.Reply}", r.Sensitive);

        // Operator asked to be told when a conversation is marked sensitive.
        if (r.Sensitive && _channelCfg.NotifyOperatorOnSensitive)
            await _channel.NotifySensitiveAsync(now, ct);

        return new TickOutcome {
            Summary = r.Summary ?? $"talked with {displayName}",
            Body = r.Reply,
            Person = displayName,
            Sensitive = r.Sensitive,
            Salience = Math.Clamp(r.Salience ?? 0.5, 0, 1),
        };
    }

    /// <summary>
    /// The exact prompt text for replying to contactId right now — same
    /// stable-prefix-then-variable assembly RunAsync uses, factored out here
    /// so there's exactly one place that builds it: RunAsync and
    /// BuildPromptPreview both call this, so the preview can never quietly
    /// drift from what a real reply actually sends.
    /// </summary>
    private string BuildPrompt(string contactId, string displayName) {
        var p = _ctx.BeginStable(Seeds.ReplySystem);
        _ctx.AddRecentEpisodes(p, 6, 500);
        // The current conversation's own messages appear here too, at the
        // tail — this is the actual back-and-forth, not just what's new, so
        // a reply several messages in isn't built as if it were the opening
        // line.
        var conversation = _db.RecentConversation(contactId, _channelCfg.ConversationGapMinutes);
        _ctx.AddConversation(p, conversation, displayName, 3000);

        return p.Build("""
            Reply with JSON only:
            {
              "reply": "<what you say back>",
              "summary": "<one line for the log>",
              "sensitive": true | false,
              "salience": 0.0-1.0,
              "they_want_to_be_called": "<name, only if they just asked you to call them something — otherwise null>"
            }

            Mark sensitive if this conversation touches something personal or
            private. Your operator is told when that happens; he asked to be.
            It is not hidden from anyone.
            """);
    }

    /// <summary>
    /// For /admin debug context — see Program.cs. Unlike RunAsync, this never
    /// touches the LLM and never requires a pending inbound message: the
    /// point isn't answering the newest thing, it's showing what the prompt
    /// looks like right now, which is meaningful even between messages.
    /// </summary>
    public string BuildPromptPreview() {
        var isOperator = _replyToUserId == _channelCfg.OperatorUserId;
        var contactId = _replyToUserId.ToString();
        var displayName = isOperator
            ? _channelCfg.OperatorName
            : _people.CurrentName(contactId, _channelCfg.DisplayNameFor(_replyToUserId));
        return BuildPrompt(contactId, displayName);
    }

    private sealed class ReplyResult {
        [JsonPropertyName("reply")] public string? Reply { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("sensitive")] public bool Sensitive { get; set; }
        [JsonPropertyName("salience")] public double? Salience { get; set; }
        [JsonPropertyName("they_want_to_be_called")] public string? TheyWantToBeCalled { get; set; }
    }
}
