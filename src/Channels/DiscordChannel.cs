using System.Text.RegularExpressions;
using System.Threading.Channels;
using CozyHarness.Core;
using Discord;
using Discord.WebSocket;

namespace CozyHarness.Channels;

/// <summary>
/// Discord.Net gateway client. One operator, reached over DM — not a
/// configured channel. Guild chat is deliberately out of scope for now: guild
/// messages are ignored entirely (see OnGatewayMessageAsync), and the whole
/// class assumes a single, stable DM channel with one specific user.
///
/// Because DMs are exempt from the Message Content privileged intent (a bot
/// always sees the content of DMs it's a party to, regardless), this needs no
/// privileged intents at all — see the constructor.
///
/// Two rules preserved from the original contract:
///   - Inbound interrupts the pulse cycle so real conversation is possible: an
///     accepted message is always handed to MessageReceived, with no filtering
///     or batching beyond "is this actually the operator talking."
///   - Outbound is never blocked. The daily budget is shown to the agent, not
///     enforced against it here.
///
/// Inbound messages go onto a private queue and are handled one at a time by a
/// single background worker (ProcessInboxAsync) instead of being awaited
/// directly on Discord.Net's gateway task. A reply can take minutes on CPU
/// inference, plus a git commit and mirror push on top — awaiting that inline
/// would block heartbeat processing on the same task and risk Discord dropping
/// the connection as unresponsive. The queue preserves the same effective
/// ordering the old inline-await had (one message fully handled before the
/// next starts) without blocking the socket while it happens.
///
/// Presence doubles as an ambient status: <see cref="OnActivityChanged"/> shows
/// whatever AgentActivity reports ("working on ...", "reading the world", ...)
/// for as long as a heavy tick runs, and a message that arrives mid-tick also
/// gets its own one-off notice with Interrupt/Let it finish buttons — a reply
/// comes either way, so "let it finish" is just acknowledging that, not a real
/// choice with a different outcome. The two presence writers (this one and the
/// reply-in-flight one in ProcessInboxAsync) don't coordinate; if both fire in
/// the same window the presence just flickers, which costs nothing.
/// </summary>
public sealed class DiscordChannel : IOperatorChannel, IAsyncDisposable {
    private const int MaxMessageLength = 2000;   // Discord's hard limit per message
    private const int FenceCloseReserve = 4;      // room to append "\n```" if a chunk breaks mid-fence
    private const int MaxErrorTraceLength = 3500; // Chunk() would happily split a longer one across many messages; this is a spam guard, not a Discord limit

    private const string InterruptButtonId = "activity-interrupt";
    private const string WaitButtonId = "activity-wait";

    private static readonly Regex FenceDelimiter =
        new(@"^```(\S*)[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Generated text going into a real channel shouldn't be trusted to ping people —
    // if the model ever echoes "@everyone" or a raw <@id> from context, this is what
    // stops it from actually notifying anyone.
    private static readonly AllowedMentions NoMentions = new(AllowedMentionTypes.None);

    private readonly string _token;
    private readonly ulong _operatorUserId;
    private readonly AgentActivity _activity;
    private readonly DiscordSocketClient _client;
    private readonly Channel<(SocketMessage Msg, string Text)> _inbox =
        Channel.CreateUnbounded<(SocketMessage Msg, string Text)>();

    private IDMChannel? _dmChannel;   // resolved once in StartAsync; every send/read after that reuses it
    private ulong? _lastInboundMessageId;
    private Task? _inboxWorker;

    public event Func<string, Task>? MessageReceived;

    public DiscordChannel(string token, ulong operatorUserId, AgentActivity activity)
    {
        // A real Discord snowflake is never 0 — this is the default `ulong`
        // when OperatorUserId is left unset in config. Left unchecked, the bot
        // would connect fine and have no one to DM, which is a much worse
        // failure than refusing to start.
        if (operatorUserId == 0)
            throw new ArgumentException(
                "OperatorUserId is 0 — set it to your Discord user ID (enable Developer Mode, right-click " +
                "your name, Copy User ID), or the bot has no one to DM.", nameof(operatorUserId));

        _token = token;
        _operatorUserId = operatorUserId;
        _activity = activity;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            // Neither privileged: DMs need no Message Content grant (see class
            // remarks), and Guilds is the non-privileged baseline Discord.Net's
            // own caching expects even when guild features go unused. No
            // GuildMessages — guild chat is out of scope for now.
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.DirectMessages,
            MessageCacheSize = 0,   // acted on immediately; nothing here needs a cache
            LogLevel = LogSeverity.Info,   // Info+ so connect/resume/ready lifecycle is visible when reconnects happen
        });

