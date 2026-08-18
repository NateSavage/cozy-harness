using System.Text;
using Discord;
using Discord.WebSocket;

namespace CozyHarness.Channels;

/// <summary>
/// A second Discord bot application, existing for exactly one reason: Discord
/// gives no way to restrict a slash command's VISIBILITY to specific user
/// IDs. default_member_permissions and per-command permission overwrites are
/// both guild-scoped and simply don't apply in DMs; a command registered for
/// BOT_DM context shows up in the picker for anyone who can open a DM with
/// the app at all, whitelist or not. Execution was already correctly gated
/// (see DiscordChannel.OnSlashCommandExecutedAsync) — this exists to stop the
/// command's mere existence and description from being visible to people who
/// aren't the operator or an admin, which the single-bot design can't do.
///
/// So: a wholly separate bot application, with its own token, that only the
/// operator and admins are ever given the invite link for. Nobody who
/// doesn't already have that link can see this bot exists, let alone its
/// commands.
///
/// Deliberately minimal compared to DiscordChannel: no DM messaging, no
/// MessageReceived, no per-sender routing, no presence — admin commands
/// today are all read-only queries, not a conversation, so none of that
/// machinery is needed here. See IAdminChannel.
/// </summary>
public sealed class DiscordAdminChannel : IAdminChannel, IAsyncDisposable {
    private const int MaxMessageLength = 2000;   // Discord's hard limit per message
    private const string AdminCommandName = "admin";

    private readonly string _token;
    private readonly ulong _operatorUserId;
    private readonly HashSet<ulong> _adminUserIds;
    private readonly DiscordSocketClient _client;

    // Filled by RegisterCommandAsync (before StartAsync), registered with
    // Discord as subcommands of a single global "admin" command once login
    // succeeds — same grouping rule as DiscordChannel: a name containing a
    // space (e.g. "debug context") becomes a subcommand GROUP rather than one
    // flat subcommand, since Discord doesn't allow spaces in a single
    // subcommand name.
    private readonly Dictionary<string, (string Description, Func<ulong, CancellationToken, Task<string>> Handler)> _commands = new();

    public DiscordAdminChannel(string token, ulong operatorUserId, IEnumerable<ulong> adminUserIds) {
        _token = token;
        _operatorUserId = operatorUserId;
        _adminUserIds = new HashSet<ulong>(adminUserIds);

        _client = new DiscordSocketClient(new DiscordSocketConfig {
            // Slash-command interactions arrive over INTERACTION_CREATE,
            // unaffected by DirectMessages/MessageContent — this bot never
            // reads a message body, so neither intent is needed. Guilds is
            // the non-privileged baseline Discord.Net's own caching expects
            // even when guild features go unused, same as DiscordChannel.
            GatewayIntents = GatewayIntents.Guilds,
            LogLevel = LogSeverity.Info,
        });

        _client.Log += OnLog;
        _client.SlashCommandExecuted += OnSlashCommandExecutedAsync;
    }

    public Task RegisterCommandAsync(string name, string description, Func<ulong, CancellationToken, Task<string>> handler) {
        _commands[name] = (description, handler);
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct) {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task OnReady() { ready.TrySetResult(); return Task.CompletedTask; }
        _client.Ready += OnReady;

        try {
            await _client.LoginAsync(TokenType.Bot, _token);
            await _client.StartAsync();

            // Same reasoning as DiscordChannel.StartAsync: a well-formed but
            // wrong token just retries 401s forever without throwing, so
            // bound the wait rather than hang indefinitely.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using (timeoutCts.Token.Register(() => ready.TrySetCanceled(timeoutCts.Token))) {
                try { await ready.Task; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    throw new TimeoutException(
                        "Discord admin-bot gateway never became ready within 30s — check AdminDiscordToken.");
                }
            }
        } finally {
            _client.Ready -= OnReady;
        }

        if (_commands.Count == 0) return;   // nothing registered — leave the bot with no commands at all

        var dmContexts = new[] { InteractionContextType.Guild, InteractionContextType.BotDm };
        var guildInstall = new[] { ApplicationIntegrationType.GuildInstall };

