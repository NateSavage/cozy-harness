namespace CozyHarness.Config;

/// <summary>
/// Everything tunable. Loaded from agent.json. The agent may read this file (full transparency) but not write it.
/// </summary>
public sealed class AgentConfig {
    public string TreeRoot { get; set; } = "/agent";
    public string? MirrorRemote { get; set; } = "mirror";

    /// <summary>
    /// Off switch for GitStore: no init, no per-tick commit, no mirror push.
    /// The tree is still plain files on disk either way — this only turns off
    /// the git history layered on top of it.
    /// </summary>
    public bool EnableGit { get; set; } = true;

    public LlmConfig Llm { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();
    public ChannelConfig Channel { get; set; } = new();
    public GoalConfig Goals { get; set; } = new();
    public FeedConfig Feeds { get; set; } = new();
    public ChoreConfig Chores { get; set; } = new();

    /// <summary>Operator's IANA timezone. The agent has no circadian rhythm; its world does.</summary>
    public string OperatorTimeZone { get; set; } = "Europe/London";
}

public sealed class LlmConfig {
    // Unix sockets, not TCP endpoints: llama-server listens on one when --host
    // ends in .sock. Same box, no reason to pay for a TCP round trip.
    public string MainSocketPath { get; set; } = "/run/cozy-harness/llama-main.sock";
    public string PulseSocketPath { get; set; } = "/run/cozy-harness/llama-pulse.sock";

    /// <summary>Total context on the main server, across all of MainSlots — see /admin context.</summary>
    public int MainContextSize { get; set; } = 65536;
    /// <summary>Parallel slots on the main server; each gets MainContextSize / MainSlots. Forced to 1 when MTP is on — see module.nix's enableMtp.</summary>
    public int MainSlots { get; set; } = 4;
    /// <summary>Total context on the pulse server, which always runs single-slot — the pulse tick's own capacity, unlike the main server's, needs no division.</summary>
    public int PulseContextSize { get; set; } = 8192;

    /// <summary>
    /// One slot per tick type. Each slot keeps its KV cache warm between ticks,
    /// so only the delta is prefilled. See design doc section 7.
    /// </summary>
    public Dictionary<string, int> Slots { get; set; } = new() {
        ["pulse"]   = 0,
        ["work"]    = 0,
        ["intake"]  = 1,
        ["reflect"] = 2,
        ["reply"]   = 3,
        // Shares slot 0 with pulse/work rather than claiming a 5th warm slot:
        // chores are occasional and small, so the cache reuse isn't worth the
        // extra ~3GB. Give it its own slot if that stops being true.
        ["chore"]   = 0,
    };

    public int MaxTokensPulse { get; set; } = 64;
    public int MaxTokensWork { get; set; } = 1200;
    public int MaxTokensIntake { get; set; } = 900;
    public int MaxTokensReflect { get; set; } = 2400;
    public int MaxTokensReply { get; set; } = 500;
    public int MaxTokensChore { get; set; } = 400;

    /// <summary>Nucleus sampling threshold. Google's published default for Gemma 4.</summary>
    public double TopP { get; set; } = 0.95;

    /// <summary>Top-k cutoff. Google's published default for Gemma 4 — llama-server's own generic default (40) predates and doesn't match it.</summary>
    public int TopK { get; set; } = 64;

    /// <summary>
    /// Strings that end a completion early. `&lt;end_of_turn&gt;` is Gemma's real raw-completion
    /// stop token (id 106): the harness talks to llama.cpp's /completion endpoint directly,
    /// never /v1/chat/completions, so nothing ever renders the chat template, but an IT-tuned
    /// model can still emit its own end-of-turn token unprompted. "\n\n---" catches the model
    /// starting a new markdown section instead of stopping.
    /// </summary>
    public List<string> Stop { get; set; } = new() { "\n\n---", "<end_of_turn>" };
}

public sealed class ScheduleConfig {
    public int PulseIntervalSeconds { get; set; } = 120;
    /// <summary>Jitter fraction, so ticks don't lockstep with cron artifacts.</summary>
    public double PulseJitter { get; set; } = 0.20;
    public int QuietPulseIntervalSeconds { get; set; } = 900;

    public int QuietHourStart { get; set; } = 23;  // operator-local
    public int QuietHourEnd { get; set; } = 7;

    public int IntakeMorningHour { get; set; } = 7;
    public int IntakeEveningHour { get; set; } = 19;
    public int DailyReflectHour { get; set; } = 22;
    public DayOfWeek WeeklyReflectDay { get; set; } = DayOfWeek.Sunday;
    public int WeeklyReflectHour { get; set; } = 20;

