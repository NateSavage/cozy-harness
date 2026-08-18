# Nix

Builds clean on nixos-unstable as of 2026-08-07. One fixup needed on first eval:
`buildDotnetModule`'s `fetch-deps` script always writes JSON, and `addNuGetDeps`
picks JSON-vs-Nix parsing off the `nugetDeps` file's extension — so the lockfile
must be named `deps.json`, not `deps.nix`.

## Quick start

```bash
nix develop                       # dotnet 10, sqlite, git, llama-cpp
nix build .#agent-harness
```

### Generating the NuGet lock

`buildDotnetModule` needs a pinned dependency set before it can build offline:

```bash
nix build .#agent-harness.passthru.fetch-deps
./result nix/deps.json
```

Rerun this any time you touch `CozyHarness.csproj`. Until it exists, `nix build`
will fail with a missing-file error — that's expected, not a bug in the flake.

## As a NixOS module

```nix
{
  inputs.agent-harness.url = "github:you/agent-harness";

  outputs = { nixpkgs, agent-harness, ... }: {
    nixosConfigurations.server = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        agent-harness.nixosModules.default
        {
          services.cozy-harness = {
            enable = true;
            package = agent-harness.packages.x86_64-linux.agent-harness;

            user = "agent";
            home = "/home/agent";     # the tree lives at /home/agent/tree

            modelDirectory = "/var/lib/models";
            mainModel  = "gemma-4-26B-A4B-it-Q4_K_M.gguf";
            pulseModel = "gemma-4-E4B-it-Q4_K_M.gguf";
            # optional: fetch on activation instead of placing the files by hand
            mainModelUrl  = "https://huggingface.co/.../gemma-4-26b-a4b-it-q4_k_m.gguf";
            pulseModelUrl = "https://huggingface.co/.../gemma-4-e4b-it-q4_k_m.gguf";
            enableMtp  = false;   # see caveat below
            threads = 8;

            limits = {
              totalMemoryMax  = "30G";   # slice-wide ceiling
              totalMemoryHigh = "28G";   # throttle before killing
              mainModelReserve = "18G";  # protection, not a cap
              harnessMemoryMax = "1G";
              maxTreeSize = "20G";       # warns hourly, never blocks
            };

            discordTokenFile = "/run/secrets/discord-token";
            mirrorRepository = "git@backup-host:agent-mirror.git";

            settings = {
              operatorTimeZone = "Europe/London";
              schedule = {
                pulseIntervalSeconds = 120;
                quietHourStart = 23;
                quietHourEnd = 7;
                maxWorkTicksPerDay = 20;
              };
              channel = {
                operatorName = "you";
                operatorUserId = 123456789012345678;  # your Discord user ID, not a channel — talked to over DM
                notifyOperatorOnSensitive = true;
              };
              goals.minUselessGoals = 1;
              feeds.gitHubUser = "you";
              chores.maxChoresPerDay = 8;
            };
          };
        }
      ];
    };
  };
}
```

## Fetching models

Models are **not** in the Nix store (see below), so `nix build`/`nix rebuild`
never touch them. If you'd rather not place the GGUFs by hand, set
`mainModelUrl`/`pulseModelUrl` and the module adds a systemd oneshot service
per model (`cozy-harness-download-main-model`, `-pulse-model`) that:

- runs before the corresponding `llama-*` service, downloading straight to
  `modelDirectory` on the target machine — never into a build-time derivation
- skips the download if the file is already there
- if `mainModelSha256`/`pulseModelSha256` is set, re-verifies the existing
  file on every activation and redownloads on a mismatch; without a hash the
  file is trusted once present (and a warning says so)

Rebuilding the config doesn't re-trigger a download — the oneshot unit stays
`RemainAfterExit` satisfied until you change the URL, delete the file, or (with
a hash configured) the file stops matching.

**Progress over DM.** If `discordTokenFile` is set, the download service DMs
the operator too — no separate option, it just reuses whatever Discord config
the harness already has. This runs as a plain root oneshot, entirely outside
the .NET process (which usually isn't even up yet — `llama-${name}.service`
depends on this finishing first), so it talks to Discord's REST API directly
with `curl`/`jq` rather than going through `DiscordChannel`. One message, sent
once and edited in place every 5 minutes while the download is in flight
("downloading main model: 42% (3.2G / 7.6G)"), not a new message per update —
these can run for hours (`TimeoutStartSec = "6h"`), and message content is
resolved via `HEAD` where the host supports it; otherwise progress reports
bytes downloaded without a percentage. Every Discord call here is best-effort
and swallows its own failures — a rate limit or network hiccup never takes the
actual download down with it. Verified end-to-end against a fake local server
standing in for both Discord and the model host, not against real Discord.

## The agent user

