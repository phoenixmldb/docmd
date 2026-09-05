namespace Docmd.Word;

using Ooxml.Md.Core.Assets;
using Docmd.Word.Assembly;
using Ooxml.Md.Core.Frontmatter;

/// <param name="Markdown">The converted document, frontmatter included when requested.</param>
/// <param name="Properties">Metadata read from the document, including the source hash.</param>
/// <param name="AssetIssues">
/// Non-fatal problems worth telling the user about. Plan 3's audit aggregates these.
/// </param>
/// <param name="StyleUsage">
/// How the style map fit this document, in both directions. A map fails silently by nature —
/// a misspelled styleId simply never matches — so the one thing a person cannot tell from the
/// output is whether their configuration did anything.
/// </param>
public sealed record ConversionResult(
    string Markdown,
    DocumentProperties Properties,
    IReadOnlyList<AssetIssue> AssetIssues,
    StyleUsage StyleUsage);
