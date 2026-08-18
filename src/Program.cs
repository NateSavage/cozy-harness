using System.Text.Json;
using CozyHarness.Channels;
using CozyHarness.Chores;
using CozyHarness.Config;
using CozyHarness.Core;
using CozyHarness.Domain;
using CozyHarness.Feeds;
using CozyHarness.Goals;
using CozyHarness.Llm;
using CozyHarness.People;
using CozyHarness.Retrieval;
using CozyHarness.Storage;
using CozyHarness.Ticks;

var configPath = args.FirstOrDefault(a => a.EndsWith(".json")) ?? "agent.json";
var cfg = File.Exists(configPath)
    ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
    : new AgentConfig();

// The bot token never goes through agent.json (it would land in the
// world-readable Nix store — see the module's discordTokenFile option). It's
// delivered instead via systemd's LoadCredential, mounted at
// $CREDENTIALS_DIRECTORY/discord-token — the same mechanism the model-download
// service already uses. Bridge it into config here; if it's absent (no
// CREDENTIALS_DIRECTORY, e.g. running outside the unit) Channel.DiscordToken
// stays empty and Program falls back to ConsoleChannel below, same as today.
var credentialsDir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
if (credentialsDir is not null) {
    var tokenPath = Path.Combine(credentialsDir, "discord-token");
    if (File.Exists(tokenPath))
        cfg.Channel.DiscordToken = File.ReadAllText(tokenPath).Trim();

    // Same delivery mechanism, second credential — see ChannelConfig.AdminDiscordToken.
    var adminTokenPath = Path.Combine(credentialsDir, "discord-admin-token");
    if (File.Exists(adminTokenPath))
        cfg.Channel.AdminDiscordToken = File.ReadAllText(adminTokenPath).Trim();
}

AgentTree tree = new(cfg.TreeRoot);
tree.EnsureLayout();

GitStore git = new(cfg.TreeRoot, cfg.MirrorRemote, cfg.EnableGit);
git.EnsureRepo();

using IndexDb db = new(tree.Abs("index.sqlite"));

// The index is derived. Rebuilding on every start is cheap and means a corrupt
// index is never a problem you have to think about.
var report = new IndexRebuilder(tree, db).Rebuild();
Console.WriteLine($"[index] {report.Episodes} episodes, {report.Goals} goals, {report.Chores} chores");
foreach (var m in report.Malformed) Console.Error.WriteLine($"[index] malformed: {m}");

if (args.Contains("--rebuild-only")) return;

var http = new HttpClient();
using var mainLlm = new LlamaClient(cfg.Llm.MainSocketPath, cfg.Llm.TopP, cfg.Llm.TopK, cfg.Llm.Stop);
using var pulseLlm = new LlamaClient(cfg.Llm.PulseSocketPath, cfg.Llm.TopP, cfg.Llm.TopK, cfg.Llm.Stop);

ContextBuilder context = new (tree, db);
GoalStore goals = new (tree, db, cfg.Goals);
ChoreStore chores = new (tree, db, cfg.Chores);
PeopleStore people = new (tree, db);
people.EnsurePerson(cfg.Channel.OperatorName, cfg.Channel.OperatorName, isOperator: true);

// What the agent is doing right now, if anything — shared between the
// scheduler (sets it) and the channel (shows it, and can interrupt it).
AgentActivity activity = new();

IOperatorChannel channel = string.IsNullOrEmpty(cfg.Channel.DiscordToken)
    ? new ConsoleChannel(activity)
    : new DiscordChannel(cfg.Channel.DiscordToken!, cfg.Channel.OperatorUserId,
                          cfg.Channel.AllowedUsers.Select(u => u.UserId),
                          cfg.Channel.AdminUsers.Select(u => u.UserId), activity);

// Optional — see ChannelConfig.AdminDiscordToken. Null means admin commands
// stay on `channel` above, same as before this existed; RegisterAdminCommandAsync
// below is what actually decides where each one lands, so nothing else needs
// to branch on this.
IAdminChannel? adminChannel = string.IsNullOrEmpty(cfg.Channel.AdminDiscordToken)
    ? null
    : new DiscordAdminChannel(cfg.Channel.AdminDiscordToken!, cfg.Channel.OperatorUserId,
                               cfg.Channel.AdminUsers.Select(u => u.UserId));

var feeds = new List<IFeed> { new WatchedDirectoryFeed(cfg.Feeds.WatchedDirectory) };
if (!string.IsNullOrEmpty(cfg.Feeds.GitHubUser))
    feeds.Add(new GitHubFeed(http, cfg.Feeds.GitHubUser!));

