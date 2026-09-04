namespace Docmd.Word;

using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Frontmatter;

/// <param name="Markdown">The converted document, frontmatter included when requested.</param>
/// <param name="Properties">Metadata read from the document, including the source hash.</param>
/// <param name="AssetIssues">
/// Non-fatal problems worth telling the user about. Plan 3's audit aggregates these.
/// </param>
public sealed record ConversionResult(
    string Markdown,
    DocumentProperties Properties,
    IReadOnlyList<AssetIssue> AssetIssues);