        _client.Log += OnLog;
        _client.MessageReceived += OnGatewayMessageAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
        _activity.Changed += OnActivityChanged;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task OnReady() { ready.TrySetResult(); return Task.CompletedTask; }
        _client.Ready += OnReady;

        try
        {
            await _client.LoginAsync(TokenType.Bot, _token);
            await _client.StartAsync();

            // A malformed token fails LoginAsync directly, but a well-formed,
            // *wrong* one just makes the gateway retry 401s forever without ever
            // throwing — StartAsync would otherwise hang until someone Ctrl-Cs
            // the whole harness. Bound the wait so a bad token fails loudly here.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using (timeoutCts.Token.Register(() => ready.TrySetCanceled(timeoutCts.Token)))
            {
                try { await ready.Task; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Discord gateway never became ready within 30s — check the bot token.");
                }
            }
        }
        finally
        {
            _client.Ready -= OnReady;
        }

        // Resolved once, up front, rather than re-resolved on every send: fails
        // loudly here (bad ID, or Discord refusing the DM — e.g. no shared
        // server, or the user's DMs closed to the bot) instead of on the first
        // thing the agent ever tries to say.
        var user = await _client.Rest.GetUserAsync(_operatorUserId)
            ?? throw new InvalidOperationException(
                $"Discord user {_operatorUserId} not found — check OperatorUserId is correct.");
        _dmChannel = await user.CreateDMChannelAsync();

