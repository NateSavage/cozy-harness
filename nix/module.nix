{ config, lib, pkgs, ... }:

let
  cfg = config.services.cozy-harness;
  jsonFormat = pkgs.formats.json { };

  # The harness reads agent.json. Generating it from Nix means the whole system —
  # tick cadence, quiet hours, privacy defaults — is declarative and diffable.
  configFile = jsonFormat.generate "agent.json" cfg.settings;

  treeDir = "${cfg.home}/tree";

  # Shared between the two llama-server units (DynamicUser, ephemeral UID each)
  # and the harness (fixed cfg.user/cfg.group): a plain tmpfs directory rather
  # than a per-unit RuntimeDirectory=, which systemd binds privately per DynamicUser
  # unit and hides from everyone else (systemd/systemd#7260) — connecting from the
  # harness would fail even with matching permission bits. Ownership is root:cfg.group,
  # 0770; each llama unit pins Group = cfg.group (DynamicUser still gives it its own
  # UID) so its socket lands group-writable, and cfg.group is already the harness's
  # primary group.
  socketDir = "/run/cozy-harness";
  socketPathFor = name: "${socketDir}/llama-${name}.sock";

  # Shared with the download-progress notifier below and the Discord-config
  # assertion at the bottom of this file.
  operatorUserId = cfg.operatorDiscordUserId;

  # Readiness waiting for llama-main/llama-pulse happens inside the harness
  # itself now (LlamaClient.WaitForHealthyAsync, called from Program.cs),
  # after the process — and therefore this systemd unit — is already
  # "started". It used to block here in preStart instead, which meant CPU
  # model load time (minutes, under --mlock) raced against systemd's own
  # startup timeout for no reason; see cozy-harness.service below.

  mkLlamaService = { name, model, ctxSize, parallel, quantKv, memoryMin, mtp ? false, extraFlags ? [ ] }:
    let socketPath = socketPathFor name; in {
    "llama-${name}" = {
      description = "llama-server (${name}) for cozy-harness";
      wantedBy = [ "multi-user.target" ];
      after = [ "network.target" ];

      # A socket left behind by an unclean exit makes llama-server's bind() fail
      # with "address already in use" on the next start, since it doesn't unlink
      # first. Directory is 0770 root:cfg.group; this unit's dynamic UID can
      # unlink inside it regardless of who owned the stale file, since unlink
      # permission comes from the directory, not the file.
      preStart = "${pkgs.coreutils}/bin/rm -f ${socketPath}";

      # A model that fails to load fails identically every time (bad path,
      # unsupported architecture, OOM) — RestartSec=10 was letting that retry
      # forever (seen: 11 restarts and counting on a config error that no
      # amount of waiting fixes). Two attempts within a window comfortably
      # covering both is enough to rule out a one-off fluke without spinning
      # forever on something that needs a human. `systemctl reset-failed`
      # clears it once the actual problem's fixed.
      startLimitIntervalSec = 30;
      startLimitBurst = 2;

      serviceConfig = {
        Type = "simple";
        ExecStart = lib.escapeShellArgs ([
          "${cfg.llamaPackage}/bin/llama-server"
          "--model" model
          "--host" socketPath   # llama-server listens on a unix socket iff --host ends in .sock
          "--threads" (toString cfg.threads)
          "--ctx-size" (toString ctxSize)
          "--parallel" (toString parallel)
          "--load-mode" "mlock"   # --mlock is deprecated (still works, but this is the current flag)
        ] ++ lib.optionals quantKv [
          # Roughly halves KV cache memory at negligible quality cost. On CPU,
          # every extra bit per parameter is more RAM bandwidth per token.
          "--cache-type-k" "q8_0"
          "--cache-type-v" "q8_0"
        ] ++ lib.optionals mtp ([
          "--spec-type" "draft-mtp"
          "--spec-draft-model" "${cfg.modelDirectory}/${cfg.mtpDraftModel}"
          "--spec-draft-n-max" (toString cfg.mtpDraftNMax)
        ] ++ lib.optionals (cfg.mtpDraftNMin != 0) [
          "--spec-draft-n-min" (toString cfg.mtpDraftNMin)
        ] ++ cfg.mtpFlags)
          ++ extraFlags);

        Slice = "agent.slice";

        # Protection, not a cap: these pages are mlocked and unreclaimable, so a
        # MemoryMax here would mean the OOM killer rather than throttling. Tell
        # the kernel to take memory from something else instead.
        MemoryMin = memoryMin;

        Restart = "always";
        RestartSec = 10;

        # --mlock pins the weights so they are never paged out. Without this the
        # models are the first thing the kernel evicts and every tick pays a
        # disk read.
        LimitMEMLOCK = "infinity";

        # These processes hold the whole point of the machine. Don't let the OOM
        # killer take them before anything else.
        OOMScoreAdjust = -500;

        DynamicUser = true;
        Group = cfg.group;   # shares the harness's group so it can reach socketDir; UID stays dynamic
        UMask = "0007";      # so the socket file itself comes out group-writable (rwxrwx---)
        ProtectSystem = "strict";
        ProtectHome = true;
        PrivateTmp = true;
        PrivateDevices = true;
        NoNewPrivileges = true;
        RestrictAddressFamilies = [ "AF_INET" "AF_INET6" "AF_UNIX" ];
        ReadOnlyPaths = [ cfg.modelDirectory ];
        ReadWritePaths = [ socketDir ];
        MemoryDenyWriteExecute = false;  # BLAS kernels need it
      };
    };
  };

  mkModelDownloadService = { name, url, sha256, dest, gates ? [ "llama-${name}.service" ] }:
    let
      shaArg = if sha256 == null then "" else sha256;

      # If Discord is configured for the harness, reuse it for download
      # progress too — no separate toggle. The download runs as a plain root
      # oneshot, completely outside the .NET process (which usually isn't
      # even running yet — this is a dependency of llama-${name}.service), so
      # it can't reuse DiscordChannel; it talks to Discord's REST API
      # directly instead.
      notifyProgress = cfg.discordTokenFile != null;

      script = pkgs.writeShellScript "cozy-harness-download-${name}-model" ''
        set -euo pipefail

        dest=${lib.escapeShellArg dest}
        url=${lib.escapeShellArg url}
        sha256=${lib.escapeShellArg shaArg}

        verify() {
          [ -n "$sha256" ] || return 1
          [ "$(${pkgs.coreutils}/bin/sha256sum "$1" | cut -d' ' -f1)" = "$sha256" ]
        }

        if [ -f "$dest" ]; then
          if [ -z "$sha256" ]; then
            echo "$dest already present (no hash configured, trusting it)"
            exit 0
          fi
          if verify "$dest"; then
            echo "$dest already present and verified"
            exit 0
          fi
          echo "$dest exists but failed verification; redownloading" >&2
        fi

        ${lib.optionalString notifyProgress ''
          # Best-effort DM progress reporting. Every call in here is allowed
          # to fail silently (network hiccup, rate limit, whatever) — a
          # stalled notifier must never take the actual download down with
          # it. One message, edited in place, rather than a new one per
          # update: this can run for hours (see TimeoutStartSec below), and a
          # DM full of "12%... 13%... 14%..." helps no one.
          DISCORD_TOKEN="$(cat "$CREDENTIALS_DIRECTORY/discord-token")"
          DISCORD_API="https://discord.com/api/v10"
          DM_CHANNEL_ID=""
          DISCORD_MSG_ID=""

          discord_dm_channel() {
            DM_CHANNEL_ID="$(${pkgs.curl}/bin/curl -sf -X POST "$DISCORD_API/users/@me/channels" \
              -H "Authorization: Bot $DISCORD_TOKEN" -H "Content-Type: application/json" \
              -d '{"recipient_id":"${toString operatorUserId}"}' \
              | ${pkgs.jq}/bin/jq -r '.id // empty')" || DM_CHANNEL_ID=""
          }

          notify() {
            local text="$1"
            [ -n "$DM_CHANNEL_ID" ] || discord_dm_channel
            [ -n "$DM_CHANNEL_ID" ] || return 0
            local body
            body="$(${pkgs.jq}/bin/jq -n --arg c "$text" '{content: $c}')"
            if [ -z "$DISCORD_MSG_ID" ]; then
              DISCORD_MSG_ID="$(${pkgs.curl}/bin/curl -sf -X POST "$DISCORD_API/channels/$DM_CHANNEL_ID/messages" \
                -H "Authorization: Bot $DISCORD_TOKEN" -H "Content-Type: application/json" \
                -d "$body" | ${pkgs.jq}/bin/jq -r '.id // empty')" || DISCORD_MSG_ID=""
            else
              ${pkgs.curl}/bin/curl -sf -X PATCH "$DISCORD_API/channels/$DM_CHANNEL_ID/messages/$DISCORD_MSG_ID" \
                -H "Authorization: Bot $DISCORD_TOKEN" -H "Content-Type: application/json" \
                -d "$body" >/dev/null || true
            fi
            return 0
          }
        ''}

        mkdir -p "$(dirname "$dest")"
        tmp="$dest.part"

        ${lib.optionalString notifyProgress ''
          notify "downloading ${name} model ($(basename "$dest"))..."
          # Best-effort: chunked responses or a HEAD-unfriendly host just mean
          # progress reports as bytes-so-far instead of a percentage.
          total="$(${pkgs.curl}/bin/curl -sIL "$url" 2>/dev/null \
            | ${pkgs.gnugrep}/bin/grep -i '^content-length:' | tail -1 \
            | cut -d: -f2 | tr -d ' \r\n')" || total=""
        ''}

        ${pkgs.curl}/bin/curl -fL --retry 3 --retry-delay 5 -o "$tmp" "$url" &
        curl_pid=$!

        ${lib.optionalString notifyProgress ''
          # Poll the partial file's size rather than parse curl's own
          # progress meter — that's meant for a terminal, not for scripting.
          # (A retry inside curl's own --retry restarts $tmp from scratch, so
          # a brief dip in reported progress after a network blip is real,
          # not a bug here.)
          while kill -0 "$curl_pid" 2>/dev/null; do
            sleep 2
            kill -0 "$curl_pid" 2>/dev/null || break
            have="$(${pkgs.coreutils}/bin/stat -c%s "$tmp" 2>/dev/null || echo 0)"
            if [ -n "$total" ] && [ "$total" -gt 0 ] 2>/dev/null; then
              pct=$(( have * 100 / total ))
              notify "downloading ${name} model: ''${pct}% ($(${pkgs.coreutils}/bin/numfmt --to=iec "$have") / $(${pkgs.coreutils}/bin/numfmt --to=iec "$total"))"
            else
              notify "downloading ${name} model: $(${pkgs.coreutils}/bin/numfmt --to=iec "$have") so far"
            fi
          done
        ''}

        set +e
        wait "$curl_pid"
        curl_status=$?
        set -e

        if [ "$curl_status" -ne 0 ]; then
          ${lib.optionalString notifyProgress ''notify "downloading ${name} model failed (curl exit $curl_status)"''}
          exit "$curl_status"
        fi

        actual_sha256="$(${pkgs.coreutils}/bin/sha256sum "$tmp" | cut -d' ' -f1)"

        if [ -n "$sha256" ] && [ "$actual_sha256" != "$sha256" ]; then
          echo "downloaded $url but it failed hash verification (expected $sha256, got $actual_sha256)" >&2
          ${lib.optionalString notifyProgress ''notify "${name} model downloaded but failed hash verification (expected $sha256, got $actual_sha256)"''}
          rm -f "$tmp"
          exit 1
        fi

        mv "$tmp" "$dest"
        echo "downloaded $dest (sha256: $actual_sha256)"
        ${lib.optionalString notifyProgress ''
          if [ -n "$sha256" ]; then
            notify "${name} model downloaded and verified (sha256: $actual_sha256)"
          else
            notify "${name} model downloaded (sha256: $actual_sha256)"
          fi
        ''}
      '';
    in {
      "cozy-harness-download-${name}-model" = {
        description = "Download the ${name} model for cozy-harness";
        # WantedBy/Before here (rather than touching mkLlamaService) means this
        # unit only exists — and only gets pulled in — when a URL is actually
        # configured; the gated unit(s) start exactly as before if it isn't.
        # gates defaults to the matching llama-${name}.service, but the MTP
        # drafter has no service of its own — it gates llama-main.service instead.
        requiredBy = gates;
        before = gates;
        after = [ "network-online.target" ];
        wants = [ "network-online.target" ];

        serviceConfig = {
          Type = "oneshot";
          RemainAfterExit = true;
          ExecStart = "${script}";
          User = "root";  # writes into modelDirectory, a stable shared path

          LoadCredential = lib.optional notifyProgress
            "discord-token:${cfg.discordTokenFile}";

          # Multi-gigabyte file over a possibly slow link; systemd's 90s
          # default would kill a healthy download.
          TimeoutStartSec = "6h";
        };
      };
    };
