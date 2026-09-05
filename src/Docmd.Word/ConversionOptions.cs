namespace Docmd.Word;

using Ooxml.Md.Core.Markdown;

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
}
