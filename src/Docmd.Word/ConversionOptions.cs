namespace Docmd.Word;

using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.StyleMapping;

/// <summary>Everything <see cref="DocumentConverter"/> needs to run one conversion.</summary>
public sealed record ConversionOptions
{
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Typed as <see cref="Uri"/>, not <see langword="string"/>: a malformed base URL must
    /// fail loudly at the argument boundary, not survive to
    /// <see cref="Ooxml.Md.Core.Assets.FileSystemAssetSink"/> deep in the pipeline and emit
    /// thousands of assets with broken links. The CLI is expected to parse raw input with
    /// <c>Uri.TryCreate(raw, UriKind.Absolute, out var baseUri)</c> before building options.
    /// </summary>
    public Uri? AssetBaseUrl { get; init; }

    public bool IncludeImages { get; init; } = true;
    public bool IncludeFrontmatter { get; init; } = true;
    public MarkdownFlavour Flavour { get; init; } = MarkdownFlavour.Gfm;

    /// <summary>
    /// Carried for a later task. <see cref="Ooxml.Md.Core.Assets.AssetRewriter"/> currently
    /// hardcodes the "img/" prefix and does not consult this value -- a known, accepted gap,
    /// not threaded through here.
    /// </summary>
    public string ImageDirectoryName { get; init; } = "img";

    /// <summary>
    /// A user stylesheet to run instead of the built-in one, or null for the built-in.
    /// </summary>
    /// <remarks>
    /// The transform is the product's semantic recovery layer, and it is a stylesheet rather
    /// than compiled code precisely so it can be replaced. <c>--print-stylesheet</c> emits the
    /// built-in one as a starting point, so overriding means editing what actually ran rather
    /// than reconstructing it from the repository.
    /// </remarks>
    public string? StylesheetPath { get; init; }

    /// <summary>House-style rules, or an empty map for built-in behaviour only.</summary>
    public StyleMap StyleMap { get; init; } = StyleMap.Empty;

    /// <summary>
    /// Where extracted assets are written, or null for
    /// <see cref="Ooxml.Md.Core.Assets.FileSystemAssetSink"/> writing beside the Markdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam that separates where bytes land from what the Markdown says. A sink receives
    /// each asset and returns the URI to reference it by, so an implementation can upload to
    /// object storage, deduplicate on content hash, or refuse an asset outright, and the
    /// Markdown follows whatever it returns.
    /// </para>
    /// <para>
    /// Exposed here rather than only existing as an interface: <c>IAssetSink</c> documents
    /// itself as the seam cloud sinks implement and ship as separate packages, and that was
    /// not true while <see cref="DocumentConverter"/> constructed the filesystem sink
    /// unconditionally. An extension point nothing can reach is a comment, not a seam.
    /// </para>
    /// <para>
    /// Ignored when <see cref="IncludeImages"/> is false, since nothing is extracted at all.
    /// </para>
    /// </remarks>
    public IAssetSink? AssetSink { get; init; }
}