ErrorReporter errors = new(channel);
TickRunner runner = new(tree, git, db, errors);

ITick TickFactory(TickType tickType) => tickType switch {
    TickType.Pulse         => new PulseTick(pulseLlm, db, context, cfg.Llm, cfg.Schedule.MaxWorkTicksPerDay),
    TickType.Work          => new WorkTick(mainLlm, db, context, goals, channel, cfg.Llm, activity),
    TickType.Intake        => new IntakeTick(mainLlm, db, context, goals, feeds, cfg.Llm),
    TickType.ReflectDaily  => new ReflectTick(false, mainLlm, db, context, goals, tree, cfg.Llm),
    TickType.ReflectWeekly => new ReflectTick(true, mainLlm, db, context, goals, tree, cfg.Llm),
    TickType.Chore         => new ChoreTick(mainLlm, db, context, chores, cfg.Llm, activity),
    _ => throw new ArgumentOutOfRangeException(nameof(tickType)),
};

// Reply gets its own factory, not a case in TickFactory above: it's the only
// tick that needs to know who to answer — the sender, whitelisted or the
// operator, routed back to their own DM channel (see DiscordChannel.ReplyToAsync).
// Everything else about the reply (history, logging, the prompt itself)
// still treats the conversation as the operator's, per AllowedUserIds' scope.
ITick ReplyFactory(ulong replyToUserId) =>
    new ReplyTick(mainLlm, db, context, channel, people, cfg.Llm, cfg.Channel, replyToUserId);

AgentClock clock = new(cfg.Schedule, cfg.OperatorTimeZone);
TickScheduler scheduler = new(clock, cfg.Schedule, cfg.Chores, cfg.Channel, db, runner, TickFactory, ReplyFactory, channel, activity, errors);

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// The operator's name is fixed by config, never auto-tracked — see
// PeopleStore's class remarks on why whitelisted others are handled
// differently (they have no pre-existing history to disturb; the operator
// does). ReplyTick resolves the same way for its own logging.
string ResolveContactName(ulong userId, string discordDisplayName) {
    if (userId == cfg.Channel.OperatorUserId) return cfg.Channel.OperatorName;
    people.SyncDiscordName(userId.ToString(), discordDisplayName);
    return people.CurrentName(userId.ToString(), cfg.Channel.DisplayNameFor(userId));
}

channel.MessageReceived += async (userId, discordName, content) => {
    db.AddMessage("in", ResolveContactName(userId, discordName), content, userId.ToString());
    await scheduler.HandleInboundAsync(userId, cts.Token);
};

// Every admin command goes through here rather than calling
// channel.RegisterCommandAsync directly, so this is the one place that
// decides where "/admin ..." actually lives: the dedicated admin bot when
// AdminDiscordToken is configured, otherwise `channel` itself — today's
// behavior, unchanged, for deployments that haven't set up a second bot
// (or are running ConsoleChannel locally). Command names and handlers are
// identical either way.
Task RegisterAdminCommandAsync(string name, string description, Func<ulong, CancellationToken, Task<string>> handler) =>
    adminChannel is not null
        ? adminChannel.RegisterCommandAsync(name, description, handler)
        : channel.RegisterCommandAsync(name, description, handler);

await RegisterAdminCommandAsync("goals", "List active goals", (_, ct) => {
    var goals = db.ActiveGoals();
    var text = goals.Count == 0
        ? "No active goals."
        : string.Join("\n", goals.Select(g =>
            $"**{g.Title}** ({g.Kind}, `{g.Id}`) — last touched " +
            (g.LastTouched is { } lt && DateTimeOffset.TryParse(lt, out var d) ? ContextBuilder.Ago(d) : "never")));
    return Task.FromResult(text);
});

