using CozyHarness.Core;
using CozyHarness.Domain;
using CozyHarness.Storage;

namespace CozyHarness.Ticks;

/// <summary>
/// Wraps every tick: writes the episode, commits it, updates the index, pushes
/// the mirror. One commit per tick, commit message = episode summary.
/// </summary>
public sealed class TickRunner {
    private readonly AgentTree _tree;
    private readonly GitStore _git;
    private readonly IndexDb _db;
    private readonly ErrorReporter _errors;

    public TickRunner(AgentTree tree, GitStore git, IndexDb db, ErrorReporter errors) {
        _tree = tree; _git = git; _db = db; _errors = errors;
    }

    public async Task<TickOutcome> RunAsync(ITick tick, CancellationToken ct) {
        TickOutcome outcome;
        try {
            outcome = await tick.RunAsync(ct);
        }
        catch (OperationCanceledException) {
            // Never rethrow: this token may be a per-tick one the operator (or
            // shutdown) just cancelled, not the app's own token — letting this
            // propagate would take the whole scheduler loop down with it. An
            // interrupted tick is recorded like any other outcome; ticks that
            // haven't touched anything yet (see each ITick) just get retried
            // whenever they're next due.
            outcome = new TickOutcome {
                Summary = $"{tick.Type} tick was interrupted before finishing",
                Salience = 0.3,
            };
        }
        catch (Exception ex) {
            // A crashed tick is recorded like anything else. The loop continues:
            // an agent whose life stops because one tick threw is not persistent.
            _errors.Report($"{tick.Type} tick failed", ex);
            outcome = new TickOutcome {
                Summary = $"tick failed: {ex.GetType().Name}",
                Body = $"The {tick.Type} tick threw an exception.\n\n```\n{ex}\n```",
                Salience = 0.9,
            };
        }

        if (outcome.Silent) return outcome;

        var episode = new Episode {
            Timestamp = DateTimeOffset.UtcNow,
            Type = tick.Type,
            Summary = outcome.Summary,
            Body = outcome.Body,
            DidNothing = outcome.DidNothing,
            GoalId = outcome.GoalId,
            Person = outcome.Person,
            Sensitive = outcome.Sensitive,
            Salience = outcome.Salience,
            TokensUsed = outcome.TokensUsed,
        };

        _tree.WriteEpisode(episode);
        var sha = _git.CommitTick(tick.Type.ToString().ToLowerInvariant(), outcome.Summary);
        _db.UpsertEpisode(episode, episode.RelativePath, sha);
        _git.PushMirror();

        return outcome;
    }
}