in
{
  options.services.cozy-harness = {
    enable = lib.mkEnableOption "the agent harness";

    package = lib.mkOption {
      type = lib.types.package;
      description = "The harness package.";
    };

    user = lib.mkOption {
      type = lib.types.str;
      default = "agent";
      description = ''
        The system user the agent runs as. It gets a real account with a real
        home directory, because the tree is its life and everything-is-a-file is
        the whole design: it should be able to `cd`, `grep`, and `git log` its
        own history with the same tools anyone else would use.
      '';
    };

    group = lib.mkOption {
      type = lib.types.str;
      default = "agent";
      description = "Primary group for the agent user.";
    };

    home = lib.mkOption {
      type = lib.types.path;
      default = "/home/agent";
      description = ''
        The agent's home. Its memory tree lives at $HOME/tree, its own source is
        symlinked to $HOME/tree/harness, and this is the only directory it can
        write to.

        This directory IS the agent. It is the only thing on the machine worth
        backing up — index.sqlite is derived and regenerates on every start.
      '';
    };

    shell = lib.mkOption {
      type = lib.types.package;
      default = pkgs.bashInteractive;
      description = ''
        Login shell for the agent user. A real shell, so you can `sudo -u agent
        -i` and stand where it stands — which is by far the fastest way to
        understand what it can actually see.
      '';
    };

    extraPackages = lib.mkOption {
      type = lib.types.listOf lib.types.package;
      default = with pkgs; [ git coreutils findutils gnugrep gnused less ripgrep jq sqlite ];
      description = ''
        Tools on the agent's PATH. A small model is far better at grep and git
        log than at any bespoke API, so give it the ordinary ones.
      '';
    };

    llamaPackage = lib.mkOption {
      type = lib.types.package;
      default = pkgs.llama-cpp;
      description = "llama.cpp build providing llama-server.";
    };

    modelDirectory = lib.mkOption {
      type = lib.types.path;
      default = "/var/lib/models";
      description = ''
        Where the GGUF files live. Deliberately outside the Nix store — a 22GB model is not something you want to copy on every rebuild.
      '';
    };

    mainModel = lib.mkOption {
      type = lib.types.str;
      default = "gemma-4-26B-A4B-it-qat-UD-Q4_K_XL.gguf";
      example = "gemma-4-26B-A4B-it-Q4_K_M.gguf";
      description = ''
        Filename (within modelDirectory) of the main model.

        Gemma 4 26B-A4B is the suggested default rather than Qwen3.6-35B-A3B,
        which is the stronger agentic model. The reasoning: this harness's
        structured output is a five-field JSON object, which anything in this
        class handles. What is actually hard here is the reflect tick — rereading
        weeks of your own writing and deciding whether you still mean it — and
        Gemma is consistently rated better at English prose while Qwen is
        consistently noted as weaker at it.

        It is also smaller (~16GB at Q4 against ~20-22GB), which buys warm slots.

        If your agent turns out to spend its life on tool-heavy work rather than
        on writing, swap to Qwen. The endpoints are per-tick-type; nothing else
        in the harness changes.
      '';
    };

    pulseModel = lib.mkOption {
      type = lib.types.str;
      default = "gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf";
      example = "gemma-4-E4B-it-Q4_K_M.gguf";
      description = ''
        Small model for the pulse tick. Its only job is deciding whether anything
        needs attention, and it should usually answer no.
      '';
    };

    mainModelUrl = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-qat-GGUF/resolve/main/gemma-4-26B-A4B-it-qat-UD-Q4_K_XL.gguf";
      example = "https://huggingface.co/example/gemma-4-26B-A4B-it-Q4_K_M-GGUF/resolve/main/gemma-4-26b-a4b-it-q4_k_m.gguf";
      description = ''
        Direct download URL for mainModel. When set, a systemd oneshot service
        fetches it into modelDirectory before llama-main starts. Skipped if the
        file is already present — and, if mainModelSha256 is set, only trusted
        once it verifies.

        Still deliberately outside the Nix store (see modelDirectory): this
        downloads straight to the target machine's disk at activation time, not
        into a build-time derivation, so a rebuild never re-copies the file.
      '';
    };

    mainModelSha256 = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = "a7c5bc715f5ff8e99a3e8901ce7d2b42b402c669bf24f7c5250747633d0f5891";
      description = ''
        Expected sha256 of mainModel. Verified after download, and re-checked
        against any file already at that path — a mismatch triggers a
        redownload. Leave null to skip verification and trust the file once
        present.
      '';
    };

    pulseModelUrl = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = "https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/main/gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf";
      example = "https://huggingface.co/example/gemma-4-E4B-it-Q4_K_M-GGUF/resolve/main/gemma-4-e4b-it-q4_k_m.gguf";
      description = "Direct download URL for pulseModel. Same mechanics as mainModelUrl.";
    };

    pulseModelSha256 = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = "df0fd4ee07072c607c29a0a1cb4f98918426cca12f45a2776bdd6ee6d09a4de3";
      description = "Expected sha256 of pulseModel. Same mechanics as mainModelSha256.";
    };

    threads = lib.mkOption {
      type = lib.types.int;
      default = 8;
      description = "Inference threads. Match physical cores, not hyperthreads.";
    };

    topP = lib.mkOption {
      type = lib.types.float;
      default = 0.95;
      description = ''
        Nucleus sampling threshold sent on every completion request, for both
        mainModel and pulseModel. Google's published default for Gemma 4.
      '';
    };

    topK = lib.mkOption {
      type = lib.types.int;
      default = 64;
      description = ''
        Top-k sampling cutoff sent on every completion request. Google's
        published default for Gemma 4 — llama-server's own generic default
        (40) predates and doesn't match it.
      '';
    };

    stopSequences = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ "\n\n---" "<end_of_turn>" ];
      description = ''
        Strings that end a completion early. `<end_of_turn>` is Gemma's real
        raw-completion stop token (id 106): the harness talks to llama.cpp's
        /completion endpoint directly, never /v1/chat/completions, so nothing
        ever renders the chat template — but an IT-tuned model can still emit
        its own end-of-turn token unprompted. "\n\n---" catches the model
        starting a new markdown section instead of stopping.
      '';
    };

    enableMtp = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = ''
        Multi-token prediction: speculative decoding against a small drafter model
        that mainModel's own quant repo ships alongside it (a companion mtp-*.gguf,
        distinct from mainModel itself — see mtpDraftModel, required alongside this).
        Reported at roughly 1.4-2.2x faster generation with no accuracy loss, which
        on CPU is the difference between a two-minute work tick and a one-minute one.

        This is the single toggle: turning it on also forces mainSlots to 1 and
        collapses settings.llm.slots so every tick type lands on that one slot —
        llama.cpp's MTP drafting only supports a single parallel slot
        (n_parallel=1), and that's a consequence of flipping this switch, not a
        second and third thing you need to keep in sync by hand. It does mean
        tick types serialize onto one slot instead of the four-way parallelism
        mainSlots normally buys; only turn this on if faster per-tick generation
        matters more than ticks overlapping.

        DEFAULT OFF. --spec-* flag names have moved before (the --draft-max /
        --draft-min flags this module used to hardcode no longer exist) and may
        move again — verify against `llama-server --help` on your build.
      '';
    };

    mtpDraftModel = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      example = "mtp-gemma-4-26B-A4B-it.gguf";
      description = ''
        Filename (within modelDirectory) of the MTP drafter GGUF used for
        speculative decoding against mainModel. This is a separate, much smaller
        file than mainModel itself — typically shipped at the root of the same
        quant repo as a companion `mtp-*.gguf`. Required when enableMtp is set.
      '';
    };

    mtpDraftModelUrl = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      example = "https://huggingface.co/example/gemma-4-26B-A4B-it-qat-GGUF/resolve/main/mtp-gemma-4-26B-A4B-it.gguf";
      description = "Direct download URL for mtpDraftModel. Same mechanics as mainModelUrl.";
    };

    mtpDraftModelSha256 = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      description = "Expected sha256 of mtpDraftModel. Same mechanics as mainModelSha256.";
    };

    mtpDraftNMax = lib.mkOption {
      type = lib.types.int;
      default = 3;
      description = ''
        Maximum draft tokens per step (--spec-draft-n-max). Model cards for MTP
        drafters generally recommend 2-3; higher values see diminishing returns
        as more of the draft gets rejected.
      '';
    };

    mtpDraftNMin = lib.mkOption {
      type = lib.types.int;
      default = 0;
      description = ''
        Minimum draft tokens per step (--spec-draft-n-min). 0 (llama.cpp's own
        default) omits the flag entirely rather than passing it explicitly.
      '';
    };

    mtpFlags = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ ];
      description = ''
        Extra flags appended after the ones this module derives automatically
        (--spec-type draft-mtp, --spec-draft-model, --spec-draft-n-max, and
        --spec-draft-n-min when non-zero) — for rarely-needed knobs like
        --spec-draft-p-min or --spec-draft-device. Verify against
        `llama-server --help` on your build; the --spec-* surface has moved
        before.
      '';
    };

    mainExtraFlags = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ ];
      example = [ "--numa" "distribute" ];
      description = ''
        Raw flags appended to the main llama-server. The escape hatch for anything this module gets wrong or hasn't caught up with.

        `--numa distribute` is worth trying if your box has more than one memory
        node; on CPU inference, memory locality moves the number more than most
        other knobs.
      '';
    };

    pulseExtraFlags = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ ];
      description = "Raw flags appended to the pulse llama-server.";
    };

    mainContextSize = lib.mkOption {
      type = lib.types.int;
      default = 65536;
      description = ''
        Total context across all slots. Four warm slots at 16K each is the design
        default; each slot keeps its KV cache between ticks so only the delta is
        prefilled.
      '';
    };

    mainSlots = lib.mkOption {
      type = lib.types.int;
      default = 4;
      description = ''
        Parallel slots on the main server. One per tick type.

        Forced to 1 whenever enableMtp is set — llama.cpp's MTP drafting only
        supports a single parallel slot, so this value is overridden
        internally rather than something you also need to set by hand.
      '';
    };

    enableGit = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Whether the harness versions its tree in git at all: repo init at
        startup, one commit per tick, mirror push. The tree is still plain
        files on disk either way — this only turns off the git history layered
        on top of it (and with it, `git log` as the timeline/introspection
        tool the design leans on — see mirrorRepository).

        DEFAULT ON. Turning this off is meant as a temporary/testing knob, not
        a steady-state choice.
      '';
    };

    mirrorRepository = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      example = "git@backup-host:agent-mirror.git";
      description = ''
        Bare repo the agent pushes to but cannot rewrite. `rm` is recoverable
        from git; a force-push or aggressive gc is not. With a mirror the agent
        can be completely free with its own tree, which is the point.

        A local path is initialised automatically. A remote is strongly
        preferred — a mirror on the same disk protects against the agent, not
        against the disk.

        No effect while enableGit is false.
      '';
    };

    githubUser = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      example = "octocat";
      description = ''
        GitHub username to poll for the intake tick — the operator's own,
        for knowing him. Unauthenticated (public events API, no token needed);
        fine at this polling cadence (twice daily by default) against
        GitHub's 60-req/hour unauthenticated limit.

        Null disables the feed entirely — no GitHubFeed is constructed, not
        just an empty one.
      '';
    };

    limits = {
      totalMemoryMax = lib.mkOption {
        type = lib.types.str;
        default = "30G";
        description = ''
          Hard ceiling for everything the agent runs — both llama-servers plus
          the harness — enforced on a shared systemd slice rather than per
          service. A cap on the slice can't be gamed by one component growing at
          another's expense, and it leaves the rest of the machine yours.

          Set this below your physical RAM with real headroom. The models are
          mlocked and therefore unreclaimable: if the slice hits this limit,
          something dies rather than swaps.
        '';
      };

      totalMemoryHigh = lib.mkOption {
        type = lib.types.str;
        default = "28G";
        description = ''
          Soft ceiling. Above this the kernel throttles and reclaims before it
          starts killing. Keep a couple of GB between this and totalMemoryMax so
          there is somewhere to land.
        '';
      };

      mainModelReserve = lib.mkOption {
        type = lib.types.str;
        default = "18G";
        description = ''
          MemoryMin for the main llama-server: memory the kernel will not reclaim
          from it under pressure.

          Note this is protection, not a cap. Deliberately no MemoryMax on the
          llama services — their pages are mlocked and cannot be reclaimed, so a
          cgroup limit on them means the OOM killer rather than throttling.
        '';
      };

      pulseModelReserve = lib.mkOption {
        type = lib.types.str;
        default = "6G";
        description = "MemoryMin for the pulse server. E4B at Q4 is ~5GB.";
      };

      harnessMemoryMax = lib.mkOption {
        type = lib.types.str;
        default = "1G";
        description = ''
          The harness itself holds text and a SQLite handle; a gigabyte is
          generous. This one gets a real Max because it is the component most
          likely to leak, and killing it is cheap — it restarts, and crashed
          ticks are already recorded as episodes.
        '';
      };

      cpuQuota = lib.mkOption {
        type = lib.types.nullOr lib.types.str;
        default = null;
        example = "600%";
        description = ''
          Slice-wide CPU ceiling, where 100% is one core. Null means no hard cap
          — the Nice and CPUWeight settings already make the agent yield to
          anything you are doing, and a hard quota mostly just makes ticks take
          longer without freeing anything you'd notice.

          Set it if the box has other jobs that need guaranteed latency.
        '';
      };

      tasksMax = lib.mkOption {
        type = lib.types.int;
        default = 512;
        description = "Thread/process ceiling for the slice. Inference wants threads; this is mostly a runaway guard.";
      };

      maxTreeSize = lib.mkOption {
        type = lib.types.nullOr lib.types.str;
        default = "20G";
        example = "20G";
        description = ''
          Checked hourly; logs a warning when exceeded. Deliberately a warning
          and not a limit — the tree is the agent's memory, and silently failing
          its writes would be a strange way to treat that. Years of episodes fit
          in a few GB, so hitting this means something is wrong (an unpruned
          observations directory, usually) rather than that it has lived too long.

          Null disables the check.
        '';
      };
    };

    settings = lib.mkOption {
      type = jsonFormat.type;
      default = { };
      description = ''
        Contents of agent.json. The module supplies its own settings via
        mkDefault, computed from options like operatorDiscordUserId below —
        prefer a typed option over reaching into this directly.

        WARNING: despite the name, jsonFormat's type does NOT merge separate
        definitions of this option key-by-key across priority tiers. Setting
        so much as settings.foo.bar from a host config entirely discards the
        module's own mkDefault contents (treeRoot, llm.*, everything) rather
        than layering over it — confirmed directly against pkgs.formats.json.
        If you need to inject something this module has no dedicated option
        for yet, add one (see operatorDiscordUserId for the pattern) rather
        than setting a path under settings from the outside.
      '';
    };

    discordTokenFile = lib.mkOption {
      type = lib.types.nullOr lib.types.path;
      default = null;
      description = ''
        Path to a file containing the Discord bot token, read at start via
        LoadCredential. Never put the token in settings — it would land in the
        world-readable Nix store.
      '';
    };

    operatorDiscordUserId = lib.mkOption {
      type = lib.types.int;
      default = 0;
      description = ''
        The operator's Discord user ID (a snowflake, never legitimately 0).
        The harness DMs this user rather than posting to a channel. Woven
        into settings.channel.operatorUserId internally — set it here, not
        via settings directly (see the warning on that option).
      '';
    };

    conversationGapMinutes = lib.mkOption {
      type = lib.types.int;
      default = 30;
      description = ''
        How long a silence has to run before the next Discord message counts
        as a new conversation rather than a continuation. ReplyTick shows the
        model the whole back-and-forth back to the start of the current
        conversation by this measure — too long and an old, unrelated
        exchange bleeds into a new one; too short and the model loses the
        thread of a conversation with normal pauses in it.
      '';
    };
  };

  config = lib.mkIf cfg.enable {
    # NOTE: pkgs.formats.json's type does not merge across mkDefault/mkForce/
    # normal priority tiers — a competing definition of `settings` (or any
    # nested path under it) from a host config wins outright and silently
    # discards everything in this block, rather than merging per-key
    # (confirmed directly against pkgs.formats.json). So everything the
    # module needs to inject — including anything sourced from another
    # option, like operatorDiscordUserId here — has to be assembled into
    # this single mkDefault value, not layered on from outside it.
    services.cozy-harness.settings = lib.mkDefault {
      treeRoot = treeDir;
      enableGit = cfg.enableGit;
      mirrorRemote = if cfg.mirrorRepository == null then null else "mirror";
      channel.operatorUserId = cfg.operatorDiscordUserId;
      channel.conversationGapMinutes = cfg.conversationGapMinutes;
      feeds.githubUser = cfg.githubUser;
      llm = {
        mainSocketPath = socketPathFor "main";
        pulseSocketPath = socketPathFor "pulse";
        topP = cfg.topP;
        topK = cfg.topK;
        stop = cfg.stopSequences;
        # MTP only supports a single parallel slot (see the mainSlots override
        # below), so every tick type has to land on the one slot that exists.
        slots =
          if cfg.enableMtp
          then { pulse = 0; work = 0; intake = 0; reflect = 0; reply = 0; chore = 0; }
          else { pulse = 0; work = 0; intake = 1; reflect = 2; reply = 3; chore = 0; };
      };
    };

    # llama.cpp's MTP drafting only supports n_parallel=1. enableMtp is the
    # toggle; this is a consequence of it, not a second thing to remember to
    # set — mkForce wins over both mainSlots' own mkDefault and any plain
    # `mainSlots = N;` a host sets (mkForce's priority 50 beats the default
    # "normal" priority 100 those carry).
    services.cozy-harness.mainSlots = lib.mkIf cfg.enableMtp (lib.mkForce 1);

    users.users.${cfg.user} = {
      isSystemUser = true;
      group = cfg.group;
      home = cfg.home;
      createHome = true;
      homeMode = "0750";
      shell = cfg.shell;
      description = "Agent";
      packages = cfg.extraPackages;
    };

    users.groups.${cfg.group} = { };

    # Created once, independent of either llama unit's lifecycle, so it's never
    # torn down by systemd's per-unit DynamicUser runtime-directory handling —
    # see the comment on socketDir above.
    systemd.tmpfiles.rules = [
      "d ${socketDir} 0770 root ${cfg.group} -"
    ];

    systemd.services = lib.mkMerge [
      (mkLlamaService {
        name = "main";
        model = "${cfg.modelDirectory}/${cfg.mainModel}";
        ctxSize = cfg.mainContextSize;
        parallel = cfg.mainSlots;
        quantKv = true;
        memoryMin = cfg.limits.mainModelReserve;
        mtp = cfg.enableMtp;
        extraFlags = cfg.mainExtraFlags;
      })
      (mkLlamaService {
        name = "pulse";
        model = "${cfg.modelDirectory}/${cfg.pulseModel}";
        ctxSize = 8192;
        parallel = 1;
        quantKv = false;
        memoryMin = cfg.limits.pulseModelReserve;
        mtp = false;   # the pulse generates ~20 tokens; drafting buys nothing
        extraFlags = cfg.pulseExtraFlags;
      })
      (lib.optionalAttrs (cfg.mainModelUrl != null) (mkModelDownloadService {
        name = "main";
        url = cfg.mainModelUrl;
        sha256 = cfg.mainModelSha256;
        dest = "${cfg.modelDirectory}/${cfg.mainModel}";
      }))
      (lib.optionalAttrs (cfg.pulseModelUrl != null) (mkModelDownloadService {
        name = "pulse";
        url = cfg.pulseModelUrl;
        sha256 = cfg.pulseModelSha256;
        dest = "${cfg.modelDirectory}/${cfg.pulseModel}";
      }))
      (lib.optionalAttrs (cfg.enableMtp && cfg.mtpDraftModelUrl != null) (mkModelDownloadService {
        name = "mtp-draft";
        url = cfg.mtpDraftModelUrl;
        sha256 = cfg.mtpDraftModelSha256;
        dest = "${cfg.modelDirectory}/${cfg.mtpDraftModel}";
        # No llama-mtp-draft.service exists — llama-main loads this file itself
        # via --spec-draft-model, so gate the unit that actually needs it.
        gates = [ "llama-main.service" ];
      }))
      {
        cozy-harness = {
          description = "Agent harness";
          wantedBy = [ "multi-user.target" ];
          after = [ "network.target" "llama-main.service" "llama-pulse.service" ];
          wants = [ "llama-main.service" "llama-pulse.service" ];

          path = cfg.extraPackages ++ [ pkgs.openssh ];

          environment = {
            HOME = cfg.home;
            AGENT_TREE = treeDir;

            # Keep .NET inside the cgroup limit rather than discovering it by
            # being killed. Workstation GC because the harness is one slow loop,
            # not a throughput server, and server GC would reserve per-core heaps
            # it will never use.
            DOTNET_gcServer = "0";
            DOTNET_GCHeapHardLimitPercent = "0x4B";  # 75% of the cgroup limit
            DOTNET_GCConserveMemory = "5";
          };

          preStart = ''
            mkdir -p ${treeDir}

            # The agent's own source, browsable. Available always, loaded never.
            ln -sfn ${cfg.package}/share/cozy-harness ${treeDir}/harness

            ${lib.optionalString (cfg.mirrorRepository != null) ''
              ${lib.optionalString (lib.hasPrefix "/" cfg.mirrorRepository) ''
                if [ ! -d ${cfg.mirrorRepository} ]; then
                  ${pkgs.git}/bin/git init --bare ${cfg.mirrorRepository}
                fi
              ''}
              if [ -d ${treeDir}/.git ]; then
                cd ${treeDir}
                ${pkgs.git}/bin/git remote remove mirror 2>/dev/null || true
                ${pkgs.git}/bin/git remote add mirror ${cfg.mirrorRepository}
              fi
            ''}

          '';

          serviceConfig = {
            Type = "simple";
            ExecStart = "${lib.getExe cfg.package} ${configFile}";

            # Restart, always. An agent whose life ends because of one exception is not persistent.
            # Crashed ticks are already recorded as episodes; a crashed process should just come back.
            Restart = "always";
            RestartSec = 30;

            User = cfg.user;
            Group = cfg.group;
            WorkingDirectory = cfg.home;

            LoadCredential = lib.optional (cfg.discordTokenFile != null)
              "discord-token:${cfg.discordTokenFile}";

            ProtectSystem = "strict";
            # NOT ProtectHome: the agent's home is where it lives.
            ProtectHome = false;
            ReadWritePaths = [ cfg.home ]
              ++ lib.optional (cfg.mirrorRepository != null && lib.hasPrefix "/" cfg.mirrorRepository)
                   cfg.mirrorRepository;

            PrivateTmp = true;
            PrivateDevices = true;
            NoNewPrivileges = true;
            RestrictAddressFamilies = [ "AF_INET" "AF_INET6" "AF_UNIX" ];

            Slice = "agent.slice";

            MemoryMax = cfg.limits.harnessMemoryMax;
            MemoryHigh = cfg.limits.harnessMemoryMax;
            MemorySwapMax = 0;
            TasksMax = 64;

            # Low priority: on a shared box, the agent should yield to anything
            # the operator is actually doing. It has no deadlines.
            Nice = 10;
            IOSchedulingClass = "idle";
            CPUWeight = 20;
          };
        };
      }

      # A warning, not a limit. The tree is the agent's memory; silently failing
      # its writes would be a strange way to treat that.
      (lib.mkIf (cfg.limits.maxTreeSize != null) {
        agent-disk-check = {
          description = "Check the size of the agent's tree";
          serviceConfig = {
            Type = "oneshot";
            User = cfg.user;
            Group = cfg.group;
          };
          script = ''
            limit=$(${pkgs.coreutils}/bin/numfmt --from=iec ${cfg.limits.maxTreeSize})
            used=$(${pkgs.coreutils}/bin/du -sb ${treeDir} | ${pkgs.coreutils}/bin/cut -f1)
            if [ "$used" -gt "$limit" ]; then
              echo "agent tree is $(${pkgs.coreutils}/bin/numfmt --to=iec $used), over the ${cfg.limits.maxTreeSize} mark." >&2
              echo "Years of episodes fit in a few GB. Check observations/ for unpruned intake." >&2
              exit 1
            fi
          '';
        };
      })
    ];

    # Everything the agent runs shares one budget. A slice-wide cap is the only
    # honest way to say "the agent gets this much of the machine" — per-service
    # limits just move the growth around.
    systemd.slices.agent = {
      description = "CozyHarness Agent (harness + inference servers)";
      sliceConfig = {
        MemoryHigh = cfg.limits.totalMemoryHigh;
        MemoryMax = cfg.limits.totalMemoryMax;
        MemorySwapMax = 0;   # mlocked weights must never reach swap
        TasksMax = cfg.limits.tasksMax;
        CPUWeight = 30;
        IOWeight = 30;
      } // lib.optionalAttrs (cfg.limits.cpuQuota != null) {
        CPUQuota = cfg.limits.cpuQuota;
      };
    };

    systemd.timers.agent-disk-check = lib.mkIf (cfg.limits.maxTreeSize != null) {
      wantedBy = [ "timers.target" ];
      timerConfig = {
        OnBootSec = "10m";
        OnUnitActiveSec = "1h";
        Unit = "agent-disk-check.service";
      };
    };

    warnings =
      lib.optional (cfg.mirrorRepository == null)
        "services.cozy-harness: no git mirror configured. The agent can destroy its own history with a force-push or an aggressive gc, and nothing will stop it."
      ++ lib.optional
        (cfg.mirrorRepository != null
          && lib.hasPrefix "/" cfg.mirrorRepository
          && lib.hasPrefix cfg.home cfg.mirrorRepository)
        "services.cozy-harness: the git mirror is inside the agent's own home. That is not a mirror. Put it outside ${cfg.home}, ideally on another host."
      ++ lib.optional (cfg.limits.cpuQuota != null && cfg.limits.cpuQuota == "100%")
        "services.cozy-harness: a CPUQuota of 100% gives the agent one core total, shared between both inference servers. Ticks will take many minutes. That may be what you want, but it is worth being deliberate about."
      ++ lib.optional (cfg.mainModelUrl != null && cfg.mainModelSha256 == null)
        "services.cozy-harness: mainModelUrl is set without mainModelSha256. The download is trusted on first sight and never re-checked — a truncated download or a swapped file upstream won't be caught."
      ++ lib.optional (cfg.pulseModelUrl != null && cfg.pulseModelSha256 == null)
        "services.cozy-harness: pulseModelUrl is set without pulseModelSha256. The download is trusted on first sight and never re-checked — a truncated download or a swapped file upstream won't be caught."
      ++ lib.optional (cfg.enableMtp && cfg.mtpDraftModelUrl != null && cfg.mtpDraftModelSha256 == null)
        "services.cozy-harness: mtpDraftModelUrl is set without mtpDraftModelSha256. The download is trusted on first sight and never re-checked — a truncated download or a swapped file upstream won't be caught."
      ++ lib.optional cfg.enableMtp
        "services.cozy-harness: enableMtp is on, which forces mainSlots to 1 and collapses settings.llm.slots onto that one slot (llama.cpp only supports n_parallel=1 for MTP drafting). Tick types serialize instead of overlapping across the usual four slots.";

    assertions = [
      {
        assertion = cfg.limits.totalMemoryHigh != cfg.limits.totalMemoryMax;
        message = ''
          services.cozy-harness: totalMemoryHigh equals totalMemoryMax, so there
          is no throttling range — the slice goes straight from fine to OOM.
          Leave a couple of GB between them.
        '';
      }
      {
        assertion = cfg.threads > 0;
        message = "services.cozy-harness: threads must be positive.";
      }
      {
        assertion = cfg.mainModelSha256 == null || cfg.mainModelUrl != null;
        message = "services.cozy-harness: mainModelSha256 is set but mainModelUrl is not.";
      }
      {
        assertion = cfg.pulseModelSha256 == null || cfg.pulseModelUrl != null;
        message = "services.cozy-harness: pulseModelSha256 is set but pulseModelUrl is not.";
      }
      {
        assertion = cfg.mtpDraftModelSha256 == null || cfg.mtpDraftModelUrl != null;
        message = "services.cozy-harness: mtpDraftModelSha256 is set but mtpDraftModelUrl is not.";
      }
      {
        assertion = !cfg.enableMtp || cfg.mtpDraftModel != null;
        message = ''
          services.cozy-harness: enableMtp is set but mtpDraftModel is not. MTP
          speculative decoding needs a drafter GGUF — the companion mtp-*.gguf
          shipped alongside mainModel in its quant repo — in addition to
          mainModel itself.
        '';
      }
      {
        # The token itself only exists at runtime, via LoadCredential — this is
        # the one Discord-related thing eval actually can check. The harness
        # itself now refuses to start on this (a real snowflake is never 0),
        # but that only surfaces as a crash-loop in the journal; catching it
        # at rebuild time is a lot less annoying to debug.
        assertion = cfg.discordTokenFile == null || operatorUserId != 0;
        message = ''
          services.cozy-harness: discordTokenFile is set but
          operatorDiscordUserId is unset (or 0). The bot talks to the operator
          over DM now, not a configured channel — it will start, connect, and
          have no one to DM, since a real Discord user ID is never 0.
        '';
      }
    ];
  };
}
