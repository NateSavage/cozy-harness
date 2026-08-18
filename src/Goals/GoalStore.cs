using CozyHarness.Config;
using CozyHarness.Domain;
using CozyHarness.Storage;

namespace CozyHarness.Goals;

/// <summary>
/// Goal state is the directory. `mv` is the transition; git records it as a
/// rename, so `git log --diff-filter=R goals/` is the full lifecycle history.
/// </summary>
public sealed class GoalStore {
    private readonly AgentTree _tree;
    private readonly IndexDb _db;
    private readonly GoalConfig _cfg;

    public GoalStore(AgentTree tree, IndexDb db, GoalConfig cfg) {
        _tree = tree; _db = db; _cfg = cfg;
    }

    public Goal Create(string title, GoalKind kind, string? description, string? originEpisode) {
        var n = NextOrdinal();
        var id = $"{n:D4}-{Slugify(title)}";
        var renewDays = kind == GoalKind.Longitudinal ? _cfg.LongitudinalRenewDays : _cfg.DefaultRenewDays;

        var g = new Goal {
            Id = id,
            Title = title,
            State = GoalState.Proposed,
            Kind = kind,
            Created = DateTimeOffset.UtcNow,
            Description = description,
            OriginEpisode = originEpisode,
            RenewBy = DateTimeOffset.UtcNow.AddDays(renewDays),
            LastTouched = DateTimeOffset.UtcNow,
        };
        WriteFile(g);
        _db.UpsertGoal(g);
        return g;
    }

    /// <summary>
    /// Transition. Abandoned and Done REQUIRE a written reason — never let a goal close without prose.
    /// That text is the most human thing in the tree.
    /// </summary>
    public Goal Transition(Goal g, GoalState to, string? why = null) {
        if ((to is GoalState.Abandoned or GoalState.Done) && string.IsNullOrWhiteSpace(why))
            throw new ArgumentException("Closing a goal requires a reason.", nameof(why));

        string oldPath = _tree.Abs(g.RelativePath);
        Goal next = g with {
            State = to,
            LastTouched = DateTimeOffset.UtcNow,
            Closed = to is GoalState.Abandoned or GoalState.Done ? DateTimeOffset.UtcNow : g.Closed,
            ClosedWhy = why ?? g.ClosedWhy,
        };

        string newPath = _tree.Abs(next.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        if (File.Exists(oldPath) && oldPath != newPath)
            File.Move(oldPath, newPath);

        WriteFile(next);
        _db.UpsertGoal(next);
        return next;
    }

    /// <summary>
    /// Reaffirm. Persistence requires a deliberate act, not inertia — this is the
    /// anti-groove mechanism, so it must be called explicitly, never automatically.
    /// </summary>
    public Goal Renew(Goal g) {
        var days = g.Kind == GoalKind.Longitudinal ? _cfg.LongitudinalRenewDays : _cfg.DefaultRenewDays;
        var next = g with { RenewBy = DateTimeOffset.UtcNow.AddDays(days), LastTouched = DateTimeOffset.UtcNow };
        WriteFile(next);
        _db.UpsertGoal(next);
        return next;
    }

    /// <summary>Goals past renewal decay to dormant. Called by reflect, not by the pulse.</summary>
    public List<Goal> DecayStale() {
        var decayed = new List<Goal>();
        foreach (var id in _db.GoalsPastRenewal()) {
            var g = Load(id);
            if (g is null) continue;
            decayed.Add(Transition(g, GoalState.Dormant));
        }
        return decayed;
    }

    public Goal? Load(string id) {
        foreach (var state in Enum.GetValues<GoalState>()) {
            var probe = _tree.Abs($"{Goal.DirectoryFor(state)}/{id}.md");
            if (File.Exists(probe)) return ParseGoalFile(_tree.Root, probe);
        }
        return null;
    }

    /// <summary>
    /// The mix check. If nothing useless is alive, the stack has quietly become a task queue in costume.
    /// Surfaced to the reflect tick, never auto-corrected.
    /// </summary>
    public bool HasEnoughUselessGoals() => _db.CountGoals("active", "useless") >= _cfg.MinUselessGoals;

    private void WriteFile(Goal g) {
        var path = _tree.Abs(g.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var meta = new Dictionary<string, object?> {
            ["id"] = g.Id,
            ["title"] = g.Title,
            ["state"] = g.State.ToString().ToLowerInvariant(),
            ["kind"] = g.Kind.ToString().ToLowerInvariant(),
            ["created"] = g.Created.ToString("o"),
            ["renew_by"] = g.RenewBy?.ToString("o"),
            ["last_touched"] = g.LastTouched?.ToString("o"),
            ["origin_episode"] = g.OriginEpisode,
        };
        if (g.Closed is not null) meta["closed"] = g.Closed.Value.ToString("o");
        if (g.ClosedWhy is not null) meta["closed_why"] = g.ClosedWhy;

        File.WriteAllText(path, Frontmatter.Write(meta, g.Description ?? ""));
    }

    public static Goal ParseGoalFile(string root, string absPath) {
        var (meta, body) = Frontmatter.Read(File.ReadAllText(absPath));
        var dir = Path.GetFileName(Path.GetDirectoryName(absPath)) ?? "active";
        return new Goal {
            Id = meta.GetValueOrDefault("id") ?? Path.GetFileNameWithoutExtension(absPath),
            Title = meta.GetValueOrDefault("title") ?? "(untitled)",
            // Directory wins over frontmatter: the filesystem is ground truth.
            State = IndexRebuilder.ParseEnum(dir, GoalState.Active),
            Kind = IndexRebuilder.ParseEnum(meta.GetValueOrDefault("kind"), GoalKind.Craft),
            Created = IndexRebuilder.ParseTs(meta.GetValueOrDefault("created")),
            Description = body,
            OriginEpisode = meta.GetValueOrDefault("origin_episode"),
            RenewBy = meta.TryGetValue("renew_by", out var rb) && DateTimeOffset.TryParse(rb, out var d1) ? d1 : null,
            LastTouched = meta.TryGetValue("last_touched", out var lt) && DateTimeOffset.TryParse(lt, out var d2) ? d2 : null,
            Closed = meta.TryGetValue("closed", out var cl) && DateTimeOffset.TryParse(cl, out var d3) ? d3 : null,
            ClosedWhy = meta.GetValueOrDefault("closed_why"),
        };
    }

    private int NextOrdinal() {
        var max = 0;
        foreach (var f in _tree.GoalFiles()) {
            var name = Path.GetFileNameWithoutExtension(f);
            var dash = name.IndexOf('-');
            if (dash > 0 && int.TryParse(name[..dash], out var n)) max = Math.Max(max, n);
        }
        return max + 1;
    }

    private static string Slugify(string s) {
        char[] chars = s.ToLowerInvariant()
                        .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                        .ToArray();
        string slug = new(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}
