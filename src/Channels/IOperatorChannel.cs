namespace CozyHarness.Channels;

public interface IOperatorChannel {
    /// <summary>Raised when the operator says something. Drives the fast path.</summary>
    event Func<string, Task>? MessageReceived;

    Task StartAsync(CancellationToken ct);
    Task SendAsync(string content, CancellationToken ct);

    /// <summary>
    /// The operator asked to be told when a conversation is marked sensitive.
    /// they are told THAT it happened, not made to read it — the notice is the point.
    /// </summary>
    Task NotifySensitiveAsync(DateTimeOffset when, CancellationToken ct);

    /// <summary>
    /// Always available, never rate-limited, always read. Both because it might
    /// matter and because it is the best debugging signal this system produces.
    /// </summary>
    Task SayStuckAsync(string what, CancellationToken ct);

    /// <summary>
    /// An internal failure the operator should know about — a crashed tick, a
    /// scheduler cycle that threw, anything unexpected. This is the harness
    /// reporting on itself, not the agent's own voice (contrast SayStuckAsync).
    /// Implementations must never let sending this notification itself throw
    /// back out — see ErrorReporter, which is what actually calls this.
    /// </summary>
    Task NotifyErrorAsync(string context, Exception ex, CancellationToken ct);
}
