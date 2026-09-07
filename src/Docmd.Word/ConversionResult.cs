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
/// <param name="Coverage">
/// Whether every word a reader can see in the source survived into the Markdown, and what is
/// missing if not. Losing a word is not cosmetic for the people using this: a converted document
/// goes into a retrieval index or a contract review, and a sentence that quietly lost its subject
/// is worse than a document that failed loudly, because nothing downstream can tell.
/// </param>
public sealed record ConversionResult(
    string Markdown,
    DocumentProperties Properties,
    IReadOnlyList<AssetIssue> AssetIssues,
    StyleUsage StyleUsage,
    TextCoverage Coverage);
