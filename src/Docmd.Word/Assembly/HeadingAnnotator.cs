namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;
using Ooxml.Md.Core;

/// <summary>Where a paragraph's heading level came from. Reported by the corpus audit.</summary>
public enum HeadingSource
{
    None,
    OutlineLevel,
    BasedOn,
    StyleName,
    DirectFormat,
}

/// <summary>
/// Pipeline stage 3: stamps each body paragraph with its effective heading level, the rule
/// that determined it, and a stable slug.
/// </summary>
/// <remarks>
/// Heading structure decides RAG chunk boundaries, so this is the highest-leverage
/// correctness work in the product: a missed heading merges two chunks, a false positive
/// shatters a paragraph. Detection order is spec §4.4, most reliable rule first.
/// </remarks>
public static class HeadingAnnotator
{
    /// <summary>Longest text still plausible as a heading.</summary>
    private const int MaxDirectFormatHeadingLength = 120;

    /// <summary>How much larger than body text a direct-formatted line must be, in half-points.</summary>
    private const int DirectFormatSizeMargin = 4;

    /// <summary>
    /// Level assigned to every direct-format heading. Zero-based, so this is Markdown "##".
    /// Inferring depth from font size is guesswork that fails differently on each document;
    /// a flat level still produces correct chunk boundaries, which is the actual goal.
    /// </summary>
    private const int DirectFormatLevel = 1;

    public static void Annotate(XDocument composite)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var stylesElement = composite.Root?.Element(WordNames.Docmd + "styles");
        var resolver = StyleResolver.FromComposite(stylesElement ?? new XElement(WordNames.W + "styles"));
        var slugger = new Slugger();

        var body = composite.Root?.Element(WordNames.Docmd + "body");
        if (body is null)
        {
            return;
        }