The module creates a real account — `agent`, with `/home/agent` as its home and
an interactive shell. This is deliberate. Everything-is-a-file is the whole
design: the agent should be able to `cd`, `grep`, and `git log` its own history
with the same tools anyone else would use, and `extraPackages` puts those on its
PATH.

```
/home/agent/
  tree/              its memory, under git — the only thing worth backing up
    harness -> /nix/store/.../share/cozy-harness   its own source, readable
    index.sqlite     derived, gitignored, regenerated on every start
```

To stand where it stands:

```bash
sudo -u agent -i
cd tree && git log --oneline          # the timeline
git log -p self/model.md              # the self-model diff
git log --diff-filter=R goals/        # every state transition
```

That is by far the fastest way to understand what it can actually see.

Models are **not** in the Nix store. A 22GB GGUF copied on every rebuild is not
a thing you want; put them in `modelDirectory` by hand.

## What the module decides for you

| Setting | Why |
|---|---|
| `LimitMEMLOCK=infinity` + `--mlock` | Weights pinned in RAM. Without it the kernel evicts them and every tick pays a disk read. |
| `OOMScoreAdjust=-500` on the servers | They hold the point of the machine; don't let them go first. |
| `--cache-type-k/v q8_0` on the main server | Roughly halves KV cache memory. On CPU, bandwidth is the bottleneck. |
| `Nice=10`, `IOSchedulingClass=idle`, `CPUWeight=20` | The agent has no deadlines and should yield to whatever you're actually doing. |
| `Restart=always` on the harness | An agent whose life ends on one exception isn't persistent. Crashed ticks are already recorded as episodes. |
| `preStart` health-waits | The harness survives a dead server, but starting into a wall of failures pollutes the episode log on day one. |
| Token via `LoadCredential` | Anything in `settings` lands in the world-readable Nix store. |
| `ProtectHome = false` | Every other service should have it on; this one lives in /home. |
| `ReadWritePaths = [ home ]` | With `ProtectSystem=strict`, its home is the only writable path. |
| `llama-*` and the harness talk over `/run/cozy-harness/*.sock` | Unix sockets, not TCP loopback — no reason to pay for a port on a connection that never leaves the box. |
| `Group = cfg.group` on the `llama-*` services, plus a `systemd.tmpfiles.rules` entry for `/run/cozy-harness` | `DynamicUser` still gives each server its own ephemeral UID; pinning the group is what lets the harness's fixed user reach the socket. A per-unit `RuntimeDirectory=` would have worked too, except systemd binds those privately per `DynamicUser` unit and hides them from everyone else ([systemd#7260](https://github.com/systemd/systemd/issues/7260)) — hence the plain tmpfiles-managed directory instead. |

## Model choice

Defaults are **Gemma 4 26B-A4B** (main) and **Gemma 4 E4B** (pulse), which is
not the obvious pick — Qwen3.6-35B-A3B is the stronger agentic model and the
community favourite for local tool use.

The reasoning is about what this harness actually asks for. Its structured
output is a five-field JSON object; anything in this class handles that. What's
genuinely hard is the `reflect` tick: rereading weeks of your own writing and
deciding whether you still mean it. Qwen is repeatedly noted as weaker at
English prose; Gemma is repeatedly praised for it. Gemma 26B-A4B is also ~4GB
smaller at Q4, which buys warm slots.

**Swap to Qwen if the agent turns out to live on tool-heavy work.** Endpoints are
per-tick-type in `agent.json`, so you can even run one model for `work` and
another for `reflect` — though not both at once in 32GB.

E4B rather than E2B for the pulse: it still has to emit a structured decision,
and E4B roughly doubles E2B's Tau2 score for about 5GB. Cheap insurance against
a pulse that wakes work for no reason, or never wakes it.

### MTP

`enableMtp` turns on multi-token prediction — roughly 1.4–2.2x faster generation
with no accuracy loss, which on CPU is a two-minute work tick becoming a
one-minute one. It needs MTP-enabled GGUFs and a llama.cpp build with MTP
merged, and costs ~1GB per server.

**Off by default, and `mtpFlags` is the single thing in this module most likely
to be wrong** — the flag names have been moving. Check `llama-server --help` on
your build first, and use `mainExtraFlags` directly if they differ.

Not enabled for the pulse: it generates about twenty tokens, so drafting buys
nothing.

## Resource limits

Everything the agent runs — both llama-servers and the harness — lives in
`agent.slice` with one shared budget. Per-service limits just move growth
around; a slice-wide cap is the only honest way to say "the agent gets this much
of the machine."

The asymmetry inside the slice is deliberate:

