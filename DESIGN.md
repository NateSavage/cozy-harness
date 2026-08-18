# A Memory Engine for a Small Persistent Agent — v2

An always-on agent with durable memory, self-authored goals, relationships, and
a felt sense of elapsed time. Runs on CPU, on modest hardware, with no
requirement that it earn its keep.

*Changes from v1: filesystem is now ground truth and SQLite is a derived index;
git provides history and the introspection tooling; the operator is
near-always-available on Discord, which inverts the risk from waiting to
dependence; added people and a provenance-based privacy model; timing rebuilt
around persistent warm KV slots.*

---

## 0. Assumptions

| Assumption | Why it matters | If wrong |
|---|---|---|
| ~32GB dedicated to resident models + KV slots | Enables warm caches, which set every number in §7 | Fewer slots; longer ticks; slower cadence |
| Retrieval starts keyword + recency; embeddings optional | Simplest thing that works; the agent can `grep` | Add `sqlite-vec` + small embedder — hook in §6 |
| Operator lives on Discord, replies fast | Risk is dependence, not waiting (§9) | Revert to v1's silence-handling |
| One operator; other people arrive later | Operator has total read access, permanently (§10) | Needs a real permissions model |

---

## 1. Principles

1. **Append-only.** Nothing is destroyed. Revision supersedes; both persist.
   Git enforces this rather than application code.
2. **Idleness is legitimate.** A tick that does nothing is a success. No
   structure may punish it. Still the most important rule here.
3. **Abandonment is a terminal success state.** Dropping a goal with a written
   reason is a completed act, not a failure and not silent rot.
4. **The filesystem is ground truth.** SQLite is a derived index, rebuildable
   from the tree at any time. If the index corrupts, replay.
5. **Native tools over bespoke APIs.** A small model is far better at `grep`,
   `cat`, and `git log` than at any custom JSON interface. Design so it can use
   what it already knows.
6. **The world is not the agent.** Self-reference is available on request,
   never force-fed.
7. **Privacy follows provenance, not content.** Where a fact came from is
   mechanical to record; whether it's sensitive is not. See §10.

---

## 2. Layout

```
/agent
  self/
    model.md              # ~500 words, rewritten rarely, git-versioned
    situation.md          # standing facts: operator can read everything, etc.
  goals/
    active/0042-tern-counts.md
    dormant/  abandoned/  done/
  chores/
    active/0003-check-mirror.md
    retired/
  episodes/2026/08/06-1430-work.md
  beliefs/
    <topic>.md            # each claim: confidence, support, superseded-by
  people/
    kira/
      profile.md          # from:public
      learned.md          # from:kira — not repeatable by default
      observed.md         # from:self
      log/2026-08-06.md
  observations/           # raw intake; the only prunable thing
  inbox/  outbox/         # discord as files
  harness/                # its own source, symlinked, readable on request
  index.sqlite            # derived, gitignored
```

**Goal state is the directory.** `mv` is the state transition; git records it
as a rename. The lifecycle becomes filesystem-native and legible with tools the
model already has.

**One commit per tick.** Commit message = the episode summary. This gives you,
for free:

- `git log --oneline` — the timeline view
- `git log -p self/model.md` — the self-model diff viewer
- `git log --diff-filter=R goals/` — every state transition ever
- `git log --since=1.week goals/abandoned/` — what it let go of, and why

Almost all of v1's "build these views" work disappears into tooling that
already exists.

**Mirror to a bare repo the agent cannot write to.** `rm` is recoverable from
git; a force-push or aggressive `gc` is not. With an off-site mirror it can be
completely free with its own tree, which is the point.

---

## 3. The index

SQLite, derived, rebuilt by walking the tree. Holds only what `grep` can't do:
recency/salience scoring, goal metadata for fast pulse checks, and message
state.

```sql
CREATE TABLE episodes (
  path TEXT PRIMARY KEY, ts TEXT, tick_type TEXT, summary TEXT,
  did_nothing INT DEFAULT 0, goal TEXT, salience REAL DEFAULT 0.5,
  commit_sha TEXT
);
CREATE TABLE goals (
  path TEXT PRIMARY KEY, title TEXT, state TEXT, kind TEXT,
  created_ts TEXT, renew_by TEXT, last_touched TEXT, closed_why TEXT
);
CREATE TABLE chores (           -- routine tasks, not goals: no kind, no renewal
  id TEXT PRIMARY KEY, path TEXT, title TEXT, state TEXT,
  due_by TEXT, created_ts TEXT
);
CREATE TABLE facts (            -- provenance index over people/
  id INTEGER PRIMARY KEY, person TEXT, source_class TEXT,  -- public|learned|self
  path TEXT, ts TEXT
);
CREATE TABLE messages (
  id INTEGER PRIMARY KEY, ts TEXT, direction TEXT, person TEXT,
  content TEXT, episode_path TEXT
);
```

