using CozyHarness.Channels;

namespace CozyHarness.Core;

/// <summary>
/// Best-effort "something broke" notifications to the operator, via whatever
/// channel is configured — this is what makes IOperatorChannel.NotifyErrorAsync
/// actually get called. Fire-and-forget on purpose: a caller sitting in an
/// exception handler (the scheduler loop, a tick that just crashed) needs to
/// keep going, not block on a Discord round trip. A failure to SEND the
/// notification must never itself become a new unhandled exception — that
/// would defeat the entire point of this class.
/// </summary>
public sealed class ErrorReporter {
    private readonly IOperatorChannel _channel;

    public ErrorReporter(IOperatorChannel channel) => _channel = channel;

    public void Report(string context, Exception ex) {
        _ = Task.Run(async () => {
            try { await _channel.NotifyErrorAsync(context, ex, CancellationToken.None); }
            catch { /* the alert itself failing shouldn't cascade into more noise */ }
        });
    }
}