        _inboxWorker = Task.Run(() => ProcessInboxAsync(ct), CancellationToken.None);
    }

    /// <summary>
    /// The only work done on Discord.Net's gateway task: cheap filtering plus an
    /// enqueue. Everything that can be slow — the reply itself — happens in
    /// ProcessInboxAsync, off this task. See the class remarks.
    /// </summary>
    private Task OnGatewayMessageAsync(SocketMessage msg)
    {
        if (msg.Channel is not IDMChannel) return Task.CompletedTask;   // guild chat is out of scope for now
        if (msg.Author.Id != _operatorUserId) return Task.CompletedTask;   // only the configured operator — never a stranger's DM

        var text = ExtractText(msg);
        if (text is null) return Task.CompletedTask;

        _inbox.Writer.TryWrite((msg, text));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves mention/channel/emoji snowflakes to readable text — raw
    /// <c>&lt;@id&gt;</c> syntax means nothing to the model, and it would
    /// otherwise get written permanently into the interaction log as noise.
    /// Falls back to a text stand-in when Content is empty but the operator
    /// sent an attachment, so it isn't silently dropped. Null means there is
    /// truly nothing here to act on.
    /// </summary>
    private static string? ExtractText(SocketMessage msg)
    {
        var text = (msg as SocketUserMessage)?.Resolve() ?? msg.Content;
        if (!string.IsNullOrWhiteSpace(text)) return text;

        if (msg.Attachments.Count == 0) return null;

        var names = string.Join(", ", msg.Attachments.Select(a => a.Filename));
        var noun = msg.Attachments.Count == 1 ? "an attachment" : "attachments";
        return $"[operator sent {noun} with no text: {names}]";
    }

    /// <summary>
    /// Runs for the lifetime of the channel, handling exactly one inbound
    /// message at a time in arrival order — the same ordering guarantee the
    /// original inline-await design had, just off the gateway task. Also
    /// covers the "is anything even happening" gap: a typing indicator and a
    /// watching status while a reply is in flight, since generation can take
    /// minutes and silence is otherwise indistinguishable from "didn't arrive"
    /// or "crashed."
    /// </summary>
    private async Task ProcessInboxAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (msg, text) in _inbox.Reader.ReadAllAsync(ct))
            {
                _lastInboundMessageId = msg.Id;
                var handler = MessageReceived;
                if (handler is null) continue;

                IDisposable? typing = null;
                try
                {
                    // A reply always comes either way — this is a heads-up,
                    // not a gate, and never blocks the reply from starting.
                    if (_activity.CurrentTick is not null)
                        await SendBusyNoticeAsync(msg.Id);

                    typing = _dmChannel!.EnterTypingState();
                    await _client.SetGameAsync("a reply taking shape", type: ActivityType.Watching);
                }
                catch { /* best-effort; a presence hiccup shouldn't block the actual reply */ }

                try { await handler(text); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[discord] inbound handler failed: {ex}");
                    try { await NotifyErrorAsync("handling your message failed", ex, ct); } catch { /* best-effort */ }
                }
                finally
                {
                    typing?.Dispose();
                    try { await _client.SetGameAsync(null); } catch { /* best-effort */ }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>
    /// "Here's what I'm doing, interrupt or let it finish?" — sent once per
    /// inbound message that arrives while a heavy tick is running. Threaded to
    /// that specific message rather than going through SendAsync, since it's
    /// tied to one arrival, not a general thing the agent wants to say.
    /// </summary>
    private async Task SendBusyNoticeAsync(ulong replyToId)
    {
        var components = new ComponentBuilder()
            .WithButton("Interrupt", InterruptButtonId, ButtonStyle.Danger)
            .WithButton("Let it finish", WaitButtonId, ButtonStyle.Secondary)
            .Build();

        try
        {
            await _dmChannel!.SendMessageAsync($"Right now I'm {_activity.Summary()}.",
                allowedMentions: NoMentions,
                messageReference: new MessageReference(replyToId),
                components: components);
        }
        catch { /* best-effort — the real reply is coming regardless */ }
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.Channel.Id != _dmChannel?.Id || component.User.Id != _operatorUserId) return;

        try
        {
            switch (component.Data.CustomId)
            {
                case InterruptButtonId:
                    var interrupted = _activity.TryInterrupt();
                    await component.UpdateAsync(m =>
                    {
                        m.Content = interrupted ? "→ interrupting now." : "→ already finished — nothing to interrupt.";
                        m.Components = new ComponentBuilder().Build();
                    });
                    break;

                case WaitButtonId:
                    await component.UpdateAsync(m =>
                    {
                        m.Content = "→ okay, I'll get to it.";
                        m.Components = new ComponentBuilder().Build();
                    });
                    break;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[discord] button handling failed: {ex}"); }
    }

    /// <summary>Reflects AgentActivity in the bot's presence for as long as a heavy tick runs — see class remarks on why replies use their own, separate presence text.</summary>
    private void OnActivityChanged()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (_activity.CurrentTick is null) await _client.SetGameAsync(null);
                else await _client.SetGameAsync(_activity.Summary(), type: ActivityType.Watching);
            }
            catch { /* best-effort */ }
        });
    }

    public async Task SendAsync(string content, CancellationToken ct)
    {
        if (_dmChannel is null)
            throw new InvalidOperationException("DiscordChannel.StartAsync hasn't completed yet — no DM channel resolved.");

        if (string.IsNullOrWhiteSpace(content)) return;   // Discord rejects an empty message body outright

        // The first chunk threads to whatever the operator most recently said,
        // then the reference is consumed — a later, unprompted send (WorkTick's
        // message_operator, SayStuckAsync) shouldn't look like a reply to an old
        // conversation just because nothing newer came in.
        var reference = _lastInboundMessageId is { } id ? new MessageReference(id) : null;
        _lastInboundMessageId = null;

        var first = true;
        foreach (var chunk in Chunk(content))
        {
            ct.ThrowIfCancellationRequested();
            await _dmChannel.SendMessageAsync(chunk,
                allowedMentions: NoMentions,
                messageReference: first ? reference : null);
            first = false;
        }
    }

    public Task NotifySensitiveAsync(DateTimeOffset when, CancellationToken ct) =>
        SendAsync($"(a conversation at {when:HH:mm} is marked sensitive in my log)", ct);

    public Task SayStuckAsync(string what, CancellationToken ct) =>
        SendAsync($"I think I'm stuck: {what}", ct);

    public Task NotifyErrorAsync(string context, Exception ex, CancellationToken ct)
    {
        var trace = ex.ToString();
        if (trace.Length > MaxErrorTraceLength) trace = trace[..MaxErrorTraceLength] + "\n[...truncated]";
        return SendAsync($"⚠️ **{context}**\n```\n{trace}\n```", ct);
    }

    private static Task OnLog(LogMessage msg)
    {
        var stream = msg.Severity <= LogSeverity.Warning ? Console.Error : Console.Out;
        var suffix = msg.Exception is null ? "" : $" ({msg.Exception.Message})";
        stream.WriteLine($"[discord] {msg.Severity}: {msg.Message}{suffix}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Splits on the last newline before the limit where possible, so a long
    /// reply doesn't get cut mid-sentence for no reason. Also tracks fenced
    /// code blocks (```) across the split: a chunk that would leave a fence
    /// open gets it closed at the break and reopened — same language tag — at
    /// the top of the next chunk, so neither half renders with broken markdown.
    /// </summary>
    private static IEnumerable<string> Chunk(string content)
    {
        if (content.Length <= MaxMessageLength) { yield return content; yield break; }

        var start = 0;
        string? openFence = null;   // language tag carried into this piece, if a fence is open
        while (start < content.Length)
        {
            var openReserve = openFence is null ? 0 : openFence.Length + 4;   // room for "```lang\n"
            var limit = MaxMessageLength - openReserve - FenceCloseReserve;
            var remaining = content.Length - start;
            var end = start + Math.Min(limit, remaining);
            if (end < content.Length)
            {
                var lastNewline = content.LastIndexOf('\n', end - 1, end - start);
                if (lastNewline > start) end = lastNewline + 1;
            }

            var piece = content[start..end];
            var matches = FenceDelimiter.Matches(piece);
            var stillOpen = openFence;
            foreach (Match m in matches)
                stillOpen = stillOpen is null ? m.Groups[1].Value : null;

            // A fence opened right at the tail of this piece with nothing after
            // it would otherwise open-then-immediately-close an empty block here
            // and put the real content in the next chunk. Push the whole fence
            // to the next chunk instead.
            if (openFence is null && stillOpen is not null && matches.Count > 0)
            {
                var lastOpen = matches[^1];
                if (lastOpen.Index > 0 && piece[(lastOpen.Index + lastOpen.Length)..].Trim().Length == 0)
                {
                    end = start + lastOpen.Index;
                    piece = content[start..end];
                    stillOpen = null;
                }
            }

            var hasMore = end < content.Length;
            var chunk = openFence is null ? piece : $"```{openFence}\n{piece}";
            if (stillOpen is not null && hasMore) chunk = chunk.TrimEnd('\n') + "\n```";

            yield return chunk.TrimEnd('\n');
            openFence = hasMore ? stillOpen : null;
            start = end;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Log -= OnLog;
        _client.MessageReceived -= OnGatewayMessageAsync;
        _client.ButtonExecuted -= OnButtonExecutedAsync;
        _activity.Changed -= OnActivityChanged;
        _inbox.Writer.TryComplete();

        if (_inboxWorker is not null)
        {
            try { await _inboxWorker; } catch { /* already logged inside the loop */ }
        }

        try { await _client.StopAsync(); } catch { /* best-effort on the way out */ }
        _client.Dispose();
    }
}
