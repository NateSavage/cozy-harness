# Cozy Harness

C# implementation of the memory engine design. .NET 8, Linux.

## Shape

```
src/
  Program.cs          composition root
  Core/               clock, scheduler (jitter, backpressure), current-activity + interrupt, error reporting
  Storage/            tree layout, frontmatter, git, derived SQLite index
  Domain/             Episode, Goal, Belief, Person + Provenance
  Goals/              lifecycle; state is the directory, mv is the transition
  Chores/             routine tasks; due-based, not pulse-judged
  People/             provenance-based privacy
  Retrieval/          budget filling with cache-aware ordering
  Llm/                llama-server client, warm slots
  Ticks/              pulse, work, intake, reflect, reply, chore
  Channels/           operator line; console stand-in, Discord shell
  Feeds/              github, watched directory
```

See [`docs/tick-cycle.md`](docs/tick-cycle.md) for how these actually run —
the scheduling priority, backpressure, and jitter, traced through the code.

## Running

```bash
./run-servers.sh &            # two persistent llama-servers (Gemma 4 26B-A4B + E4B)
dotnet run -- agent.json      # ConsoleChannel unless agent.json sets discordToken
dotnet run -- --rebuild-only  # regenerate the index from the tree
```

Start with `discordToken: null`. Step 1 of the build order is a pulse-only agent
and a `git log`, and you don't want to be debugging a gateway while reading it.

## Where the design lives in the code

Design decisions that would be easy to sand off by accident, and where they are:

| Decision | Location |
|---|---|
| Idleness is free — no episode written at all | `TickOutcome.Nothing`, `PulseTick` |
| Work ticks can't resolve by asking the operator | `WorkTick.RunAsync` — the message sends, the tick still writes |
| Closing a goal requires a reason | `GoalStore.Transition` throws without one |
| Renewal is never automatic | `GoalStore.Renew` is only called on explicit `renew: true` |
| Goal state is the filesystem | `Goal.DirectoryFor`, `GoalStore.ParseGoalFile` (directory wins over frontmatter) |
| Chores are due-based, never pulse-judged | `TickScheduler.OneCycleAsync` checks `DueChores` directly, no LLM call to decide |
| Retiring a chore requires a reason | `ChoreStore.Retire` throws without one |
| Stable prefix first, for KV reuse | `ContextBuilder.BeginStable` / `PromptParts` |
| Privacy follows provenance | `PersonFact.MayTravelTo` |
| Operator told when sensitive | `ReplyTick` → `IOperatorChannel.NotifySensitiveAsync` |
| A failed reply still says *something* | `ReplyTick.RunAsync` → `SayStuckAsync` rather than leaving the operator in silence |
| Discord user ID of 0 fails fast, not silently | `DiscordChannel`'s constructor — a real snowflake is never 0 |
| Talked to over DM, never a guild channel | `DiscordChannel.OnGatewayMessageAsync` drops anything not `IDMChannel`; guild participation is intentionally out of scope for now |
| Reply is not work | `TickScheduler.HandleInboundAsync` skips the heavy lock |
| An interrupt cancels one tick, never the agent's life | `AgentActivity.Begin` links a per-tick token; `TickRunner` never rethrows `OperationCanceledException` |
| An interrupt is never reported as a failure | `TickRunner.RunAsync`'s cancellation branch doesn't call `ErrorReporter` — only the generic-exception branch does |
| Shutdown between cycles still runs cleanup | `TickScheduler.RunAsync` — `Task.Delay` has its own catch, or `Program.cs`'s post-loop `DisposeAsync` would never run |
| A notification failure can't cause a second failure | `ErrorReporter.Report` is fire-and-forget with its own swallowed try/catch |
| Backpressure over queueing | `TickScheduler.OneCycleAsync` |
| Index is derived | `IndexRebuilder`, rerun on every start |

## Known gaps

- No news or social feed yet; `IFeed` is the seam.
- No guild chat — deliberate scope, not an oversight. DM-only for now; every
  guild message is dropped unread in `OnGatewayMessageAsync`. See DESIGN.md §9.
- `SayStuckAsync` now fires as `ReplyTick`'s fallback when a reply fails to
  parse, but the model still has no way to invoke it itself — no field in any
  tick's JSON reaches it yet. That's still worth adding, probably in the work
  tick.
- Beliefs are modelled but no tick writes them yet.
- Rollup consolidation (design §7) not implemented.
- Token estimate is `length / 4`. Fine to start; swap for the tokenizer endpoint
  if budgets start mattering.
- Third-party outbound gating (`PeopleStore.Blockers`) is written but unused —
  needed before anyone but the operator can talk to it.
- Everything that actually touches Discord's gateway — the Interrupt/Let it
  finish buttons, and now DM resolution (`StartAsync`'s `GetUserAsync` /
  `CreateDMChannelAsync`, the `IDMChannel` + author-ID filter in
  `OnGatewayMessageAsync`) — is untested against a real Discord server. The
  scheduling/activity/error-reporting logic around it is verified end-to-end
  with fake channels and ticks; Discord.Net's own socket entities aren't
  something that can be constructed outside a real gateway session.

## First week

Run it with no goals, no chores, no feeds, console channel. Watch `git log --oneline`
fill with pulses that decided nothing needed doing. That's the system working.
