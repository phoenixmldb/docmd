namespace Ooxml.Md.Core.Markdown;

/// <summary>Which Markdown dialect to emit.</summary>
public enum MarkdownFlavour
{
    /// <summary>GitHub Flavored Markdown: pipe tables and strikethrough.</summary>
    Gfm,

    /// <summary>Strict CommonMark: no tables, which are emitted as HTML instead.</summary>
    CommonMark,
}

/// <summary>
/// Serialisation settings. Flavour lives here rather than in the stylesheet so a dialect
/// change never forks the transform.
/// </summary>
public sealed record MarkdownOptions
{
    public MarkdownFlavour Flavour { get; init; } = MarkdownFlavour.Gfm;

    public static MarkdownOptions Default { get; } = new();
}
