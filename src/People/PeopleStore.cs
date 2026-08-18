using CozyHarness.Domain;
using CozyHarness.Storage;

namespace CozyHarness.People;

/// <summary>
/// Privacy follows provenance, not content. Recording *where* a fact came from is
/// mechanical; judging whether it is sensitive is not. So the model is asked only
/// for the former, and the disclosure rule derives from it at read time.
/// </summary>
public sealed class PeopleStore {
    private readonly AgentTree _tree;
    private readonly IndexDb _db;

    public PeopleStore(AgentTree tree, IndexDb db) { _tree = tree; _db = db; }

    public Person EnsurePerson(string slug, string displayName, bool isOperator = false) {
        var p = new Person {
            Slug = slug,
            DisplayName = displayName,
            IsOperator = isOperator,
            FirstMet = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(_tree.Abs(p.Directory));
        Directory.CreateDirectory(_tree.Abs($"{p.Directory}/log"));

        foreach (var prov in Enum.GetValues<Provenance>()) {
            var path = _tree.Abs(p.PathFor(prov));
            if (!File.Exists(path))
                File.WriteAllText(path, Header(p, prov));
        }
        return p;
    }

    public void RecordFact(PersonFact fact) {
        var person = new Person { Slug = fact.PersonSlug, DisplayName = fact.PersonSlug };
        var rel = person.PathFor(fact.Source);
        var path = _tree.Abs(rel);
        if (!File.Exists(path)) EnsurePerson(fact.PersonSlug, fact.PersonSlug);

        var tags = new List<string> { $"from:{fact.Source.ToString().ToLowerInvariant()}" };
        if (fact.DerivedFromPrivate) tags.Add("derived-from-private");
        if (fact.Sensitive) tags.Add("sensitive");

        var line = $"\n- [{fact.Recorded:yyyy-MM-dd}] ({string.Join(", ", tags)}) {fact.Content.Trim()}\n";
        File.AppendAllText(path, line);
        _db.AddFact(fact, rel);
    }

    public void AppendInteractionLog(string slug, DateTimeOffset when, string content, bool sensitive) {
        var path = _tree.Abs($"people/{slug}/log/{when:yyyy-MM-dd}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var marker = sensitive ? " **[sensitive]**" : "";
        File.AppendAllText(path, $"\n### {when:HH:mm}{marker}\n\n{content.Trim()}\n");
    }

    /// <summary>The name currently on file for this slug, or fallback if they've never been seen before. Read fresh every call — cheap, and the file is the only source of truth.</summary>
    public string CurrentName(string slug, string fallback) {
        var path = _tree.Abs($"people/{slug}/name.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback;
    }

    /// <summary>
    /// What Discord itself currently calls them — passive, best-effort
    /// tracking, not something they asked for. Never overwrites a name set
    /// via SetPreferredName: if they told the agent what to call them, their
    /// own Discord display name changing later shouldn't silently undo that.
    /// A no-op write-wise when the name hasn't actually changed, so an
    /// unchanged display name doesn't produce a git diff on every message.
    /// </summary>
    public void SyncDiscordName(string slug, string discordDisplayName) {
        if (string.IsNullOrWhiteSpace(discordDisplayName)) return;
        if (File.Exists(_tree.Abs($"people/{slug}/preferred-name"))) return;

        // Deliberately not CurrentName(slug, discordDisplayName) == discordDisplayName:
        // that fallback IS discordDisplayName, so on a brand-new contact (no
        // file yet) the comparison is trivially true against itself and the
        // name never actually gets written. Read the raw stored value —
        // null, not a fallback — so "nothing on file yet" is correctly
        // treated as different from any real name.
        var path = _tree.Abs($"people/{slug}/name.txt");
        var current = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        if (current == discordDisplayName) return;
        WriteName(slug, discordDisplayName);
    }

    /// <summary>
    /// A name they explicitly asked to be called — from ReplyTick noticing
    /// it in conversation. Marks it as preferred so SyncDiscordName won't
    /// later overwrite it just because their Discord display name differs.
    /// </summary>
    public void SetPreferredName(string slug, string name) {
        if (string.IsNullOrWhiteSpace(name)) return;
        WriteName(slug, name);
        File.WriteAllText(_tree.Abs($"people/{slug}/preferred-name"), "");
    }

    private void WriteName(string slug, string name) {
        var path = _tree.Abs($"people/{slug}/name.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name.Trim());
    }

    /// <summary>
    /// Gate for anything leaving toward a third party. Storing is cheap; publishing
    /// cannot be undone. Returns the facts that must NOT travel.
    /// </summary>
    public IReadOnlyList<PersonFact> Blockers(IEnumerable<PersonFact> referenced, string recipientSlug) =>
        referenced.Where(f => !f.MayTravelTo(recipientSlug)).ToList();

    private static string Header(Person p, Provenance prov) => prov switch {
        Provenance.Public =>
            $"# {p.DisplayName} — public\n\nThings they have said openly, or that anyone could find.\nThis travels freely.\n",
        Provenance.Learned =>
            $"# {p.DisplayName} — learned\n\nThings they told me directly.\n**This does not travel without their consent.**\n",
        Provenance.SelfObserved =>
            $"# {p.DisplayName} — observed\n\nMy own impressions and inferences.\nMine to share — except where an inference rests on something in learned.md,\nin which case it inherits that restriction.\n",
        _ => "",
    };
}
