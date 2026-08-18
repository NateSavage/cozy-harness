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
    : new DiscordChannel(cfg.Channel.DiscordToken!, cfg.Channel.OperatorUserId, activity);

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
    TickType.Reply         => new ReplyTick(mainLlm, db, context, channel, people, cfg.Llm, cfg.Channel),
    TickType.Chore         => new ChoreTick(mainLlm, db, context, chores, cfg.Llm, activity),
    _ => throw new ArgumentOutOfRangeException(nameof(tickType)),
};

AgentClock clock = new(cfg.Schedule, cfg.OperatorTimeZone);
TickScheduler scheduler = new(clock, cfg.Schedule, cfg.Chores, db, runner, TickFactory, activity, errors);

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

channel.MessageReceived += async content => {
    db.AddMessage("in", cfg.Channel.OperatorName, content);
    await scheduler.HandleInboundAsync(cts.Token);
};
await channel.StartAsync(cts.Token);

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
