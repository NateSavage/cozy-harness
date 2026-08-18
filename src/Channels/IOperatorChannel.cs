namespace CozyHarness.Channels;

public interface IOperatorChannel {
    /// <summary>
    /// Raised when an allowed sender says something. Drives the fast path.
    /// The id identifies who — the operator or someone on the whitelist —
    /// purely so a reply can be routed back to the right person; see
    /// ReplyToAsync. The name is whatever the channel currently displays for
    /// them (e.g. Discord's own display name) — passive best-effort
    /// tracking for PeopleStore.SyncDiscordName, not something they asked
    /// for; see SetPreferredName for the difference.
    /// </summary>
    event Func<ulong, string, string, Task>? MessageReceived;

    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// What the agent is currently called on this channel — Discord's own
    /// display name for the bot's account, read live rather than configured
    /// separately, the same reasoning as why contact names come from
    /// PeopleStore/Discord rather than a static string: it can't drift out
    /// of sync with what the person on the other end actually sees. Only
    /// meaningful after StartAsync has completed.
    /// </summary>
    string AgentDisplayName { get; }

    /// <summary>Always to the operator specifically — proactive messages (WorkTick's message_operator, stuck/error/sensitive notices) are never meant for anyone else on the whitelist.</summary>
    Task SendAsync(string content, CancellationToken ct);

    /// <summary>
    /// Registers a named command (a Discord slash command; a `/admin name`
    /// line in ConsoleChannel) whose handler returns the text to show back.
    /// Call once per command, before StartAsync, from Program.cs — the
    /// channel stays domain-agnostic; whoever registers supplies the actual
    /// lookup (querying IndexDb, etc.). Operator + AgentConfig.ChannelConfig.AdminUsers
    /// only: a materially bigger trust grant than the plain DM whitelist, not
    /// something extended to it — see RegisterWhitelistedCommandAsync for
    /// that. Distinct from the control-plane surfaces that stay operator-only
    /// unconditionally (interrupt buttons, SendAsync's proactive sends) —
    /// admins get this, not the operator's own identity.
    ///
    /// Every command registered here is grouped under a single "admin"
    /// command — `/admin goals`, `/admin chores`, etc. in Discord — so the
    /// name itself signals operator-only rather than that only being
    /// discoverable from a permission error after the fact. `name` is the
    /// subcommand, not the top-level command.
    ///
    /// A `name` containing a space (e.g. "debug context") becomes a
    /// subcommand GROUP wrapping a subcommand — `/admin debug context` —
    /// rather than one flat subcommand, since Discord doesn't allow spaces
    /// in a single subcommand name. ConsoleChannel needs no special handling
    /// for this: it matches the whole "/admin "-prefixed remainder as one
    /// string either way.
    ///
    /// The handler receives the invoking user's id as its first argument —
    /// the operator or one of AdminUsers, whichever actually typed the
    /// command (ConsoleChannel passes its fixed ConsoleUserId sentinel,
    /// which is designed to equal the operator's id when there's no real
    /// Discord identity). This exists so a command whose answer is
    /// per-conversation (e.g. "debug context") can build it for whoever
    /// asked rather than defaulting to the operator — see Program.cs's
    /// registration of "debug context" for why this matters: with more than
    /// one admin, silently answering for the operator instead of the actual
    /// caller means one admin's debug output can leak another person's
    /// conversation.
    ///
    /// Hard requirement, not an incidental property, and shared with
    /// RegisterWhitelistedCommandAsync below: a command invocation and its
    /// handler's response must never reach the model's context. Neither side
    /// goes through MessageReceived, db.AddMessage, or
    /// PeopleStore.AppendInteractionLog — implementations must answer the
    /// command directly (Discord: an ephemeral interaction response; Console:
    /// a direct Console.WriteLine) and never route it through SendAsync/
    /// ReplyToAsync or any other path ContextBuilder later reads from. If a
    /// future command's handler needs to write something durable, it must not
    /// be anything RecentConversation/ContextBuilder pulls in as if it were
    /// something the agent said or was told.
    /// </summary>
    Task RegisterCommandAsync(string name, string description, Func<ulong, CancellationToken, Task<string>> handler);

    /// <summary>
    /// Same contract as RegisterCommandAsync — including the hard
    /// context-isolation requirement documented there — except the audience
    /// is the operator OR anyone on ChannelConfig.AllowedUsers (AdminUsers
    /// included — they're implicitly on the plain whitelist too), not the
    /// operator alone. Registered as its own standalone top-level command
    /// (`/context`, typed the same in ConsoleChannel), deliberately NOT
    /// grouped under "admin": that name means the narrower operator+admin
    /// tier, and this isn't. Reserve this for things that are safe to hand
    /// to anyone allowed to DM the agent at all — operational counters like
    /// /context, not anything that touches goals, chores, or other people's
    /// conversations.
    /// </summary>
    Task RegisterWhitelistedCommandAsync(string name, string description, Func<CancellationToken, Task<string>> handler);

    /// <summary>
    /// A reply addressed to whoever actually sent the inbound message —
    /// operator or a whitelisted third party. Routes to that sender's own DM
    /// channel, not necessarily the operator's (see DiscordChannel's
    /// per-sender channel cache). This is purely message routing: the
    /// conversation history and prompt ReplyTick builds around this send are
    /// scoped and framed for whoever userId actually is, not the operator by
    /// default — see ReplyTick.BuildPrompt and Seeds.ReplySystemFor.
    /// </summary>
    Task ReplyToAsync(ulong userId, string content, CancellationToken ct);

    /// <summary>
    /// The away/online half of presence — separate from AgentActivity.Important,
    /// which can override this as Do Not Disturb regardless of quiet hours
    /// (see DiscordChannel.SyncStatusAsync for how the two combine). The
    /// `away` passed in is already the fully-decided value, not raw quiet
    /// hours: TickScheduler.UpdatePresenceAsync folds in whether a DM
    /// conversation is currently live (nothing within
    /// ChannelConfig.ConversationGapMinutes counts as quiet, even at 3am) —
    /// this method itself doesn't need to know why.
    /// </summary>
    Task SetAwayAsync(bool away, CancellationToken ct);

    /// <summary>
    /// The operator asked to be told when a conversation is marked sensitive.
    /// they are told THAT it happened, not made to read it — the notice is the point.
    /// </summary>
    Task NotifySensitiveAsync(DateTimeOffset when, CancellationToken ct);

    /// <summary>
    /// Always available, never rate-limited, always read. Both because it might
    /// matter and because it is the best debugging signal this system produces.
    /// </summary>
    Task SayStuckAsync(string what, CancellationToken ct);

    /// <summary>
    /// An internal failure the operator should know about — a crashed tick, a
    /// scheduler cycle that threw, anything unexpected. This is the harness
    /// reporting on itself, not the agent's own voice (contrast SayStuckAsync).
    /// Implementations must never let sending this notification itself throw
    /// back out — see ErrorReporter, which is what actually calls this.
    /// </summary>
    Task NotifyErrorAsync(string context, Exception ex, CancellationToken ct);
}
