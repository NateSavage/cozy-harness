# The tick cycle

How the harness actually spends its time, traced through the code. `DESIGN.md`
§4 and §7 explain *why* it's shaped this way; this is the *how*, grounded in
the real call chain, for whoever next needs to change it without reading every
file first.

There is exactly one loop and one fast path in:

- **The loop** — `TickScheduler.RunAsync`, started once from `Program.cs` and
  never exited except on cancellation. Runs `OneCycleAsync`, sleeps, repeats.
- **The fast path** — `TickScheduler.HandleInboundAsync`, fired by
  `IOperatorChannel.MessageReceived`. Nothing about it waits for the loop.

Everything below is what happens inside those two entry points, plus two ways
things reach the operator that aren't a reply: the operator can cancel
whatever heavy tick is running via `AgentActivity` ("Interruption" below), and
failures reach them as a DM via `ErrorReporter` ("Error reporting" below). (Two
different things share the word "interrupt" here — the fast path *interrupts
the pulse cycle* to get a reply out quickly, while `AgentActivity.TryInterrupt`
*cancels a running tick*. They're unrelated; a reply always happens regardless
of the latter.)

---

## One cycle, in priority order

`OneCycleAsync` is a strict waterfall — the first check that matches wins and
the rest are skipped for this cycle. Nothing here is weighted or scored;
scheduled ticks simply outrank anything the pulse might want:

1. **Weekly reflect**, if `AgentClock.IsWeeklyReflectHour()` and it hasn't
   already run today.
2. **Daily reflect**, same shape, checked second so weekly wins on the one
   evening they'd otherwise collide.
3. **Intake**, if `IsIntakeHour()` (morning or evening, config-set) and more
   than 6 hours have passed since the last one — the hour check alone would
   double-fire across the whole `:00`–`:05` window.
4. **A due chore**, if today's chore count is under `maxChoresPerDay` and
   `IndexDb.DueChores` returns anything. No model call decides this one;
   due-ness is arithmetic (`Chore.DueBy = (LastRun ?? Created) + Interval`),
   so there's nothing to judge.
5. **Backpressure gate** — if the heavy lock is already held (see below), the
   cycle ends here. The pulse doesn't even run.
6. **The pulse**, always, if nothing above fired. Cheap outs first (no active
   goals and nothing unread → skip without a model call); otherwise one small
   LLM call, answering `nothing` / `work` / `intake` / `reflect`. Every one of
   these outcomes is `Silent` (see below) — a wake decision never itself
   appears in `git log`, only whatever it woke does.
7. **Quiet hours gate** — if it's quiet hours and the pulse woke `work`, that
   wake is dropped. Everything else the pulse can wake (`intake`, `reflect`)
   is *not* gated here — quiet hours only ever blocks new work.
8. Whatever the pulse woke, run it.

Steps 1–4 run regardless of quiet hours or the pulse's opinion; only step 7
(work specifically, woken from the pulse) is quiet-hours-gated. A chore due at
3am runs at 3am.

```
 weekly reflect due? ──yes──▶ run it
        │no
 daily reflect due?  ──yes──▶ run it
        │no
 intake due (>6h)?   ──yes──▶ run it
        │no
 a chore due,
 under today's cap?  ──yes──▶ run it
        │no
 heavy lock held?    ──yes──▶ do nothing this cycle
        │no
      pulse ──"nothing"──▶ do nothing this cycle
        │"work"/"intake"/"reflect"
 quiet hours && wake=="work"? ──yes──▶ do nothing this cycle
        │no
      run what it woke
```

---

## Backpressure: the heavy lock

`_heavyLock` is a `SemaphoreSlim(1, 1)` — a binary lock, not a queue.
`RunHeavy` tries to take it with `WaitAsync(0)`: zero timeout, so a tick that
can't get the lock returns immediately instead of waiting in line. If a work
tick is still running when the next cycle wakes up, that cycle just does
nothing and tries again next interval.

This is deliberate: on CPU, a queue of backed-up ticks becomes a spiral where
each one prefills a colder cache than the last. Skipping is cheap; queueing
compounds. Everything routed through `RunHeavy` — reflect, intake, chore, and
whatever the pulse wakes — competes for this one lock. The pulse itself does
not take it (step 6 runs even if a chore *just* released it), which is how a
pulse can observe the world again immediately after a heavy tick finishes.

`Reply` is the one tick that never touches this lock at all (see below).

**The token each heavy tick actually runs on is not `ct`.** `RunHeavy` gets a
tick-scoped token from `AgentActivity.Begin(type, ct)` — linked to the real
`ct`, but cancellable on its own via `AgentActivity.TryInterrupt()` without
touching `ct` itself. This is deliberate and load-bearing: see "Interruption"
below for why sharing `ct` directly would be a bug, not just a missed feature.

