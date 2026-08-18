using CozyHarness.Core;

namespace CozyHarness.Channels;

/// <summary>
/// Stand-in for Discord. Use this for the first week — step 1 of the build order
/// is a pulse-only agent and a git log, and you do not want to be debugging a
/// gateway connection while looking at that.
///
/// Also where to smoke-test the busy/interrupt behaviour without a real bot:
/// type `/interrupt` on its own line to cancel whatever heavy tick is running.
/// </summary>
public sealed class ConsoleChannel : IOperatorChannel {
    private readonly AgentActivity _activity;

    public ConsoleChannel(AgentActivity activity) => _activity = activity;

    public event Func<ulong, string, string, Task>? MessageReceived;

    // No live Discord identity to read here — just a fixed stand-in.
    public string AgentDisplayName => "the agent";

    // Console has no real per-user identity — everything typed here is "the
    // operator" as far as the rest of the harness is concerned, and there's
    // no live display name to report (ConsoleUserId == OperatorUserId is the
    // only case that matters, and that path never reads this name anyway).
    private const ulong ConsoleUserId = 0;
    private const string ConsoleUserName = "console";

    // Console's equivalent of Discord's registered slash commands — same
    // handlers, just typed as "/admin name" (RegisterCommandAsync) or a bare
    // "/name" (RegisterWhitelistedCommandAsync), matching Discord's
    // grouped-vs-standalone split (see DiscordChannel.AdminCommandName). No
    // operator/whitelist gate needed here: there's only ever one person on
    // the other end of a console — the split exists purely so typing one
    // matches the other channel's spelling.
    //
    // Dispatched straight to Console.WriteLine below, before MessageReceived
    // is ever invoked for that line — see RegisterCommandAsync's doc on
    // IOperatorChannel. The command text and its response must never become
    // something db.AddMessage/AppendInteractionLog records, or ContextBuilder
    // will hand it to the model as if it were something typed to the agent.
    private const string AdminPrefix = "/admin ";
    private readonly Dictionary<string, Func<ulong, CancellationToken, Task<string>>> _adminCommands = new();
    private readonly Dictionary<string, Func<CancellationToken, Task<string>>> _userCommands = new();

    public Task RegisterCommandAsync(string name, string description, Func<ulong, CancellationToken, Task<string>> handler) {
        _adminCommands[name] = handler;
        return Task.CompletedTask;
    }

    public Task RegisterWhitelistedCommandAsync(string name, string description, Func<CancellationToken, Task<string>> handler) {
        _userCommands[name] = handler;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct) {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(ct);
                if (line is null) break;

                if (line.Trim() == "/interrupt") {
                    Console.WriteLine(_activity.TryInterrupt()
                        ? "[interrupt] cancelling the current tick."
                        : "[interrupt] nothing running to interrupt.");
                    continue;
                }

                if (line.Trim().StartsWith(AdminPrefix, StringComparison.Ordinal)) {
                    var name = line.Trim()[AdminPrefix.Length..].Trim();
                    if (_adminCommands.TryGetValue(name, out var handler)) {
                        // ConsoleUserId — the same sentinel MessageReceived
                        // uses below, designed to equal the operator's
                        // configured id when there's no real per-user
                        // identity (see the field's remarks).
                        try { Console.WriteLine($"\n{await handler(ConsoleUserId, ct)}\n"); }
                        catch (Exception ex) { await NotifyErrorAsync($"{AdminPrefix}{name} failed", ex, ct); }
                    } else {
                        Console.WriteLine($"[admin] unknown command: {name}");
                    }
                    continue;
                }

                // Bare "/name" — RegisterWhitelistedCommandAsync's standalone
                // top-level commands, matched only against that dictionary so
                // this can never accidentally reach an admin-only handler.
                if (line.Trim().StartsWith('/') && _userCommands.TryGetValue(line.Trim()[1..], out var userHandler)) {
                    var name = line.Trim()[1..];
                    try { Console.WriteLine($"\n{await userHandler(ct)}\n"); }
                    catch (Exception ex) { await NotifyErrorAsync($"/{name} failed", ex, ct); }
                    continue;
                }

                // Same information Discord gets as buttons — here it's just a
                // heads-up, since the reply comes either way; see DiscordChannel
                // for the actual interrupt-or-wait prompt.
                if (_activity.CurrentTick is not null)
                    Console.WriteLine($"[busy] right now I'm {_activity.Summary()}. " +
                                       "(type /interrupt to stop it, or just wait)");

                if (MessageReceived is not null) {
                    try { await MessageReceived(ConsoleUserId, ConsoleUserName, line); }
                    catch (Exception ex) { await NotifyErrorAsync("handling your message failed", ex, ct); }
                }
            }
        }, ct);
        return Task.CompletedTask;
    }

    public Task SendAsync(string content, CancellationToken ct) {
        Console.WriteLine($"\n[agent] {content}\n");
        return Task.CompletedTask;
    }

    public Task ReplyToAsync(ulong userId, string content, CancellationToken ct) => SendAsync(content, ct);

    public Task NotifySensitiveAsync(DateTimeOffset when, CancellationToken ct) {
        Console.WriteLine($"[notice] a conversation at {when:HH:mm} was marked sensitive");
        return Task.CompletedTask;
    }

    public Task SayStuckAsync(string what, CancellationToken ct) {
        Console.WriteLine($"[stuck] {what}");
        return Task.CompletedTask;
    }

    public Task NotifyErrorAsync(string context, Exception ex, CancellationToken ct) {
        Console.Error.WriteLine($"[error] {context}: {ex}");
        return Task.CompletedTask;
    }

    public Task SetAwayAsync(bool away, CancellationToken ct) {
        Console.WriteLine(away ? "[presence] away (quiet hours)" : "[presence] online");
        return Task.CompletedTask;
    }
}
