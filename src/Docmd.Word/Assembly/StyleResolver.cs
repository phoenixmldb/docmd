namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;

/// <summary>
/// An outline level found on a style, and whether it came from an ancestor rather than
/// the style itself. The caller needs both: the audit reports "styled" and
/// "basedOn-derived" headings separately.
/// </summary>
public readonly record struct OutlineLookup(int Level, bool FromAncestor);

/// <summary>
/// Flattens WordprocessingML style inheritance so callers see effective values.
/// </summary>
/// <remarks>
/// Resolution happens once, here, because both emitters need the identical answer.
/// Computing it inside each stylesheet would let the Markdown and the review companion
/// disagree about heading structure, which is exactly what their cross-links depend on.
/// </remarks>
public sealed class StyleResolver
{
    /// <summary>Word's default body size when docDefaults says nothing: 11pt.</summary>
    private const int WordDefaultHalfPoints = 22;

    private readonly Dictionary<string, XElement> _styles;

    private StyleResolver(Dictionary<string, XElement> styles, int defaultFontHalfPoints)
    {
        _styles = styles;
        DefaultFontHalfPoints = defaultFontHalfPoints;
    }

    public int DefaultFontHalfPoints { get; }

    public static StyleResolver FromComposite(XElement stylesElement)
    {
        ArgumentNullException.ThrowIfNull(stylesElement);

        // The composite wraps w:styles in docmd:styles; accept either as the entry point.
        var root = stylesElement.Name == WordNames.W + "styles"
            ? stylesElement
            : stylesElement.Element(WordNames.W + "styles") ?? stylesElement;

        var styles = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var style in root.Elements(WordNames.W + "style"))
        {
            var id = (string?)style.Attribute(WordNames.W + "styleId");
            if (!string.IsNullOrEmpty(id))
            {
                styles[id] = style;
            }
        }

        var defaultSize = root.Element(WordNames.W + "docDefaults")
                              ?.Element(WordNames.W + "rPrDefault")
                              ?.Element(WordNames.W + "rPr")
                              ?.Element(WordNames.W + "sz");

        return new StyleResolver(styles, ParseInt(defaultSize) ?? WordDefaultHalfPoints);
    }

    public string? NameOf(string styleId)
        => _styles.TryGetValue(styleId, out var style)
            ? (string?)style.Element(WordNames.W + "name")?.Attribute(WordNames.W + "val")
            : null;

    /// <summary>
    /// The outline level this style confers, following w:basedOn until one is found.
    /// Null means the style is not a heading. Zero-based, as in the XML: w:outlineLvl 0
    /// is Heading 1.
    /// </summary>
    public int? EffectiveOutlineLevel(string styleId) => LookupOutlineLevel(styleId)?.Level;

    /// <summary>
    /// As <see cref="EffectiveOutlineLevel"/>, but also reports whether the level came
    /// from the style itself or from something it is based on.
    /// </summary>
    public OutlineLookup? LookupOutlineLevel(string styleId)
    {
        // Cycle guard. Malformed documents with A basedOn B basedOn A exist, and a naive
        // walk hangs the converter on a customer's corpus with no diagnostic at all.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = styleId;
        var hops = 0;

        while (current is not null && visited.Add(current))
        {
            if (!_styles.TryGetValue(current, out var style))
            {
                return null;
            }

            var declared = style.Element(WordNames.W + "pPr")?.Element(WordNames.W + "outlineLvl");
            if (ParseInt(declared) is not null)
            {
                // A style that declares a level has answered the question, one way or the
                // other. ECMA-376 §17.3.1.20 reserves 9 for body text, and stock Word
                // styles set it -- so "9" is a style saying it is NOT a heading, which is
                // a stronger statement than anything its w:basedOn ancestor says. The walk
                // stops here rather than inheriting a level this style overrode.
                return HeadingAnnotator.ParseOutlineLevel(declared) is int level
                    ? new OutlineLookup(level, FromAncestor: hops > 0)
                    : null;
            }

            current = (string?)style.Element(WordNames.W + "basedOn")?.Attribute(WordNames.W + "val");
            hops++;
        }

        return null;
    }

    private static int? ParseInt(XElement? element)
    {
        var raw = (string?)element?.Attribute(WordNames.W + "val");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
