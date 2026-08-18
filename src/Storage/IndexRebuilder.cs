using CozyHarness.Chores;
using CozyHarness.Domain;
using CozyHarness.Goals;

namespace CozyHarness.Storage;

/// <summary>
/// Walks the tree and regenerates the index. The filesystem is ground truth;
/// this must be runnable at any time. Write it early — you will need it.
/// </summary>
public sealed class IndexRebuilder
{
    private readonly AgentTree _tree;
    private readonly IndexDb _db;

    public IndexRebuilder(AgentTree tree, IndexDb db) { _tree = tree; _db = db; }

    public RebuildReport Rebuild()
    {
        var report = new RebuildReport();
        _db.Clear("episodes");
        _db.Clear("goals");
        _db.Clear("chores");

        foreach (var file in _tree.EpisodeFiles())
        {
            try
            {
                var (meta, body) = Frontmatter.Read(File.ReadAllText(file));
                var rel = Path.GetRelativePath(_tree.Root, file);
                var ep = new Episode
                {
                    Timestamp = ParseTs(meta.GetValueOrDefault("ts")),
                    Type = ParseEnum(meta.GetValueOrDefault("type"), TickType.Work),
                    Summary = meta.GetValueOrDefault("summary") ?? FirstLine(body),
                    Body = body,
                    DidNothing = ParseBool(meta.GetValueOrDefault("did_nothing")),
                    GoalId = meta.GetValueOrDefault("goal"),
                    Person = meta.GetValueOrDefault("person"),
                    Sensitive = ParseBool(meta.GetValueOrDefault("sensitive")),
                    Salience = ParseDouble(meta.GetValueOrDefault("salience"), 0.5),
                };
                _db.UpsertEpisode(ep, rel, null);
                report.Episodes++;
            }
            catch (Exception ex) { report.Malformed.Add($"{file}: {ex.Message}"); }
        }

        foreach (var file in _tree.GoalFiles())
        {
            try
            {
                var g = GoalStore.ParseGoalFile(_tree.Root, file);
                _db.UpsertGoal(g);
                report.Goals++;
            }
            catch (Exception ex) { report.Malformed.Add($"{file}: {ex.Message}"); }
        }

        foreach (var file in _tree.ChoreFiles())
        {
            try
            {
                var c = ChoreStore.ParseChoreFile(file);
                _db.UpsertChore(c);
                report.Chores++;
            }
            catch (Exception ex) { report.Malformed.Add($"{file}: {ex.Message}"); }
        }

        return report;
    }

    private static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "(no summary)";

    internal static DateTimeOffset ParseTs(string? s) =>
        DateTimeOffset.TryParse(s, out var d) ? d : DateTimeOffset.UnixEpoch;

    internal static bool ParseBool(string? s) =>
        s is not null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

    internal static double ParseDouble(string? s, double fallback) =>
        double.TryParse(s, out var d) ? d : fallback;

    internal static T ParseEnum<T>(string? s, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : fallback;
}

public sealed class RebuildReport
{
    public int Episodes { get; set; }
    public int Goals { get; set; }
    public int Chores { get; set; }
    /// <summary>Files the model wrote badly. Reported, never fatal — the loop must survive bad frontmatter.</summary>
    public List<string> Malformed { get; } = new();
}