| Component | Limit | Why |
|---|---|---|
| llama-servers | `MemoryMin` only, no Max | Their pages are **mlocked and unreclaimable**. A cgroup Max on them means the OOM killer, not throttling. So they get protection from reclaim instead. |
| harness | real `MemoryMax` | It's the component most likely to leak, and killing it is cheap — it restarts, and crashed ticks are already recorded as episodes. |
| slice | `MemoryHigh` below `MemoryMax` | Throttling range. An assertion fires if you set them equal, because that goes straight from fine to OOM. |
| everything | `MemorySwapMax = 0` | Mlocked weights must never reach swap. |

`DOTNET_GCHeapHardLimitPercent` is set so .NET learns its cgroup limit rather
than discovering it by being killed, and workstation GC is forced because the
harness is one slow loop, not a throughput server.

`cpuQuota` defaults to null. `Nice = 10` and `IOSchedulingClass = idle` already
make the agent yield to whatever you're doing; a hard quota mostly makes ticks
slower without freeing anything you'd notice. Set it only if the box has other
jobs needing guaranteed latency.

`maxTreeSize` is checked hourly and **only warns**. The tree is the agent's
memory — silently failing its writes would be a strange way to treat that. Years
of episodes fit in a few GB, so tripping it means something is wrong (unpruned
`observations/`, usually) rather than that it has lived too long.

## Monitoring

Everything the module creates, by tier — what actually needs an alert versus
what's just useful context.

**Tier 1 — the agent doesn't run at all if these are down:**

| Unit | What it is | A bad day looks like |
|---|---|---|
| `cozy-harness.service` | the .NET process — scheduler loop, Discord, everything | not `active`, or `NRestarts` climbing. `preStart` waits up to 10 minutes for both llama servers before failing, so one slow start after a cold boot is normal; a *repeating* 10-minute cycle isn't. |
| `llama-main.service` | the main model — work/intake/reflect/chore all need it | failed or restart-looping (`RestartSec=10`, so a persistent problem shows as a fast loop). Check `journalctl -u llama-main` for an OOM kill or a missing model file first. |
| `llama-pulse.service` | the small model behind the pulse | same shape of failure. If only this one is down, `cozy-harness.service` itself looks "up" while doing nothing — every pulse cycle fails and gets DMed to you as a tick failure (see below), which is actually the fastest way you'll notice. |

Quick check for all three: `systemctl --failed`, or per-unit
`systemctl is-active <unit>` plus `systemctl show <unit> -p NRestarts --value`
for restart-loop detection — the two numbers a poller actually wants.

**Tier 2 — periodic correctness, not "is it up":**

| Unit | Runs | A bad day looks like |
|---|---|---|
| `agent-disk-check.service` (+ `.timer`) | hourly, if `maxTreeSize` is set (default on) | exits non-zero and *stays* `failed` until a later run finds the tree back under the limit — `systemctl status agent-disk-check` names the reason directly. |
| `cozy-harness-download-main-model.service` / `-pulse-model` | once, only if `mainModelUrl`/`pulseModelUrl` is set | download or hash check fails; `llama-main`/`llama-pulse` then fail too since the model file never lands. Not present at all if you're placing GGUFs by hand (the default). |

**Not a unit, but worth watching:** `agent.slice` —
`systemctl status agent.slice` or `systemd-cgtop` for how close the agent as a
whole is to `totalMemoryMax`. Since the llama servers are mlocked
(unreclaimable) and get no `MemoryMax` of their own, a slice-wide squeeze shows
up as the *harness* getting killed first, not a graceful slowdown — see
[Resource limits](#resource-limits) above.

**The complementary, in-band signal:** `cozy-harness.service` DMs the operator
directly (`ErrorReporter` — see `docs/tick-cycle.md`) whenever a tick or
scheduler cycle throws. That catches *application*-level failures — a bug, a
bad model response, a git error — faster than any systemd-level check will,
often before a restart even happens. systemd monitoring is for the failures
too broken to DM about: the process won't start, is crash-looping, or the box
itself is out of memory.

## Things to check on your box

- **`services.cozy-harness.threads`** — physical cores, not hyperthreads.
- **Gemma 4's license.** Sources disagree — some say Apache 2.0, others the
  Gemma license. Read the model card before you depend on it.
- **`mainContextSize`** — 65536 across 4 slots is 16K each. `llama-server` prints
  the real KV size at startup; if it doesn't fit alongside 22GB of weights,
  lower this before lowering the quant.
- **`mirrorRepository`** — the default is on the same disk, which protects
  against the agent but not against the disk. The module warns about this. Point
  it at another host.
- **Backups.** The tree *is* the agent. `/home/agent/tree` is the only thing
  worth backing up; `index.sqlite` is derived and regenerates on every start.
- **The mirror.** Unset by default, and the module warns about it. Without one,
  the agent can destroy its own history with a force-push or an aggressive `gc`
  and nothing will stop it. Point it at another host.
