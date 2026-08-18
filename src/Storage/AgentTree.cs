using CozyHarness.Domain;

namespace CozyHarness.Storage;

/// <summary> The filesystem hierarchy that the harness operates in. </summary>
public sealed class AgentTree {
    public string Root { get; }

    public AgentTree(string root) => Root = root;

    public string Abs(string relative) => Path.Combine(Root, relative);

    public static readonly string[] RequiredDirectories = {
        "self",
        "goals/proposed", "goals/active", "goals/dormant", "goals/abandoned", "goals/done",
        "chores/active", "chores/retired",
        "episodes",
        "beliefs",
        "people",
        "observations/dropbox",
        "inbox", "outbox",
    };

    public void EnsureLayout() {
        foreach (var d in RequiredDirectories)
            Directory.CreateDirectory(Abs(d));

        var gitignore = Abs(".gitignore");
        if (!File.Exists(gitignore))
            File.WriteAllText(gitignore,
                "index.sqlite\nindex.sqlite-*\n*.tmp\n" +
                // Symlink into the Nix store: the agent's own source, readable
                // whenever it wants. Not part of its history.
                "harness\n");

        var selfModel = Abs("self/model.md");
        if (!File.Exists(selfModel))
            File.WriteAllText(selfModel, Prompts.Seeds.InitialSelfModel);

        var situation = Abs("self/situation.md");
        if (!File.Exists(situation))
            File.WriteAllText(situation, Prompts.Seeds.Situation);
    }

    public void WriteEpisode(Episode e) {
        var path = Abs(e.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var meta = new Dictionary<string, object?> {
            ["ts"] = e.Timestamp.ToString("o"),
            ["type"] = e.Type.ToString().ToLowerInvariant(),
            ["summary"] = e.Summary,
            ["did_nothing"] = e.DidNothing,
            ["salience"] = Math.Round(e.Salience, 2),
            ["tokens_used"] = e.TokensUsed,
        };
        if (e.GoalId is not null) meta["goal"] = e.GoalId;
        if (e.Person is not null) meta["person"] = e.Person;
        if (e.Sensitive) meta["sensitive"] = true;

        // Collision guard: two ticks in the same minute must not clobber each other.
        var final = path;
        var n = 1;
        while (File.Exists(final))
            final = path[..^3] + $"-{n++}.md";

        File.WriteAllText(final, Frontmatter.Write(meta, e.Body ?? e.Summary));
    }

    public IEnumerable<string> EpisodeFiles() =>
        Directory.Exists(Abs("episodes"))
            ? Directory.EnumerateFiles(Abs("episodes"), "*.md", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();

    public IEnumerable<string> GoalFiles() =>
        Directory.Exists(Abs("goals"))
            ? Directory.EnumerateFiles(Abs("goals"), "*.md", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();

    public IEnumerable<string> ChoreFiles() =>
        Directory.Exists(Abs("chores"))
            ? Directory.EnumerateFiles(Abs("chores"), "*.md", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();

    public string ReadSelfModel() =>
        File.Exists(Abs("self/model.md")) ? File.ReadAllText(Abs("self/model.md")) : "";

    public string ReadSituation() =>
        File.Exists(Abs("self/situation.md")) ? File.ReadAllText(Abs("self/situation.md")) : "";
}
