namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// The safe half of a coverage report: what was lost and which construct explains it, with
/// nothing of the document in it.
/// </summary>
/// <param name="SourceWords">Words a reader can see in the source.</param>
/// <param name="LostWords">How many did not survive. A count, never the words.</param>
/// <param name="Causes">Element names, from published schemas only. See <see cref="CoverageDigest"/>.</param>
public sealed record CoverageFacts(int SourceWords, int LostWords, IReadOnlyList<LossCause> Causes)
{
    /// <summary>Everything the digest is allowed to know about one document.</summary>
    /// <remarks>
    /// Deliberately not constructed from a <see cref="TextCoverage"/> implicitly. The caller
    /// converts, and in doing so drops <see cref="LostWord.Word"/> and
    /// <see cref="LostWord.Context"/> — the only two fields that carry the user's text.
    /// </remarks>
    public static CoverageFacts From(TextCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        return new CoverageFacts(coverage.SourceWords, coverage.LostWords.Count, coverage.Causes);
    }
}

/// <summary>
/// Renders a coverage report that can be pasted into a public issue.
/// </summary>
/// <remarks>
/// <para>
/// docmd measures every conversion, which makes it the only instrument we have for documents we
/// are never allowed to see. None of that reached us, because the warning printed on stderr
/// welds the diagnostic to the content:
/// </para>
/// <code>
/// ! 2 of 3754 words did not survive conversion (99.9 % kept).
/// !   missing 'Here' near "the Discount List Click Here Click 'Add' to use"   &lt;- their document
/// !   one of them sits inside &lt;drawing&gt;.                                     &lt;- the actionable part
/// </code>
/// <para>
/// Anyone willing to help had to hand-redact the middle line or stay quiet, and documents that
/// convert badly are disproportionately contracts and internal procedures, so they stayed quiet.
/// The obstacle was never that their data is sensitive; it is that we never gave them a way to
/// report without it.
/// </para>
/// <para>
/// <strong>Redaction by construction.</strong> This type takes <see cref="CoverageFacts"/>, which
/// has no field capable of holding document text. It is not that the renderer removes the text —
/// it is never given any. "We strip the sensitive parts" is a claim that decays with every future
/// edit; "the sensitive parts are not in scope" is a property of the code's shape, and one a test
/// can check.
/// </para>
/// </remarks>
public static class CoverageDigest
{
    /// <summary>
    /// Namespaces whose element names come from a published schema and therefore cannot carry
    /// anything a person wrote.
    /// </summary>
    /// <remarks>
    /// This allowlist is load-bearing for privacy, and it is worth saying so plainly where
    /// someone might otherwise widen it as an improvement. A custom XML part can carry elements
    /// named by whoever authored the template — <c>AcmeCorpContractValue</c> is an ordinary thing
    /// to find in a real document — and the namespace URI is no safer, being usually a company
    /// domain. Anything outside this list reports as <c>foreign</c>: enough to say an
    /// unrecognised wrapper cost someone a word, without printing what it was called.
    /// </remarks>
    private static readonly HashSet<string> PublishedVocabularies =
    [
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
        "http://schemas.openxmlformats.org/markup-compatibility/2006",
        "http://schemas.openxmlformats.org/drawingml/2006/main",
        "http://schemas.openxmlformats.org/drawingml/2006/picture",
        "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
        "http://schemas.openxmlformats.org/officeDocument/2006/math",
        "urn:schemas-microsoft-com:vml",
        "urn:schemas-microsoft-com:office:office",
        "http://schemas.microsoft.com/office/word/2010/wordml",
        "http://schemas.microsoft.com/office/word/2012/wordml",
    ];

    /// <summary>True when an element name may be printed as itself.</summary>
    public static bool IsPublishedVocabulary(string? namespaceName) =>
        !string.IsNullOrEmpty(namespaceName) && PublishedVocabularies.Contains(namespaceName);

    /// <summary>The name to print for an element in the given namespace.</summary>
    public static string NameFor(string? namespaceName, string localName) =>
        IsPublishedVocabulary(namespaceName) ? localName : "foreign";

    /// <summary>
    /// Renders the digest for a run over one or more documents.
    /// </summary>
    /// <param name="documents">
    /// One entry per document converted, in the order they were converted. Documents are
    /// identified by ordinal only: a filename typically carries a client name, a project and a
    /// revision, and this repository already forbids naming real documents elsewhere.
    /// </param>
    /// <param name="engineVersion">The XSLT engine's version, or null to omit the line.</param>
    public static string Render(IReadOnlyList<CoverageFacts> documents, string? engineVersion = null)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var docmdVersion = typeof(CoverageDigest).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CoverageDigest).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        // The '+' suffix is the source-revision metadata the SDK appends; it is ours, but it is
        // noise in a report meant to be read.
        var plus = docmdVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            docmdVersion = docmdVersion[..plus];
        }

        var sb = new StringBuilder();
        sb.AppendLine("docmd coverage report");
        sb.Append("  docmd ").Append(docmdVersion);
        if (!string.IsNullOrEmpty(engineVersion))
        {
            sb.Append(" · PhoenixmlDb.Xslt ").Append(engineVersion);
        }

        sb.Append(" · ").Append(RuntimeInformation.FrameworkDescription)
          .Append(" · ").Append(RuntimeInformation.RuntimeIdentifier)
          .AppendLine().AppendLine();

        var totalWords = documents.Sum(d => (long)d.SourceWords);
        var totalLost = documents.Sum(d => (long)d.LostWords);
        var intact = documents.Count(d => d.LostWords == 0);

        sb.Append(Inv($"  {documents.Count} document(s), {totalWords:N0} words")).AppendLine();
        sb.Append(Inv($"  {intact} intact · {documents.Count - intact} with losses · "))
          .Append(Inv($"{totalLost:N0} lost"));
        if (totalWords > 0)
        {
            sb.Append(Inv($" ({totalLost / (double)totalWords:P3})"));
        }

        sb.AppendLine();

        // Causes aggregated across the run, because one line covering 500 documents is far more
        // useful to whoever reads it than 500 blocks.
        var byConstruct = documents
            .SelectMany(d => d.Causes)
            .GroupBy(c => c.Construct, StringComparer.Ordinal)
            .Select(g => (Construct: g.Key, Count: g.Sum(c => c.Count)))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Construct, StringComparer.Ordinal)
            .ToList();

        if (byConstruct.Count > 0)
        {
            sb.AppendLine().AppendLine("  causes, by occurrences");
            foreach (var (construct, count) in byConstruct)
            {
                sb.Append(Inv($"    {construct,-18}{count,6}")).AppendLine();
            }
        }

        var lossy = documents
            .Select((d, i) => (Index: i + 1, Facts: d))
            .Where(x => x.Facts.LostWords > 0)
            .ToList();

        if (lossy.Count > 0)
        {
            sb.AppendLine().AppendLine("  losses by document");
            foreach (var (index, facts) in lossy)
            {
                var names = facts.Causes.Count == 0
                    ? "unattributed"
                    : string.Join(", ", facts.Causes.Select(c => c.Construct));
                sb.Append(Inv($"    #{index,-4}{facts.SourceWords,9:N0} words{facts.LostWords,7:N0} lost   {names}"))
                  .AppendLine();
            }
        }

        sb.AppendLine()
          .AppendLine("  No document text, filenames or properties appear above; documents are")
          .AppendLine("  numbered in conversion order. Safe to paste into a public issue:")
          .AppendLine("  https://github.com/phoenixmldb/docmd/issues");

        return sb.ToString();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