**A sharp edge:** the weekly/daily/intake "already ran" trackers (`_lastWeeklyReflect`
etc.) are set *before* `RunHeavy` is awaited, not after it confirms the lock.
If the lock happens to be held at the exact cycle a scheduled tick's window
opens, `RunHeavy` no-ops — but the tracker is still marked done, so it won't
be retried later that window. Weekly reflect's window is `Minute < 5` on one
specific hour and day, so this is the one worth actually caring about: a heavy
tick in flight at just the wrong two minutes skips that week's reflect
entirely, silently.

---

## Jitter and the interval

Between cycles, `RunAsync` sleeps for `NextInterval()`:

```
base = quiet hours ? quietPulseIntervalSeconds : pulseIntervalSeconds
next = base × (1 + random(-pulseJitter, +pulseJitter))
```

Defaults: 120s base, ±20% jitter, 900s base during quiet hours. The jitter
exists so the pulse never locksteps with itself or with anything cron-shaped;
it is re-rolled every single cycle, not fixed at startup.

Quiet hours (`AgentClock.IsQuietHours`) do two things, and only two: they
lengthen this interval, and they gate the `work` wake in step 7 above. They do
not change what daily/weekly reflect, intake, or chores do — those already
have their own hour-of-day or interval gating and run on schedule regardless.

---

## The fast path: replies

`HandleInboundAsync` is called directly from `Program.cs`'s
`channel.MessageReceived` handler, on its own task, outside `OneCycleAsync`
entirely. It runs `Reply` through `_runner` but skips `RunHeavy` — no lock
acquisition, no backpressure check. A reply can start while a work tick is
mid-flight, because a reply isn't work: it doesn't serve a goal and it doesn't
write to whatever the heavy tick is in the middle of.

This is also why `WorkTick` cannot resolve by messaging the operator and
waiting — a reply and a work tick can be genuinely concurrent, so there's no
turn for the work tick to wait its turn on.

---

## What running a tick actually does

Every tick, however it got picked, ends up in `TickRunner.RunAsync`, which
wraps it the same way regardless of type:

1. Run the tick. Nothing thrown from inside it propagates out of `RunAsync` —
   both paths become a normal `TickOutcome` instead:
   - `OperationCanceledException` (an interrupt, or the app shutting down mid-tick)
     → `"{type} tick was interrupted before finishing"`, salience 0.3. **Not**
     reported to the operator — an interrupt they just asked for isn't a failure.
   - anything else → `"tick failed: {exception type}"`, salience 0.9, the
     exception trace as the body — **and** `ErrorReporter.Report(...)`, which
     is what actually gets a DM out (see "Error reporting" below).

   Neither case stops the loop — a tick that crashed or got cut short is
   recorded like anything else, same as the pulse's "nothing" being a genuine
   answer rather than a failure to find something.
2. If `outcome.Silent`, stop here — nothing is written. This is how the pulse
   answering "nothing" stays genuinely free rather than merely cheap: no
   episode file, no commit, no index row, nothing to ever retrieve later.
3. Otherwise: write the episode file (`AgentTree.WriteEpisode`), commit it
   (`GitStore.CommitTick` — one commit per tick, message `"{type}: {summary}"`),
   upsert it into `episodes` (`IndexDb.UpsertEpisode`), push the mirror.

Steps 2 and 3 are the same order-of-operations for every tick type — there is
no special case anywhere in `TickRunner` for work vs. reflect vs. chore. What
differs between tick types is entirely upstream, in what each `ITick.RunAsync`
puts into the `TickOutcome` it hands back.

---

## Interruption

`AgentActivity` (in `Core/`) is what `RunHeavy` reports itself into and reads
back out of — the one shared record of "is anything heavy running, and what."
Only heavy ticks show up here; the pulse and replies run without the heavy
lock and finish fast enough that there's nothing meaningful to interrupt.

- `RunHeavy` calls `AgentActivity.Begin(type, ct)` before running the tick,
  which hands back a `CancellationTokenSource` **linked to** `ct` but
  independently cancellable. The tick runs on `tickCts.Token`, not `ct`
  directly. A tick that reaches a point where it knows more (`WorkTick` once
  it's loaded a goal, `ChoreTick` once it's loaded a chore) calls
  `AgentActivity.SetDetail(...)` to sharpen the summary text.
- `AgentActivity.TryInterrupt()` cancels that token. Whatever the tick was
  awaiting — almost always the `LlamaClient` HTTP call — unwinds as an
  `OperationCanceledException`, caught by `TickRunner` (see above) and
  recorded as an interrupted-tick episode.