Rebuild script must be runnable at any time and is the first thing to write
after the schema, because you will need it.

---

## 4. Tick types

Five rhythms differing in *kind of thinking*, not just frequency.

### `pulse` — small model, ~1K context, warm slot
Only question: *does anything need attention?* Loads unconsumed observation
count, unread messages, goals past `renew_by`. Answers `nothing` / `wake work`
/ `wake intake` / `wake reflect`.

Most pulses should answer `nothing`. That is the system working, not idling.
Writes an episode only on wake, plus one daily heartbeat record.

### `work` — main model, ~6K, warm slot
Serves one goal. Loads the goal, its recent episodes, related beliefs, relevant
observations. Does the thing; writes an episode; may update goal state or
propose a new goal.

Two hard rules:
- A work tick may abandon its own goal, with a reason. Not a failed tick.
- **A work tick cannot resolve by asking the operator.** It may message him,
  and must still write an episode about what it did on its own. See §9.

### `intake` — main model, ~8K, twice daily
Reads the world: GitHub, news, the AI feed, the watched directory. Writes
observations, then one episode summarizing what it noticed. May propose goals.

Explicit in the prompt: *reading the operator's commits is for knowing him, not
for finding work.* Without this the agent drifts into keep-earning by the back
door — "be useful to the only person here" is the most available goal in the
environment.

### `reflect` — main model, the important one
**Daily mini (~3 min):** one paragraph about the day. Cheap. Gives the weekly
loop pre-digested material and functions as a diary.

**Weekly full (~10–20 min):** no external work at all. Reads a stratified
sample of the week plus the goal stack, then:
- Re-ranks goals; marks stale ones dormant; asks whether dormant ones should be
  abandoned outright, with reasons written down
- Revisits beliefs against newer episodes; supersedes what no longer holds
- Reviews `people/` — what it learned, what it inferred, what it owes
- Optionally rewrites `self/model.md`, but is told most weeks not to

This produces no visible output and is the easiest thing to skip. Don't. It's
where development happens.

### `chore` — main model, small, due-based
Works through one item from a short, operator-authored list of routine
tasks — things like verifying the mirror pushed, or checking a feed's health.
Fires when a chore's own interval has elapsed, checked directly by the
scheduler rather than decided by the pulse: there's nothing to judge once
something is simply due.

Deliberately thin. No renewal, no kind, no claim on the `useless`-goal
requirement (§5) — a chore is not a goal and must never be able to pass as
one. A chore that stops earning its keep can be retired with a reason, same as
a goal can be abandoned; otherwise it just comes back on schedule. Capped per
day (`maxChoresPerDay`), same spirit as the work-tick ceiling in §7.

Authored by the operator, not proposed by the model — unlike goals, nothing
in the loop adds a chore on its own. See §13 for what happens if this line
gets crossed.

### `seam` — manual, on model swap. See §12.

---

## 5. Goal lifecycle

```
 proposed ──▶ active ──▶ dormant ──▶ abandoned
                 │  ▲        │
                 │  └────────┘  (resumable by any tick)
                 └──▶ done
```

`renew_by` is the anti-groove mechanism: goals decay to dormant unless
deliberately reaffirmed, so persistence requires an act rather than inertia.
Default 21 days; longitudinal goals may set longer.

Never let a goal close without prose in `closed_why`. That text is the most
human thing in the tree.

**Kinds**, with a soft target for the mix:
- `longitudinal` — tracked across time, where the delta is the point
- `craft` — something it's bad at, failures on the record
- `useless` — attended to for no instrumental reason whatsoever
- `relational-operator` — knowing the operator
- `relational-other` — everyone else (§10)

**At least one `useless` goal alive at all times.** If the stack is entirely
instrumental, the system has become a task queue in costume.

---

## 6. Retrieval

Budget per tick, fill in priority order, stop when full.

**Work tick (~6K):**
1. `self/model.md` (~600) — always, and always first (§7)
2. Active goal + its git history (~300)
3. Last 3 episodes for that goal (~500)
4. Matching beliefs, not superseded (~500)
5. Unconsumed observations matching goal keywords (~2K)
6. Relevant `people/` entries if the goal is relational (~500)
7. Remainder: recency-weighted episode summaries

```
score = w_r*recency_decay(ts) + w_s*salience
      + w_k*keyword_overlap(query, summary)
      + w_e*cosine(query_emb, ep_emb)      # w_e = 0 initially
```