    /// <summary>Ceiling on work ticks per day. A ceiling, not a quota to fill.</summary>
    public int MaxWorkTicksPerDay { get; set; } = 20;
}

public sealed class ChannelConfig {
    public string OperatorName { get; set; } = "Nates";
    public string? DiscordToken { get; set; }
    /// <summary>The operator's Discord user ID — DiscordChannel talks to them over DM, not a configured channel. Guild chat is out of scope for now.</summary>
    public ulong OperatorUserId { get; set; }
    /// <summary>
    /// Additional people allowed to DM the bot, beyond the operator (who's
    /// always implicitly allowed — no need to list them here too). A DM from
    /// anyone else is ignored outright: not queued, not logged, does not
    /// wake the agent. Conversation history (IndexDb.RecentConversation) and
    /// message logging are scoped per sender — see DisplayNameFor — but the
    /// reply prompt itself (Seeds.ReplySystem) is still written as if talking
    /// to the operator; a whitelisted sender gets attributed correctly, not
    /// treated as a distinct relationship with its own framing, yet.
    ///
    /// See <see cref="AdminUsers"/> for the same DM access plus admin command
    /// access — an admin entry doesn't need to be duplicated here too.
    /// </summary>
    public List<AllowedContact> AllowedUsers { get; set; } = new();
    /// <summary>
    /// Discord user IDs trusted with admin command access (<c>/admin ...</c>
    /// in Discord), on top of the operator, who always has it implicitly.
    /// Listing someone here also grants them DM access same as AllowedUsers
    /// — no need to list an admin in both. See DiscordChannel's remarks on
    /// AdminCommandName for what "admin command access" actually exposes
    /// (goals/chores/debug — a materially bigger trust grant than the plain
    /// DM whitelist).
    /// </summary>
    public List<AllowedContact> AdminUsers { get; set; } = new();
    /// <summary>Soft budget: surfaced to the agent so reaching out is a visible choice, never blocked.</summary>
    public int SoftOutboundBudgetPerDay { get; set; } = 12;
    /// <summary>Operator asked to be told when a conversation is marked sensitive.</summary>
    public bool NotifyOperatorOnSensitive { get; set; } = true;
    /// <summary>
    /// How long a silence has to run before the next message counts as a new
    /// conversation rather than a continuation. Governs how far back
    /// ReplyTick's context reaches — see IndexDb.RecentConversation.
    /// </summary>
    public int ConversationGapMinutes { get; set; } = 30;

    /// <summary>
    /// Fraction of the reply slot's context window (LlmConfig.MainContextSize
    /// / MainSlots) that triggers an in-band warning appended to the reply —
    /// see ReplyTick. 1.0 (or higher) never warns, since usage can't exceed
    /// capacity. Exposed as services.cozy-harness.contextWarningThreshold.
    /// </summary>
    public double ContextWarningThreshold { get; set; } = 0.8;

    /// <summary>
    /// The display label for whoever userId actually is: the operator's own
    /// name, or the name configured for them in AllowedUsers or AdminUsers.
    /// Falls back to OperatorName for an unrecognized id so callers never
    /// need to null-check — in practice this is only ever called with an id
    /// that already passed DiscordChannel's whitelist gate.
    /// </summary>
    public string DisplayNameFor(ulong userId) =>
        userId == OperatorUserId
            ? OperatorName
            : AllowedUsers.FirstOrDefault(u => u.UserId == userId)?.Name
              ?? AdminUsers.FirstOrDefault(u => u.UserId == userId)?.Name
              ?? OperatorName;
}

public sealed class AllowedContact {
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class GoalConfig {
    public int DefaultRenewDays { get; set; } = 21;
    public int LongitudinalRenewDays { get; set; } = 90;
    /// <summary>If the stack is entirely instrumental, the system has become a task queue in costume.</summary>
    public int MinUselessGoals { get; set; } = 1;
}

public sealed class FeedConfig {
    public string? GitHubUser { get; set; }
    public string? NewsFeedUrl { get; set; }
    public string? SocialFeedUrl { get; set; }
    public string WatchedDirectory { get; set; } = "/agent/observations/dropbox";
    /// <summary>AI-identity discourse is contagious. Cap it. See design doc section 8.</summary>
    public int SocialFeedMaxItemsPerIntake { get; set; } = 10;
    public int SocialFeedIntakesPerWeek { get; set; } = 3;
}

public sealed class ChoreConfig {
    /// <summary>Ceiling on chore ticks per day. A ceiling, not a quota to fill — same spirit as MaxWorkTicksPerDay.</summary>
    public int MaxChoresPerDay { get; set; } = 8;
    /// <summary>Floor on how often a single chore may recur. Faster than this isn't a chore, it's the pulse's job.</summary>
    public double MinIntervalHours { get; set; } = 1;
}