- **Why a linked token and not just `ct` itself:** every `OperationCanceledException`
  that reaches a `catch (OperationCanceledException)` clause looks identical
  to the catcher — there's no way to ask "which token caused this" from the
  exception alone. Before this existed, `TickRunner` rethrew on cancellation,
  which was fine when the only source of cancellation was real app shutdown.
  Reusing `ct` directly for interrupts would mean an operator clicking
  "Interrupt" throws an exception that unwinds all the way to
  `TickScheduler.RunAsync`'s own `catch (OperationCanceledException) { break; }`
  — silently ending the entire scheduler loop over one button click. The
  linked-token split, plus `TickRunner` no longer rethrowing at all, is what
  makes an interrupt just *that tick* ending early rather than the agent's
  whole life ending early.
- A tick that gets interrupted before it reaches its own writes (goal
  transition, `ChoreStore.MarkRun`, marking observations consumed — all of
  which happen only after a successful model response in every current tick)
  leaves no partial state behind. It's simply retried whenever it's next due.

**What a channel does with this:** `DiscordChannel` sends a one-off notice —
"Right now I'm {`AgentActivity.Summary()`}" — with *Interrupt* / *Let it
finish* buttons whenever a message arrives while `CurrentTick` is non-null.
The reply itself is not gated on this choice; it happens either way, since
`Reply` never took the heavy lock to begin with (see "The fast path" above).
"Let it finish" is really just acknowledging that. `AgentActivity.Changed`
also drives the bot's Discord presence for as long as a heavy tick runs, the
same mechanism `ProcessInboxAsync` already used just for "a reply taking
shape" — the two don't coordinate, so presence can flicker if both are live
at once, which costs nothing. `ConsoleChannel` has the same busy notice
(typed `/interrupt` in place of a button) for testing this without Discord.

---

## Error reporting

`ErrorReporter` (`Core/ErrorReporter.cs`) is the one thing every failure path
funnels through to actually get a message out, as opposed to just sitting in
the episode log or stderr. It wraps a single method:

```csharp
void Report(string context, Exception ex);
```

Fire-and-forget by design — `Report` returns immediately, runs the actual send
on its own `Task.Run`, and swallows anything that send throws. Both halves
matter: a caller is typically *already inside an exception handler* (the
scheduler loop, a tick that just crashed) and can't afford to block on a
Discord round trip there, and a notifier that can itself throw would turn
"something broke" into a second, unhandled thing breaking.

`Report` calls `IOperatorChannel.NotifyErrorAsync`, so each channel formats it
its own way — `DiscordChannel` wraps it in a code fence (capped at ~3500
chars; `SendAsync`'s chunker would happily split a longer one across many
messages, which is more spam than signal) and sends it through the same
`SendAsync` path as everything else, so it's mention-safe and gets chunked
like any other message. `ConsoleChannel` just prints it.

**Three call sites, three non-overlapping failure classes:**

| Where | What it catches | Context string |
|---|---|---|
| `TickRunner.RunAsync` | the tick's own logic throwing (not `OperationCanceledException` — see above) | `"{type} tick failed"` |
| `TickScheduler.RunAsync`'s outer catch | anything that escapes a *whole cycle* — episode/git/index writes, a bug in the scheduling logic itself | `"a scheduler cycle failed"` |
| `DiscordChannel.ProcessInboxAsync` / `ConsoleChannel`'s read loop | the inbound-message path failing *outside* of running a tick (e.g. `db.AddMessage`) | `"handling your message failed"` |

These don't overlap: `TickRunner` swallows tick failures itself, so they never
reach `TickScheduler`'s catch; a `Reply` tick failure is caught inside
`TickRunner` (via `HandleInboundAsync` → `_runner.RunAsync`), not inside the
channel's own catch, which only wraps what's around that call.

**What doesn't get reported, on purpose:** interrupts (see above); anything
that throws *before* a channel exists to report through — a bad token, a bad
`OperatorUserId`, `llama-server` unreachable at startup — which just crash
the process today, the same as before this existed. There's no way to DM
about a failure to establish the DM.

---

## Quick reference

| Tick | What triggers it | Through `RunHeavy`? (= interruptible) | Can be silent? |
|---|---|---|---|
| `pulse` | every cycle, unless something above preempted it | no | yes — its whole point |
| `work` | pulse wakes it, and it isn't quiet hours | yes | no |
| `intake` | clock: due hour, >6h since last | yes | no |
| `reflect` (weekly/daily) | clock: due hour, not already run today | yes | no |
| `reflect` (daily only) | pulse wakes it — the pulse never wakes weekly | yes | no |
| `chore` | `IndexDb.DueChores` non-empty, under the daily cap | yes | no |
| `reply` | inbound message, any time | **no** — the one exception | no |

"Can be silent" here means *can this tick type write no episode at all*, not
just a low-salience one. Only the pulse does this (`TickOutcome.Nothing`),
which is what makes "nothing needed attention" a free answer rather than one
that quietly accumulates cost in the index.