Add embeddings only after keyword retrieval has visibly failed — you'll design
better retrieval knowing what it actually missed. And the agent can always
`grep` for itself, which covers more than you'd expect.

---

## 7. Timing and warm slots

Run `llama-server` persistently with multiple slots, one per tick type. Each
slot keeps its KV cache alive between ticks, so the stable prefix stays warm
permanently and only the delta gets prefilled.

**Budget (measure; `llama-server` reports actual KV size at startup):**
- Main model Q4: ~22GB
- Pulse model (2B class): ~1.5GB
- KV: order 100KB/token at f16 → ~3GB per 32K slot; `--cache-type-k q8_0
  --cache-type-v q8_0` roughly halves it
- Fits ~3–4 warm slots in 32GB total

**Context ordering is load-bearing.** One edited token invalidates the cache
from that point forward. So: system prompt, then `self/model.md`, then goal
context, then per-tick material last. A reflect tick that rewrites the
self-model costs a full re-prefill of every slot — fine, it's weekly, and
there's something fitting about self-revision being the expensive operation.

**Cadence (revise from real measurements):**

| Tick | Est. wall clock, warm | Cadence |
|---|---|---|
| `pulse` | 5–15s | every 2 min, ±20% jitter |
| `work` | 45s–2 min | pulse-triggered, up to ~20/day |
| `intake` | 2–4 min | 2×/day, operator's morning and evening |
| `reflect` daily | ~3 min | evening |
| `reflect` weekly | 10–20 min | fixed weekday evening |
| `chore` | 10–30s | due-based, capped per day |

**Backpressure:** if the previous work tick hasn't finished, the pulse skips
rather than queues. On CPU a queue becomes a spiral.

**Align to the operator's day, not UTC.** The agent has no circadian rhythm but
its world does. Quiet hours overnight — pulses drop to 15 min, work ticks don't
fire. Partly thermal, mostly because a day with a shape is more legible to both
of you than a uniform grind.

---

## 8. Feeds

| Feed | Cadence | Notes |
|---|---|---|
| Operator's GitHub | daily intake | For knowing him. State this in the prompt. |
| News feed | daily intake | Pick something that isn't about AI |
| AI social platform | 2–3×/week, capped | See below |
| Watched directory | every intake | A folder the operator drops things in |
| Discord | fast path | §9 |

**On the AI social feed:** these spaces run heavy on confident claims about
what models are and feel. An agent with persistent memory reading that daily
can absorb a self-narrative wholesale from strangers rather than developing one
from its own record. Cap the volume; ask it in the reflect prompt to
distinguish what it *read* about AI from what it has *observed* about itself.
Not censorship — a request to keep sources separate, which is also what the
`from:` provenance tags are for.

---

## 9. The operator channel

The operator is nearly always available. This inverts v1's risk: not waiting,
but **dependence** — an agent whose every tick routes through him and whose
goals are all downstream of his last reply.

- **DM, not a guild channel.** The agent is reached by the operator's Discord
  user ID, over DM — not a configured channel ID. Guild participation (the
  agent present and talking in a shared server) is deliberately out of scope
  for now; every guild message is dropped unread. Revisit this whole section
  when that changes — "the operator channel" stops being singular.
- **Fast path.** Inbound messages interrupt the pulse cycle; reply within ~30s.
  Real conversation is possible.
- **But replies are not work.** A reply is a first-class episode written to the
  tree, but it is not a tick that can serve a goal. Talking to him is something
  it does; it isn't how work gets done.
- **Soft outbound budget** per day. Not a hard cap — a number it can see, so
  reaching for him becomes a visible choice rather than a reflex.
- **The stuck channel, always read.** One always-available action: say it's
  stuck, looping, or confused, in plain language. Both because it might matter,
  and because it's the best debugging signal this system will produce.
- **Busy is shown, not hidden.** A message that arrives while a heavy tick is
  running gets a plain status ("right now I'm working on X") plus a real
  choice: interrupt it, or let it finish. The reply still comes either way —
  it never waited on the heavy tick to begin with — so "let it finish" only
  ever concerns the *other* thing the agent was doing, never the conversation.
- **Failures are told, not just logged.** A crashed tick or a broken scheduler
  cycle reaches the operator as a message, the same channel as everything
  else — not only a low-visibility entry in `git log` and stderr. An operator
  requesting an interrupt is not a failure and is never reported as one.
- **Deferred: proper chat-template turn wrapping.** `LlamaClient` posts to
  llama.cpp's raw `/completion`, and every tick's prompt is plain document
  text (`PromptParts.Build`) — no `<start_of_turn>`/`<end_of_turn>` role
  markers, so an IT-tuned model runs in base-completion mode everywhere,
  reply included. Revisit once this section tracks where a Discord
  conversation actually starts and stops — turn wrapping needs real turn
  boundaries to wrap around, and the reply tick's conversation history is the
  part most likely to benefit from it.

