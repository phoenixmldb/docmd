namespace Ooxml.Md.Core.Frontmatter;

/// <summary>
/// Metadata emitted as YAML frontmatter. Dates are strings already in ISO-8601 form: they
/// are passed through from the document rather than reformatted, so no CultureInfo can
/// influence the output.
/// </summary>
public sealed record DocumentProperties
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Created { get; init; }
    public string? Modified { get; init; }
    public string? Revision { get; init; }
    public string? Company { get; init; }

    /// <summary>The original filename, so a retrieved chunk names its source.</summary>
    public required string Source { get; init; }

    /// <summary>SHA-256 of the source file, so it names an exact version.</summary>
    public required string Sha256 { get; init; }
}