        var admin = new SlashCommandBuilder()
            .WithName(AdminCommandName)
            .WithDescription("Admin-only commands")
            .WithContextTypes(dmContexts)
            .WithIntegrationTypes(guildInstall);

        foreach (var (name, (description, _)) in _commands.Where(kv => !kv.Key.Contains(' ')))
            admin.AddOption(new SlashCommandOptionBuilder()
                .WithName(name)
                .WithDescription(description)
                .WithType(ApplicationCommandOptionType.SubCommand));

        // Every group fully built — every subcommand attached via AddOption
        // on the group itself — before admin.AddOption(group) runs, same
        // ordering requirement as DiscordChannel.StartAsync.
        var groups = _commands.Keys
            .Where(k => k.Contains(' '))
            .Select(k => (Group: k.Split(' ', 2)[0], Sub: k.Split(' ', 2)[1], FullName: k))
            .GroupBy(x => x.Group);

        foreach (var g in groups) {
            var group = new SlashCommandOptionBuilder()
                .WithName(g.Key)
                .WithDescription($"{g.Key} commands")
                .WithType(ApplicationCommandOptionType.SubCommandGroup);
            foreach (var entry in g)
                group.AddOption(new SlashCommandOptionBuilder()
                    .WithName(entry.Sub)
                    .WithDescription(_commands[entry.FullName].Description)
                    .WithType(ApplicationCommandOptionType.SubCommand));
            admin.AddOption(group);
        }

        try {
            await _client.Rest.BulkOverwriteGlobalCommands([admin.Build()]);
        } catch (Exception ex) {
            // No DM channel to fall back on here (unlike DiscordChannel) —
            // this bot has no messaging surface at all, so stderr is the
            // only place this failure can go.
            Console.Error.WriteLine($"[discord-admin] failed to register commands: {ex.Message}");
        }
    }

    private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command) {
        if (command.Data.Name != AdminCommandName) {
            await command.RespondAsync("Unknown command.", ephemeral: true);
            return;
        }

        if (command.User.Id != _operatorUserId && !_adminUserIds.Contains(command.User.Id)) {
            await command.RespondAsync("This command is only available to the operator and admins.", ephemeral: true);
            return;
        }

        // Nested group vs flat subcommand — see the registration loop above
        // and DiscordChannel.OnSlashCommandExecutedAsync for why.
        var top = command.Data.Options.FirstOrDefault();
        var nested = top?.Options?.FirstOrDefault();
        var sub = nested is not null ? $"{top!.Name} {nested.Name}" : top?.Name;

        if (sub is null || !_commands.TryGetValue(sub, out var entry)) {
            await command.RespondAsync("Unknown command.", ephemeral: true);
            return;
        }

        var label = $"/{AdminCommandName} {sub}";
        try {
            var text = await entry.Handler(command.User.Id, default);
            if (string.IsNullOrWhiteSpace(text)) text = "(nothing to show)";

            if (text.Length > MaxMessageLength) {
                // Same reasoning as DiscordChannel.RespondFromHandlerAsync —
                // a file, not a truncated message, so "debug context" can
                // still show a complete prompt.
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                var fileName = label.Trim('/').Replace(' ', '-') + ".txt";
                await command.RespondWithFileAsync(stream, fileName,
                    $"{label} ({text.Length:N0} characters — too long for a message)", ephemeral: true);
                return;
            }

            await command.RespondAsync(text, ephemeral: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"[discord-admin] {label} failed: {ex}");
            try { await command.RespondAsync($"⚠️ {label} failed: {ex.Message}", ephemeral: true); }
            catch { /* best-effort */ }
        }
    }

    private static Task OnLog(LogMessage msg) {
        var stream = msg.Severity <= LogSeverity.Warning ? Console.Error : Console.Out;
        var suffix = msg.Exception is null ? "" : $" ({msg.Exception.Message})";
        stream.WriteLine($"[discord-admin] {msg.Severity}: {msg.Message}{suffix}");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        _client.Log -= OnLog;
        _client.SlashCommandExecuted -= OnSlashCommandExecutedAsync;
        try { await _client.StopAsync(); } catch { /* best-effort on the way out */ }
        _client.Dispose();
    }
}
