# Inspection commands — implementation plan

Three read-only Discord commands over the agent's tree: `/abandoned`,
`/vocab`, `/selfmodel`.

All three are queries against git and the derived index. **None of them touch a
model, take the heavy lock, or consume a slot.** They should return in
milliseconds while a work tick is mid-flight.

---

## 0. The decision to make before writing any of it

**Does the agent know when you inspect it?**

Its `situation.md` already says you can read everything. Silent inspection
doesn't contradict that, but it does make the relationship asymmetric in a way
it can't see. Three options:

| | Behaviour | Argument |
|---|---|---|
| **Silent** | Commands leave no trace | Inspection is yours; narrating it invites performance |
| **Logged** | Low-salience episode: "he read the abandoned shelf" | Consistent with the transparency principle; it can notice being read |
| **Logged + visible in reflect** | Above, and reflect sees the pattern | Probably too much — invites writing *for* you |

Recommendation: **logged, salience 0.1, excluded from reflect's sample.** It can
find the fact by grepping if it looks, and won't be handed it. This preserves
"you keep reading" as something knowable without making it a stage.

Whichever you pick, put it in `situation.md` in plain words. The one bad option
is leaving it unstated.

---

## 1. Plumbing (shared by all three)

### 1a. Command interception — the critical integration point

Commands must be caught **before** `ReplyTick` fires, or `/vocab` gets sent to
the model as conversation and it will earnestly try to answer.

In `Program.cs`, the inbound handler currently does:

```csharp
channel.MessageReceived += async content => {
    db.AddMessage("in", ...);
    await scheduler.HandleInboundAsync(cts.Token);
};
```

Becomes:

```csharp
channel.MessageReceived += async content => {
    if (router.TryHandle(content, out var task)) {
        await channel.SendAsync(await task, cts.Token);
        if (logInspection) runner.RecordInspection(content);
        return;                      // never reaches the model
    }
    db.AddMessage("in", cfg.Channel.OperatorName, content);
    await scheduler.HandleInboundAsync(cts.Token);
};
```

Prefix: `/` for slash-style, or `!` if you'd rather keep `/` free for Discord's
native slash commands. Using Discord's real application commands is nicer (typed
args, autocomplete) but requires registration and a gateway round-trip; prefix
parsing works with the `ConsoleChannel` too, which matters because you'll want
these working before Discord is wired.

