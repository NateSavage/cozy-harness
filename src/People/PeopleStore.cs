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
