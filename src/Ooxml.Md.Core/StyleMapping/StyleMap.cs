namespace Ooxml.Md.Core.StyleMapping;

using System.Globalization;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

/// <summary>What a mapped style should become in the Markdown.</summary>
/// <param name="Key">The <c>w:styleId</c> or style name this entry matches, as written.</param>
/// <param name="As">The md-XML construct to emit — see <see cref="StyleMap.ParagraphKinds"/>.</param>
/// <param name="Level">Heading level, for <c>as: heading</c>. Ignored otherwise.</param>
/// <param name="Prefix">Literal text emitted before the content.</param>
public sealed record StyleRule(string Key, string As, int? Level, string? Prefix);

/// <summary>
/// A declarative map from a document's own style names to Markdown constructs.
/// </summary>
/// <remarks>
/// <para>
/// House styles carry meaning the format does not: <c>CautionNote</c> is a warning, and nothing
/// in OOXML says so. The map is how a customer tells docmd what their template means, without
/// having to write XSLT — the escape hatch for people who do is <c>--stylesheet</c>.
/// </para>
/// <para>
/// The map is emitted into the composite document rather than passed as an <c>xsl:param</c>.
/// The spec originally specified a parameter; the engine cannot carry a node in one (see
/// <c>docs/engine-defects/2026-09-04-xslt-node-valued-parameters.md</c>). Riding in the composite
/// is the better design regardless: the transform stays a pure function of one input, and the map
/// shows up in a dumped composite when something needs explaining.
/// </para>
/// </remarks>
public sealed class StyleMap
{
    /// <summary>Values <c>as:</c> may take on a paragraph style.</summary>
    public static readonly string[] ParagraphKinds =
        ["heading", "blockquote", "list-item", "ordered-list-item", "code-block", "para"];

    /// <summary>Values <c>as:</c> may take on a character style.</summary>
    public static readonly string[] CharacterKinds = ["inline-code", "strong", "em"];

    /// <summary>
    /// Kinds whose template emits <c>prefix:</c>. List items are grouped into a list before
    /// that template is reached, and character kinds are runs, so neither ever sees one.
    /// </summary>
    public static readonly string[] PrefixableKinds = ["heading", "blockquote", "code-block", "para"];

    private StyleMap(IReadOnlyList<StyleRule> rules) => Rules = rules;

    public IReadOnlyList<StyleRule> Rules { get; }

    /// <summary>An empty map — every style falls through to the built-in behaviour.</summary>
    public static StyleMap Empty { get; } = new([]);

    /// <summary>
    /// Parses a style map. Throws <see cref="StyleMapException"/> with a message naming the
    /// offending key for anything malformed — a map is hand-written configuration, and a silent
    /// no-op is indistinguishable from not passing one at all.
    /// </summary>
    public static StyleMap Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new StyleMapException($"The style map is not valid YAML: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0)
        {
            return Empty;
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new StyleMapException(
                "A style map is a mapping of style name to rule, e.g. \"CautionNote: { as: blockquote }\".");
        }

        var rules = new List<StyleRule>();
        foreach (var (keyNode, valueNode) in root.Children)
        {
            var key = ((YamlScalarNode)keyNode).Value ?? "";
            if (key.Length == 0)
            {
                throw new StyleMapException("A style map entry has an empty style name.");
            }

            if (valueNode is not YamlMappingNode rule)
            {
                throw new StyleMapException(
                    $"'{key}' must be a mapping, e.g. \"{key}: {{ as: blockquote }}\".");
            }

            rules.Add(ParseRule(key, rule));
        }

        return new StyleMap(rules);
    }

    private static StyleRule ParseRule(string key, YamlMappingNode rule)
    {
        string? kind = null, prefix = null;
        int? level = null;

        foreach (var (fieldNode, valueNode) in rule.Children)
        {
            var field = ((YamlScalarNode)fieldNode).Value ?? "";
            var value = (valueNode as YamlScalarNode)?.Value ?? "";

            switch (field)
            {
                case "as":
                    kind = value;
                    break;
                case "prefix":
                    prefix = value;
                    break;
                case "level":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        || parsed is < 1 or > 6)
                    {
                        throw new StyleMapException($"'{key}' has level '{value}'; it must be 1 to 6.");
                    }

                    level = parsed;
                    break;
                default:
                    throw new StyleMapException(
                        $"'{key}' has unknown field '{field}'. Valid fields are: as, level, prefix.");
            }
        }

        if (string.IsNullOrEmpty(kind))
        {
            throw new StyleMapException($"'{key}' has no 'as'. Valid values are: {AllKinds}.");
        }

        if (!ParagraphKinds.Contains(kind, StringComparer.Ordinal)
            && !CharacterKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new StyleMapException($"'{key}' has as '{kind}'. Valid values are: {AllKinds}.");
        }

        // Silently ignoring a level on a non-heading would leave someone convinced they had
        // configured something they had not.
        if (level is not null && !string.Equals(kind, "heading", StringComparison.Ordinal))
        {
            throw new StyleMapException($"'{key}' sets a level, which only applies to 'as: heading'.");
        }

        // Same reason as the level check above: a prefix on a list or character kind is dropped
        // on the floor, and an entry that does nothing is indistinguishable from one that was
        // never read at all.
        if (prefix is not null && !PrefixableKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new StyleMapException(
                $"'{key}' sets a prefix, which only applies to: {string.Join(", ", PrefixableKinds)}.");
        }

        return new StyleRule(key, kind, level ?? (kind == "heading" ? 1 : null), prefix);
    }

    private static string AllKinds =>
        string.Join(", ", ParagraphKinds.Concat(CharacterKinds));

    /// <summary>
    /// Renders the map as the <c>docmd:style-map</c> element the composite carries.
    /// </summary>
    /// <param name="docmd">The docmd namespace.</param>
    public XElement ToXml(XNamespace docmd)
    {
        ArgumentNullException.ThrowIfNull(docmd);

        return new XElement(
            docmd + "style-map",
            Rules.Select(r => new XElement(
                docmd + "style",
                new XAttribute("key", r.Key),
                new XAttribute("as", r.As),
                r.Level is null ? null : new XAttribute("level", r.Level.Value.ToString(CultureInfo.InvariantCulture)),
                r.Prefix is null ? null : new XAttribute("prefix", r.Prefix))));
    }
}

/// <summary>A style map could not be understood. Maps to CLI exit code 2 — bad input.</summary>
public sealed class StyleMapException : Exception
{
    public StyleMapException(string message) : base(message) { }
    public StyleMapException(string message, Exception innerException) : base(message, innerException) { }
    public StyleMapException() { }
}