        // Document order matters: slug deduplication and therefore anchor stability
        // depend on it.
        foreach (var paragraph in body.Descendants(WordNames.W + "p"))
        {
            // A paragraph inside a table cell is cell content, not document structure, and it
            // cannot become a heading whatever it looks like: the table template reads cell text
            // with string-join and never applies templates to these paragraphs. Detecting one
            // anyway was not harmless. It spent a slug, so a real heading further down took
            // "overview-1" while nothing in the document explained where "overview" had gone --
            // and Plan 2's review-companion cross-links are built on these slugs.
            var (level, source) = paragraph.Ancestors(WordNames.W + "tc").Any()
                ? (0, HeadingSource.None)
                : Detect(paragraph, resolver);

            paragraph.SetAttributeValue(WordNames.Docmd + "heading-source", source.ToString());

            if (source == HeadingSource.None)
            {
                continue;
            }

            paragraph.SetAttributeValue(
                WordNames.Docmd + "outline-level",
                level.ToString(CultureInfo.InvariantCulture));
            paragraph.SetAttributeValue(WordNames.Docmd + "slug", slugger.Slug(TextOf(paragraph)));
        }
    }

    private static (int Level, HeadingSource Source) Detect(XElement paragraph, StyleResolver resolver)
    {
        var properties = paragraph.Element(WordNames.W + "pPr");

        // Rule 1: an explicit outline level on the paragraph itself.
        var explicitLevel = ParseOutlineLevel(properties?.Element(WordNames.W + "outlineLvl"));
        if (explicitLevel is not null)
        {
            return (explicitLevel.Value, HeadingSource.OutlineLevel);
        }

        var styleId = (string?)properties?.Element(WordNames.W + "pStyle")?.Attribute(WordNames.W + "val");
        if (!string.IsNullOrEmpty(styleId))
        {
            // Rule 2: the style, or something it is based on, confers a level. The lookup
            // reports which, because the audit counts them separately -- a heading found
            // only through an ancestor is a weaker signal than one the style declares.
            var lookup = resolver.LookupOutlineLevel(styleId);
            if (lookup is not null)
            {
                return (
                    lookup.Value.Level,
                    lookup.Value.FromAncestor ? HeadingSource.BasedOn : HeadingSource.OutlineLevel);
            }

            // Rule 3: the localised style name. w:styleId is not w:name, and w:name is
            // what survives translation.
            var nameLevel = LevelFromStyleName(resolver.NameOf(styleId) ?? styleId);
            if (nameLevel is not null)
            {
                return (nameLevel.Value, HeadingSource.StyleName);
            }
        }

        // Rule 4: direct formatting, with no style involved at all.
        return LooksLikeADirectFormatHeading(paragraph, resolver)
            ? (DirectFormatLevel, HeadingSource.DirectFormat)
            : (0, HeadingSource.None);
    }

    private static int? LevelFromStyleName(string name)
    {
        // Matches "heading 1", "Heading1", "Heading 3". Localised names are handled by
        // rules 1 and 2; this is the English fallback of last resort.
        var trimmed = name.Replace(" ", "", StringComparison.Ordinal);
        if (!trimmed.StartsWith("heading", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var digits = trimmed["heading".Length..];
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var oneBased)
               && oneBased is >= 1 and <= 9
            ? oneBased - 1 // w:outlineLvl is zero-based; Heading 1 is level 0
            : null;
    }

    private static bool LooksLikeADirectFormatHeading(XElement paragraph, StyleResolver resolver)
    {
        var runs = paragraph.Elements(WordNames.W + "r").ToArray();
        if (runs.Length == 0)
        {
            return false;
        }

        var text = TextOf(paragraph).Trim();
        if (text.Length == 0 || text.Length > MaxDirectFormatHeadingLength)
        {
            return false;
        }

        // A trailing full stop is the strongest single signal that this is a sentence.
        if (text.EndsWith('.'))
        {
            return false;
        }

        var allBold = runs.All(r => IsToggleOn(r.Element(WordNames.W + "rPr")?.Element(WordNames.W + "b")));
        if (!allBold)
        {
            return false;
        }

        var largest = runs
            .Select(r => ParseInt(r.Element(WordNames.W + "rPr")?.Element(WordNames.W + "sz")))
            .Where(size => size is not null)
            .Select(size => size!.Value)
            .DefaultIfEmpty(resolver.DefaultFontHalfPoints)
            .Max();

        return largest >= resolver.DefaultFontHalfPoints + DirectFormatSizeMargin;
    }

    /// <summary>
    /// An outline level a paragraph or style actually declares as a heading level, or
    /// null.
    /// </summary>
    /// <remarks>
    /// ECMA-376 §17.3.1.20 gives w:outlineLvl the range 0..9 and reserves 9 for body
    /// text, which stock Word styles set explicitly. Reading it as a level made rule 1 --
    /// the rule the audit reports as most trustworthy -- manufacture high-confidence
    /// headings out of ordinary prose, and the serialiser's Math.Clamp hid the evidence
    /// by rendering them as "######" rather than as the nonsense they were.
    /// </remarks>
    internal static int? ParseOutlineLevel(XElement? element)
        => ParseInt(element) is int level and >= 0 and <= 8 ? level : null;

    /// <summary>
    /// Whether an ST_OnOff toggle (w:b, w:i, ...) is on. Absent w:val means on; the four
    /// off values are the schema's own.
    /// </summary>
    /// <remarks>
    /// The element's presence is not the answer. "&lt;w:b w:val=&quot;0&quot;/&gt;" is how
    /// Word turns bold OFF against a style that turns it on, so an existence test both
    /// bolds text that is not bold and, here, manufactures direct-format headings out of
    /// paragraphs that merely opted out of a bold style.
    /// </remarks>
    private static bool IsToggleOn(XElement? toggle)
        => toggle is not null
           && (string?)toggle.Attribute(WordNames.W + "val") is not ("0" or "false" or "off");

    private static string TextOf(XElement paragraph)
        => string.Concat(paragraph.Descendants(WordNames.W + "t").Select(t => t.Value));

    private static int? ParseInt(XElement? element)
    {
        var raw = (string?)element?.Attribute(WordNames.W + "val");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
