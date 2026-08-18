namespace CozyHarness.Channels;

/// <summary>
/// A dedicated surface for admin-only commands, separate from
/// IOperatorChannel's DM messaging. Exists because Discord has no way to
/// hide a slash command's visibility from specific user IDs — see
/// ChannelConfig.AdminDiscordToken's remarks and DiscordAdminChannel's class
/// doc for why. The only real lever is a wholly separate bot application
/// that only the operator and admins even know about.
///
/// Deliberately much smaller than IOperatorChannel: no messaging, no
/// presence, no per-sender DM routing — every admin command today is a
/// read-only query (goals, chores, debug context), not a conversation. If
/// that ever changes, extend this interface rather than reaching for
/// IOperatorChannel's machinery here.
/// </summary>
public interface IAdminChannel {
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Same contract as IOperatorChannel.RegisterCommandAsync, including the
    /// invoking user's id as the handler's first argument and the hard
    /// requirement that a command invocation and its response never reach
    /// the model's context (answered directly via the interaction response,
    /// never through anything ContextBuilder reads from). Every command
    /// registered here is grouped under a single "admin" top-level command,
    /// same as the fallback path — see Program.cs's RegisterAdminCommandAsync
    /// — so command names stay identical whether or not a dedicated admin
    /// bot is configured.
    /// </summary>
    Task RegisterCommandAsync(string name, string description, Func<ulong, CancellationToken, Task<string>> handler);
}
