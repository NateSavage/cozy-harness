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
  # assertion at the bottom of this file, so there's exactly one place that
  # knows how to dig this out of the freeform settings blob.
  operatorUserId = (cfg.settings.channel or { }).operatorUserId or 0;

  waitForServer = name: socketPath: pkgs.writeShellScript "wait-llama-${name}" ''
    # CPU inference with --mlock takes a while to become ready. The harness
    # tolerates a dead server (a crashed tick is recorded like any other), but
    # starting into a wall of failures pollutes the episode log on day one.
    for i in $(seq 1 300); do
      if ${pkgs.curl}/bin/curl -sf --unix-socket ${socketPath} http://localhost/health >/dev/null; then
        exit 0
      fi
      sleep 2
    done
    echo "llama-server at ${socketPath} never became healthy" >&2
    exit 1
  '';

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

      serviceConfig = {
        Type = "simple";
        ExecStart = lib.escapeShellArgs ([
          "${cfg.llamaPackage}/bin/llama-server"
          "--model" model
          "--host" socketPath   # llama-server listens on a unix socket iff --host ends in .sock
          "--threads" (toString cfg.threads)
          "--ctx-size" (toString ctxSize)
          "--parallel" (toString parallel)
          "--mlock"
        ] ++ lib.optionals quantKv [
          # Roughly halves KV cache memory at negligible quality cost. On CPU,
          # every extra bit per parameter is more RAM bandwidth per token.
          "--cache-type-k" "q8_0"
          "--cache-type-v" "q8_0"
        ] ++ lib.optionals mtp cfg.mtpFlags
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

  mkModelDownloadService = { name, url, sha256, dest }:
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
            sleep 300
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

        if [ -n "$sha256" ] && ! verify "$tmp"; then
          echo "downloaded $url but it failed hash verification" >&2
          ${lib.optionalString notifyProgress ''notify "${name} model downloaded but failed hash verification"''}
          rm -f "$tmp"
          exit 1
        fi

        mv "$tmp" "$dest"
        echo "downloaded $dest"
        ${lib.optionalString notifyProgress ''
          if [ -n "$sha256" ]; then
            notify "${name} model downloaded and verified"
          else
            notify "${name} model downloaded"
          fi
        ''}
      '';
    in {
      "cozy-harness-download-${name}-model" = {
        description = "Download the ${name} model for cozy-harness";
        # WantedBy/Before here (rather than touching mkLlamaService) means this
        # unit only exists — and only gets pulled in — when a URL is actually
        # configured; llama-${name} starts exactly as before if it isn't.
        requiredBy = [ "llama-${name}.service" ];
        before = [ "llama-${name}.service" ];
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
      example = "gemma-4-E4B-it-Q4_K_M.gguf";
      description = ''
        Small model for the pulse tick. Its only job is deciding whether anything
        needs attention, and it should usually answer no.
      '';
    };

    mainModelUrl = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
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
      default = null;
      description = ''
        Expected sha256 of mainModel. Verified after download, and re-checked
        against any file already at that path — a mismatch triggers a
        redownload. Leave null to skip verification and trust the file once
        present.
      '';
    };

    pulseModelUrl = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      example = "https://huggingface.co/example/gemma-4-E4B-it-Q4_K_M-GGUF/resolve/main/gemma-4-e4b-it-q4_k_m.gguf";
      description = "Direct download URL for pulseModel. Same mechanics as mainModelUrl.";
    };

    pulseModelSha256 = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      description = "Expected sha256 of pulseModel. Same mechanics as mainModelSha256.";
    };

    threads = lib.mkOption {
      type = lib.types.int;
      default = 8;
      description = "Inference threads. Match physical cores, not hyperthreads.";
    };

    enableMtp = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = ''
        Multi-token prediction (speculative decoding against the model's own draft head).
         Reported at roughly 1.4-2.2x faster generation with no accuracy loss, 
         which on CPU is the difference between a two-minute work tick and a one-minute one.

        Requires MTP-enabled GGUFs and a llama.cpp build with MTP merged, and costs about 1GB of extra RAM per server.

        DEFAULT OFF, and the flags below are the part of this module most likely
        to be wrong: MTP flag names have been moving. Verify against
        `llama-server --help` on your build before enabling, and use
        mainExtraFlags directly if they differ.
      '';
    };

    mtpFlags = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ "--draft-max" "4" "--draft-min" "1" ];
      description = "Flags appended when enableMtp is set. Verify against your llama.cpp build.";
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
      description = "Parallel slots on the main server. One per tick type.";
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
      description = "Contents of agent.json. Merged over the module's defaults.";
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
  };

  config = lib.mkIf cfg.enable {
    services.cozy-harness.settings = lib.mkDefault {
      treeRoot = treeDir;
      mirrorRemote = if cfg.mirrorRepository == null then null else "mirror";
      llm = {
        mainSocketPath = socketPathFor "main";
        pulseSocketPath = socketPathFor "pulse";
        slots = { pulse = 0; work = 0; intake = 1; reflect = 2; reply = 3; chore = 0; };
      };
    };

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

            ${waitForServer "main" (socketPathFor "main")}
            ${waitForServer "pulse" (socketPathFor "pulse")}
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
        "services.cozy-harness: pulseModelUrl is set without pulseModelSha256. The download is trusted on first sight and never re-checked — a truncated download or a swapped file upstream won't be caught.";

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
        # The token itself only exists at runtime, via LoadCredential — this is
        # the one Discord-related thing eval actually can check. The harness
        # itself now refuses to start on this (a real snowflake is never 0),
        # but that only surfaces as a crash-loop in the journal; catching it
        # at rebuild time is a lot less annoying to debug.
        assertion = cfg.discordTokenFile == null || operatorUserId != 0;
        message = ''
          services.cozy-harness: discordTokenFile is set but
          settings.channel.operatorUserId is unset (or 0). The bot talks to
          the operator over DM now, not a configured channel — it will start,
          connect, and have no one to DM, since a real Discord user ID is
          never 0.
        '';
      }
    ];
  };
}
