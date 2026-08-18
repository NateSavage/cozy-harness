using CozyHarness.Config;
using CozyHarness.Domain;
using CozyHarness.Storage;

namespace CozyHarness.Chores;

/// <summary>
/// Chore state is the directory, same as goals: `mv` is the transition, git
/// records it as a rename. Deliberately thinner than GoalStore — no renewal, no
/// kind, no promotion pipeline. A chore is either due or it isn't.
/// </summary>
public sealed class ChoreStore {
    private readonly AgentTree _tree;
    private readonly IndexDb _db;
    private readonly ChoreConfig _cfg;

    public ChoreStore(AgentTree tree, IndexDb db, ChoreConfig cfg) {
        _tree = tree; _db = db; _cfg = cfg;
    }

    /// <summary>
    /// Not called by any tick — chores are authored by the operator (drop a file
    /// in chores/active/, same as any other tree edit), not proposed by the
    /// model. This exists for that authoring path and for tooling.
    /// </summary>
    public Chore Create(string title, TimeSpan interval, string? description) {
        if (interval < TimeSpan.FromHours(_cfg.MinIntervalHours))
            throw new ArgumentException(
                $"A chore can't recur faster than every {_cfg.MinIntervalHours}h — that's what the pulse is for.",
                nameof(interval));

        var c = new Chore {
            Id = $"{NextOrdinal():D4}-{Slugify(title)}",
            Title = title,
            State = ChoreState.Active,
            Interval = interval,
            Created = DateTimeOffset.UtcNow,
            Description = description,
        };
        WriteFile(c);
        _db.UpsertChore(c);
        return c;
    }

    /// <summary>Retire. Same rule as closing a goal: requires a reason, never silent.</summary>
    public Chore Retire(Chore c, string why) {
        if (string.IsNullOrWhiteSpace(why))
            throw new ArgumentException("Retiring a chore requires a reason.", nameof(why));

        var oldPath = _tree.Abs(c.RelativePath);
        var next = c with { State = ChoreState.Retired, Closed = DateTimeOffset.UtcNow, ClosedWhy = why };

        var newPath = _tree.Abs(next.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        if (File.Exists(oldPath) && oldPath != newPath)
            File.Move(oldPath, newPath);

        WriteFile(next);
        _db.UpsertChore(next);
        return next;
    }

    /// <summary>Resets the interval clock. Called whether or not the tick produced anything useful.</summary>
    public Chore MarkRun(Chore c) {
        var next = c with { LastRun = DateTimeOffset.UtcNow };
        WriteFile(next);
        _db.UpsertChore(next);
        return next;
    }

    public Chore? Load(string id) {
        foreach (var state in Enum.GetValues<ChoreState>()) {
            var probe = _tree.Abs($"{Chore.DirectoryFor(state)}/{id}.md");
            if (File.Exists(probe)) return ParseChoreFile(probe);
        }
        return null;
    }

    private void WriteFile(Chore c) {
        var path = _tree.Abs(c.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var meta = new Dictionary<string, object?> {
            ["id"] = c.Id,
            ["title"] = c.Title,
            ["state"] = c.State.ToString().ToLowerInvariant(),
            ["interval_hours"] = c.Interval.TotalHours,
            ["created"] = c.Created.ToString("o"),
            ["last_run"] = c.LastRun?.ToString("o"),
        };
        if (c.Closed is not null) meta["closed"] = c.Closed.Value.ToString("o");
        if (c.ClosedWhy is not null) meta["closed_why"] = c.ClosedWhy;

        File.WriteAllText(path, Frontmatter.Write(meta, c.Description ?? ""));
    }

    public static Chore ParseChoreFile(string absPath) {
        var (meta, body) = Frontmatter.Read(File.ReadAllText(absPath));
        var dir = Path.GetFileName(Path.GetDirectoryName(absPath)) ?? "active";
        return new Chore {
            Id = meta.GetValueOrDefault("id") ?? Path.GetFileNameWithoutExtension(absPath),
            Title = meta.GetValueOrDefault("title") ?? "(untitled)",
            // Directory wins over frontmatter: the filesystem is ground truth.
            State = IndexRebuilder.ParseEnum(dir, ChoreState.Active),
            Interval = TimeSpan.FromHours(IndexRebuilder.ParseDouble(meta.GetValueOrDefault("interval_hours"), 24)),
            Created = IndexRebuilder.ParseTs(meta.GetValueOrDefault("created")),
            Description = body,
            LastRun = meta.TryGetValue("last_run", out var lr) && DateTimeOffset.TryParse(lr, out var d1) ? d1 : null,
            Closed = meta.TryGetValue("closed", out var cl) && DateTimeOffset.TryParse(cl, out var d2) ? d2 : null,
            ClosedWhy = meta.GetValueOrDefault("closed_why"),
        };
    }

    private int NextOrdinal() {
        var max = 0;
        foreach (var f in _tree.ChoreFiles()) {
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