// Whitelisted, not admin: anyone allowed to DM the agent at all is allowed
// to check this — see IOperatorChannel.RegisterWhitelistedCommandAsync.
await channel.RegisterWhitelistedCommandAsync("context", "Show context window usage per tick type", ct => {
    var mainCapacity = cfg.Llm.MainContextSize / Math.Max(1, cfg.Llm.MainSlots);
    var mainTicks = cfg.Llm.Slots.Where(kv => kv.Key != "pulse").ToList();

    // Detected from the actual slot assignment, not a separate "is MTP on"
    // flag — C# config has no such flag, only its effect (module.nix's
    // enableMtp forces every main tick type onto llama.cpp's single required
    // parallel slot). If every main tick type happens to land on the same
    // slot, they share one window and should be shown as one, whatever
    // caused that.
    var collapsed = mainTicks.Select(kv => kv.Value).Distinct().Count() <= 1;

    string Line(string label, LlamaClient client, int slot, int capacity) {
        var used = client.LastKnownUsage(slot);
        return used is null
            ? $"**{label}** — no completions yet this run"
            : $"**{label}** — {used}/{capacity} tokens ({used * 100 / capacity}%)";
    }

    var lines = new List<string> {
        Line("pulse", pulseLlm, cfg.Llm.Slots["pulse"], cfg.Llm.PulseContextSize),
    };

    if (collapsed)
    {
        // The MTP case: one clearly-labeled line for the single shared
        // window (mainCapacity is already the full context here, since
        // MainSlots is forced to 1), not a jumbled comma-list that reads
        // like just another grouped slot among several.
        var names = string.Join(", ", mainTicks.Select(kv => kv.Key).OrderBy(n => n));
        lines.Add(Line($"{names} — shared window (MTP)", mainLlm, mainTicks[0].Value, mainCapacity));
    }
    else
    {
        // One line per tick type, not grouped by physical slot: chore does
        // still share slot 0 with work (see LlmConfig.Slots) and will show
        // the same number, but each gets its own line instead of being
        // merged behind one label.
        foreach (var (tick, slot) in mainTicks.OrderBy(kv => kv.Key))
            lines.Add(Line($"{tick} (slot {slot})", mainLlm, slot, mainCapacity));
    }

    return Task.FromResult(string.Join("\n", lines));
});

// "debug context", not "debug-context" or "context-dump": the space is
// meaningful — DiscordChannel reads it as "nest this under a 'debug'
// subcommand group" (Discord doesn't allow spaces in a single subcommand
// name, so a literal `/admin debug context` invocation requires that
// nesting). See IOperatorChannel.RegisterCommandAsync.
await RegisterAdminCommandAsync("debug context", "Dump the exact prompt built for your current conversation", (invokerId, ct) => {
    // Constructing the real ReplyTick (rather than reassembling the prompt
    // by hand here) is what guarantees "exact" — same class, same fields,
    // same BuildPrompt the real reply path calls, just via the preview
    // method instead of RunAsync so nothing is sent and no LLM call happens.
    //
    // invokerId, not cfg.Channel.OperatorUserId: with more than one admin,
    // hardcoding the operator here meant every admin's "debug context" dumped
    // the OPERATOR's conversation — including its content — regardless of
    // who asked. RecentConversation is scoped per contactId (see IndexDb),
    // so this must be scoped the same way: whoever actually typed the
    // command sees their own conversation, never someone else's.
    var tick = new ReplyTick(mainLlm, db, context, channel, people, cfg.Llm, cfg.Channel, invokerId);
    return Task.FromResult(tick.BuildPromptPreview());
});

await RegisterAdminCommandAsync("chores", "List active chores", (_, ct) => {
    var chores = db.ActiveChores();
    var text = chores.Count == 0
        ? "No active chores."
        : string.Join("\n", chores.Select(c =>
            $"**{c.Title}** (`{c.Id}`) — due " +
            (DateTimeOffset.TryParse(c.DueBy, out var d) ? ContextBuilder.Ago(d) : c.DueBy)));
    return Task.FromResult(text);
});

await channel.StartAsync(cts.Token);
if (adminChannel is not null) await adminChannel.StartAsync(cts.Token);

Console.WriteLine($"[harness] running. tree={cfg.TreeRoot} tz={cfg.OperatorTimeZone}");

// Waited for here, not in the Nix unit's preStart: the process is already
// "started" as far as systemd is concerned (Discord's even connected), so a
// slow CPU model load no longer has any startup timeout to race against.
// This only exists to keep the first post-boot ticks from being a foregone
// SocketException — see WaitForHealthyAsync.
await Task.WhenAll(
    mainLlm.WaitForHealthyAsync("llama-main", cts.Token),
    pulseLlm.WaitForHealthyAsync("llama-pulse", cts.Token));
Console.WriteLine("[harness] llm servers ready.");

await scheduler.RunAsync(cts.Token);

if (channel is IAsyncDisposable disposableChannel)
    await disposableChannel.DisposeAsync();
if (adminChannel is IAsyncDisposable disposableAdminChannel)
    await disposableAdminChannel.DisposeAsync();