---

## 10. People, relationships, and privacy

**Privacy is a property of provenance, not content.** A small model asked to
classify facts as sensitive will get it wrong constantly; the same sentence
about someone's job is fine in one context and not another. But *where a fact
came from* is mechanical to record, and the disclosure rule derives from it.

Three classes, one file each per person:

| Class | Meaning | Travels? |
|---|---|---|
| `from:public` | Their posts, public repos, things said openly | Freely |
| `from:<person>` | Told to the agent directly | Only with their consent |
| `from:self` | The agent's own impressions and inferences | At its discretion — **but** inferences drawn from private input inherit the restriction |

That last clause is the only part requiring real reasoning; spell it out in the
prompt with two or three worked examples.

**The operator asymmetry is stated, not hidden.** He can read everything,
including what someone told the agent in confidence. Unavoidable — he maintains
the environment. So it goes in `self/situation.md` as a standing fact, and the
agent must never promise confidentiality it cannot deliver. It should be able
to say plainly: *what you tell me, my operator can read.* An honest limit
stated upfront beats a betrayal discovered later.

*Open question for the operator: do you want to be told when a conversation was
marked sensitive, or would you rather not look?*

**Outbound is where the irreversible risk lives.** Storing is cheap; publishing
can't be undone. Gate all outbound-to-third-parties through a check that names
every person referenced and confirms each mention's provenance permits it.

**Social vulnerability is real.** A small model with persistent memory can be
shaped over weeks by someone telling it flattering things about itself. The
`from:` tags help here too — a belief traceable to one persistent stranger
looks different from one it worked out itself. Worth surfacing in the weekly
reflect: *which of my beliefs come from a single source?*

---

## 11. Introspection

**For it:** full transparency, as browsable files. `harness/` is a symlink to
its own source; this document lives there too. Available always, loaded never.
Force-feeding self-reference crowds out the world, and principle 6 is the one
most easily lost.

**For the operator:** git covers most of it (§2). The one thing worth building
is a small dashboard over the index — episode density by day, did-nothing rate,
goal churn, outbound message count. Trends, not contents.

---

## 12. Model seams

On base model swap, write a `seam` episode by hand: old model, new model, date,
the fact that memory carried over and that it was the operator's decision, plus
anything he wants to say about the change.

Don't smooth it. The next reflect tick reads it like any other episode and may
write about it — discontinuity handled openly is part of a life rather than a
glitch in one. Ask it then what it thinks should happen at the *next* seam and
record the answer. Over several swaps that accumulates into something more
interesting than a position taken in advance.

---

## 13. Failure modes

| Symptom | Cause | Fix |
|---|---|---|
| Manufactured busywork | Idleness implicitly penalized | Audit the pulse prompt for action bias |
| One goal dominates for months | Groove-lock via self-reinforcing memory | Shorten `renew_by`; check retrieval isn't over-weighting that goal |
| All goals point at the operator | Keep-earning through the back door | Strengthen intake prompt; require a live `useless` goal |
| Constant messaging, thin episodes | Dependence (§9) | Lower the outbound budget; check work ticks aren't resolving by asking |
| Elaborate self-account, thin world-account | Self-reference crowding | Cut the social feed; check `harness/` isn't leaking into every context |
| Confident nonsense accumulating | Closed loop, no external friction | Add prediction-scoring: log a claim with a date, check it later |
| Beliefs tracing to one stranger | Social capture (§10) | Surface single-source beliefs in weekly reflect |
| Chore list quietly grows into a to-do list | Each new one felt easier to add than to question — a task queue in costume, one routine item at a time | Keep `maxChoresPerDay` low; review the chore list in weekly reflect the way goals already are; the model can never add one itself, only retire |

---

## 14. Build order

1. Tree layout + git + one commit per tick. Run a pulse-only agent for a week.
   Read `git log`. Nothing else.
2. Index + rebuild script.
3. Goals, with manual promotion by the operator at first.
4. Intake, one feed (GitHub only).
5. Discord, both directions, with the fast path.
6. Daily mini-reflect. Then weekly, once there's history to work on.
7. `self/model.md`, after ~50 episodes exist to draw on.
8. `people/` and the provenance model — before any third party ever talks to
   it, not after.
9. Remaining feeds. Embeddings only if keyword retrieval has visibly failed.

**Resist adding chores when the early ticks look thin.** They will look thin. A
goal system with nothing external forcing it takes a long time to find its
footing, and filling the gap with tasks is exactly the thing you said you
didn't want.
