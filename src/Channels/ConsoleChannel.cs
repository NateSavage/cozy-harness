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

    public event Func<string, Task>? MessageReceived;

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

                // Same information Discord gets as buttons — here it's just a
                // heads-up, since the reply comes either way; see DiscordChannel
                // for the actual interrupt-or-wait prompt.
                if (_activity.CurrentTick is not null)
                    Console.WriteLine($"[busy] right now I'm {_activity.Summary()}. " +
                                       "(type /interrupt to stop it, or just wait)");

                if (MessageReceived is not null) {
                    try { await MessageReceived(line); }
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