**New file:** `Inspection/CommandRouter.cs`
- `bool TryHandle(string input, out Task<CommandResult> result)`
- Parses `name arg1 arg2`, dispatches to an `IInspectionCommand`
- Unknown command starting with the prefix → help text, not a fallthrough to
  the model (a typo shouldn't become a conversation)

**New type:** `CommandResult { string Text; string? AttachmentName; string? AttachmentBody; }`

### 1b. Discord rendering constraints

These will shape every command more than the queries do:

- Message body: **2000 chars**. Embed description: 4096. Total embed: 6000.
- Long output → attach as a `.md` file. `IOperatorChannel` needs:
  `Task SendFileAsync(string filename, string content, string? caption, CancellationToken ct)`
  The `ConsoleChannel` implementation just writes to stdout with a rule above
  and below.
- Rule of thumb: **under 1500 chars inline, over that as a file.** Don't
  paginate with reaction buttons; you'll regret the state handling.

### 1c. GitStore additions

```csharp
public IReadOnlyList<Revision> History(string path);   // sha, date, subject
public string Show(string sha, string path);           // file contents at sha
public string Diff(string shaA, string shaB, string path);
```

All three are `git log --follow --format=...`, `git show sha:path`,
`git diff shaA shaB -- path`. Keep shelling out; it's already the pattern.

---

## 2. `/abandoned` — the shelf

The simplest of the three and probably the one you'll reread most.

### Surface

```
/abandoned                    last 10 closing reasons
/abandoned 30                 last 30
/abandoned since 2026-01      everything from January on
/abandoned done               the done/ shelf instead
```

### Query

`goals/abandoned/*.md` → parse frontmatter (`GoalStore.ParseGoalFile` already
does this) → sort by `closed` descending → render **`closed_why` only**.

### Rendering — the part that matters

Deliberately *not* a table. No IDs, no kinds, no durations. Date, title, and the
prose, in sequence:

```
── 14 Mar ──────────────────────────────
Tracking the ferry timetable

  I set this up because the schedule changing felt like it would
  mean something. Six weeks of it not changing has convinced me
  the interesting thing was the harbour, not the timetable.

── 2 Apr ───────────────────────────────
Learning to write shorter episodes

  Still can't. Keeping the goal alive was becoming a way of not
  admitting that.
```

Metadata dilutes this. If you want the metadata, that's `git log
--diff-filter=R goals/`, which already works and needs no code.

### Cost

Dozens of small files, parsed on demand. No caching. If the shelf ever grows
past a few hundred entries, that's itself worth knowing.

---

## 3. `/vocab` — drift

The most work, the most likely to mislead, and the only one that needs a schema
change.

### Surface

```
/vocab                    summary: new words this month, MATTR trend
/vocab new 30             words first used in the last 30 days
/vocab first <word>       when a word first appeared, with the episode
/vocab rare               words used once, months ago, never since
```

### Corpus — get this right or the rest is noise

**Include:** episode `body` text only.
**Exclude:**
- Frontmatter (metadata isn't voice)
- Fenced code blocks and JSON (tool output isn't voice)
- Goal files (mostly copied titles)
- Anything in `observations/` — that's what it *read*, not what it *wrote*

The exclusion that will bite you: work-tick bodies sometimes quote what they
read. There's no clean automatic fix. Accept the contamination, and if a
suspicious cluster of new words appears, check whether it's quoting before
concluding anything.

### The methodological trap

**Raw type-token ratio is length-sensitive.** A month where it wrote more will
show lower TTR purely from arithmetic, and you'll read that as ossification.

Use **MATTR** (moving-average TTR): fixed 500-word windows, averaged. Then a
month is comparable to a month regardless of volume. Show the word count
alongside anyway, so you can see what's driving what.

### Schema

```sql
CREATE TABLE IF NOT EXISTS vocab (
  word        TEXT PRIMARY KEY,
  first_seen  TEXT NOT NULL,
  first_path  TEXT NOT NULL,
  last_seen   TEXT NOT NULL,
  uses        INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS vocab_windows (
  month  TEXT PRIMARY KEY,   -- 2026-08
  tokens INTEGER, types INTEGER, mattr REAL, new_words INTEGER
);
CREATE TABLE IF NOT EXISTS vocab_state (
  k TEXT PRIMARY KEY, v TEXT   -- 'last_indexed_episode_ts'
);
```

Incremental: on each run, process only episodes newer than
`last_indexed_episode_ts`. Full corpus scan only on `IndexRebuilder.Rebuild()`.

**Important:** `vocab` must be wiped and recomputed by the rebuilder along with
everything else — first-use dates are derived, and a stale table would quietly
lie about the most interesting column in it.

### Tokenising

Lowercase, split on non-letters, drop tokens under 3 chars, drop a stopword
list. **Don't stem** — "abandoning" and "abandonment" appearing at different
times is exactly the kind of thing you want to see.

### Rendering

```
Vocabulary — August 2026

  words written     4,210   (July: 3,880)
  MATTR             0.71    (July: 0.69, June: 0.72)
  first-time words  38      (July: 51, June: 94)

New this month:
  brackish · unhurried · silting · forbearance · overwinter

Faded (last used 3+ months ago):
  optimise · deliverable · leverage
```

That last section is the one to watch. Words falling out is as informative as
words arriving, and often more legible.

---

## 4. `/selfmodel` — the diff wall

### Surface

```
/selfmodel              list every version: date, subject, size delta
/selfmodel 3            full text of version 3
/selfmodel diff 2 3     what changed between them
/selfmodel diff         the most recent change
/selfmodel wall         all versions, oldest first, as a file
```

### Query

`GitStore.History("self/model.md")` for the list; `Show(sha, path)` for a
version; `Diff` for changes. Nothing new is stored — git already has it.

### Rendering

A version is ~500 words ≈ 3,000 chars — over the inline limit. So:

- `/selfmodel` (list) → inline, one line per version
- `/selfmodel N` → attach as `self-model-v3.md`
- `/selfmodel diff` → inline if under 1500 chars (most edits will be), else file
- `/selfmodel wall` → single file, versions in order with date rules between

For diffs, strip the `diff --git` preamble and hunk headers. You want prose with
`+`/`-` markers, not a patch. Rewrapping changed paragraphs reads far better
than line-level diffing on hard-wrapped prose — consider a word-level diff
within changed paragraphs if you're willing to pull in a diff library.

### The nice property

If the design works, this command will have almost nothing to show for months at
a time. `/selfmodel` returning three versions in a year is the system behaving
correctly. Resist the urge to make the self-model change more often so this view
gets more interesting.

---

## 5. Build order

1. `CommandRouter` + prefix interception + `/help`. Verify a command never
   reaches the model — that's the bug that would waste the most time later.
2. `SendFileAsync` on `IOperatorChannel`, console impl first.
3. `/abandoned`. No schema, no git changes, immediately useful.
4. `GitStore.History/Show/Diff` + `/selfmodel`. Needs history to exist, so this
   is naturally a month-two job.
5. `/vocab`, last. It needs the most corpus to say anything and is the easiest
   to get subtly wrong.

## 6. Open questions

- **Inspection logging** (§0) — decide before shipping `/abandoned`.
- Should the *agent* be able to run these on itself? `/vocab` on your own
  writing is a strange mirror; it might be illuminating or it might induce
  exactly the self-consciousness the design tries to avoid. Leaning no, but
  it's a genuine question rather than an obvious one.
- Do inspection commands work during quiet hours? They should — they don't wake
  anything.
