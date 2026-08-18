using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CozyHarness.Storage;

/// <summary>
/// Markdown files with YAML frontmatter. Chosen because the agent can `cat` and `grep` these directly — native tools over bespoke APIs.
/// </summary>
public static class Frontmatter {
    private static readonly IDeserializer De = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Ser = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static string Write(IDictionary<string, object?> meta, string body) {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append(Ser.Serialize(meta));
        sb.Append("---\n\n");
        sb.Append(body.TrimEnd());
        sb.Append('\n');
        return sb.ToString();
    }

    public static (Dictionary<string, string> Meta, string Body) Read(string text) {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---"))
            return (meta, text);

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (meta, text);

        var yaml = text[3..end];
        var body = text[(end + 4)..].TrimStart('\n');

        try {
            var parsed = De.Deserialize<Dictionary<string, object?>>(yaml);
            if (parsed is not null)
                foreach (var (k, v) in parsed)
                    meta[k] = v?.ToString() ?? "";
        }
        catch (Exception) {
            // Frontmatter written by the model may be malformed. Body still readable;
            // the index rebuild will report the file rather than crash the loop.
        }

        return (meta, body);
    }
}
