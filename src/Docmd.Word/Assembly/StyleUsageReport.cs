namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;
using Ooxml.Md.Core.StyleMapping;

/// <summary>How often the document used a style the map says nothing about.</summary>
/// <param name="Style">The <c>w:styleId</c>, with its display name when the document declares one.</param>
/// <param name="Count">Paragraph and run references to it.</param>
public sealed record UnmappedStyle(string Style, int Count);

/// <summary>How a style map fit a document, in both directions.</summary>
/// <param name="EntriesThatMatchedNothing">Map keys no style in this document carries.</param>
/// <param name="MostUsedUnmappedStyles">
/// Styles the map does not cover <em>and</em> docmd did nothing with, busiest first -- the
/// candidates worth adding. Busiest first because a style used once is rarely worth an entry.
/// </param>
public sealed record StyleUsage(
    IReadOnlyList<string> EntriesThatMatchedNothing,
    IReadOnlyList<UnmappedStyle> MostUsedUnmappedStyles);

/// <summary>
/// Compares the styles a document actually uses against the style map it was given, in both
/// directions.
/// </summary>
/// <remarks>
/// <para>
/// A style map fails silently by nature. A misspelled <c>w:styleId</c> simply never matches, and
/// the output is identical to not passing a map at all — so the one thing a person cannot tell
/// from the result is whether their configuration did anything.
/// </para>
/// <para>
/// Reporting both directions is what turns the map from guesswork into a conversation with the
/// document: entries that matched nothing are probably typos or a map written for a different
/// template, and styles the document leans on that the map does not cover are the candidates
/// worth adding.
/// </para>
/// </remarks>
public static class StyleUsageReport
{
    /// <summary>
    /// Word's own plumbing: styles the format itself creates, which carry no house meaning and
    /// are never worth suggesting. Paragraph styles docmd already acts on are excluded a
    /// different way -- by observing what it did, not by naming them; see <see cref="Build"/>.
    /// </summary>
    private static readonly HashSet<string> Plumbing = new(StringComparer.OrdinalIgnoreCase)
    {
        "Normal", "DefaultParagraphFont", "TableNormal", "NoList", "ListParagraph",
        "Hyperlink", "FollowedHyperlink", "CommentReference", "CommentText", "CommentSubject",
        "FootnoteReference", "FootnoteText", "EndnoteReference", "EndnoteText",
        "PageNumber", "LineNumber", "Header", "Footer", "BalloonText",
    };

    /// <summary>Builds the report from a composite and the map it was converted with.</summary>
    /// <param name="composite">The composite document, after assembly.</param>
    /// <param name="styleMap">The map the conversion used.</param>
    /// <param name="topUnmapped">How many unmapped styles to name.</param>
    public static StyleUsage Build(XDocument composite, StyleMap styleMap, int topUnmapped = 5)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(styleMap);

        var body = composite.Root?.Element(WordNames.Docmd + "body");
        var styles = composite.Root?.Element(WordNames.Docmd + "styles");

        // styleId -> display name, so a map written against either form is recognised.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var style in styles?.Descendants(WordNames.W + "style") ?? [])
        {
            var id = (string?)style.Attribute(WordNames.W + "styleId");
            var name = (string?)style.Element(WordNames.W + "name")?.Attribute(WordNames.W + "val");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            {
                names[id] = name;
            }
        }

        // Every style reference, for deciding whether a map entry matched anything.
        var used = new Dictionary<string, int>(StringComparer.Ordinal);

        // Only the references docmd did nothing special with, for deciding what to suggest.
        // Suggesting a style docmd already handles is worse than saying nothing: it reads as
        // "this was missed", and acting on it adds an entry that changes no output.
        var unhandled = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var paragraph in body?.Descendants(WordNames.W + "p") ?? [])
        {
            var id = (string?)paragraph
                .Element(WordNames.W + "pPr")?.Element(WordNames.W + "pStyle")
                ?.Attribute(WordNames.W + "val");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            used[id] = used.GetValueOrDefault(id) + 1;

            // HeadingAnnotator stamps every paragraph, so "None" is a real answer rather than
            // an absent one -- it means every route to a heading was tried and none matched.
            var handled =
                (string?)paragraph.Attribute(WordNames.Docmd + "heading-source") is not (null or "None")
                || paragraph.Element(WordNames.W + "pPr")?.Element(WordNames.W + "numPr") is not null;

            if (!handled)
            {
                unhandled[id] = unhandled.GetValueOrDefault(id) + 1;
            }
        }

        foreach (var run in body?.Descendants(WordNames.W + "rStyle") ?? [])
        {
            var id = (string?)run.Attribute(WordNames.W + "val");
            if (!string.IsNullOrEmpty(id))
            {
                used[id] = used.GetValueOrDefault(id) + 1;
                unhandled[id] = unhandled.GetValueOrDefault(id) + 1;
            }
        }

        var mapped = new HashSet<string>(styleMap.Rules.Select(r => r.Key), StringComparer.Ordinal);

        var unmatched = styleMap.Rules
            .Where(rule => !used.ContainsKey(rule.Key)
                           && !used.Keys.Any(id => names.TryGetValue(id, out var n)
                                                   && string.Equals(n, rule.Key, StringComparison.Ordinal)))
            .Select(rule => rule.Key)
            .ToArray();

        var unmapped = unhandled
            .Where(pair => !mapped.Contains(pair.Key)
                           && !(names.TryGetValue(pair.Key, out var n) && mapped.Contains(n))
                           && !Plumbing.Contains(pair.Key))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(topUnmapped)
            .Select(pair => new UnmappedStyle(
                names.TryGetValue(pair.Key, out var display) && !string.Equals(display, pair.Key, StringComparison.Ordinal)
                    ? string.Create(CultureInfo.InvariantCulture, $"{pair.Key} ({display})")
                    : pair.Key,
                pair.Value))
            .ToArray();

        return new StyleUsage(unmatched, unmapped);
    }
}
