namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Ooxml.Md.Core.Markdown;

/// <summary>A word the document showed that the Markdown does not.</summary>
/// <param name="Word">The word that could not be found in the output.</param>
/// <param name="Context">The words around it in the source, for locating it by eye.</param>
public sealed record LostWord(string Word, string Context);

/// <summary>A construct that explains missing text, and how much of it this document has.</summary>
/// <param name="Construct">The element name, as it appears in the document.</param>
/// <param name="Count">How many of the lost words sit inside one.</param>
public sealed record LossCause(string Construct, int Count);

/// <summary>How much of a document's text survived conversion.</summary>
/// <param name="SourceWords">Words a reader can see in the source document.</param>
/// <param name="LostWords">Those the Markdown does not contain, in document order.</param>
/// <param name="Causes">
/// The structures the missing words are sitting in, read from the document rather than from a
/// fixed list, so a wrapper nobody anticipated names itself the first time it costs a word.
/// </param>
public sealed record TextCoverage(
    int SourceWords,
    IReadOnlyList<LostWord> LostWords,
    IReadOnlyList<LossCause> Causes)
{
    /// <summary>True when every word a reader can see survived into the Markdown.</summary>
    public bool IsComplete => LostWords.Count == 0;

    /// <summary>Share of the document's words that survived, 0 to 1. An empty document is 1.</summary>
    public double Fraction =>
        SourceWords == 0 ? 1.0 : (SourceWords - LostWords.Count) / (double)SourceWords;
}

/// <summary>
/// Checks that every word a reader can see in the source still appears, in order, in the
/// Markdown, and reports what is missing.
/// </summary>
/// <remarks>
/// <para>
/// This began as a test-only oracle and was promoted into the product because losing a word is
/// not a cosmetic defect for the people using this. A converted document goes into a retrieval
/// index or a contract review; a sentence that quietly lost its subject is worse than a document
/// that failed loudly, because nothing downstream can tell. docmd therefore never fails a
/// document for imperfect conversion — it converts, and it says what it could not carry across.
/// </para>
/// <para>
/// Measured on a 49-document corpus, 38 documents lose nothing at all and total loss is about 3%
/// of all text, of which one pathological document holds 71%: that file keeps 92% of its content
/// inside text boxes. That is the shape this reporting exists for. Nearly every document is
/// intact and needs no warning; the rare one that is badly wrong is badly wrong in a way docmd
/// can see and describe.
/// </para>
/// </remarks>
public static class TextCoverageReport
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Word = W;

    /// <summary>
    /// The legacy half of an <c>mc:AlternateContent</c>, which repeats verbatim what the
    /// <c>mc:Choice</c> beside it already says.
    /// </summary>
    /// <remarks>
    /// A reader sees one of the two, never both: Word renders the choice it understands and
    /// ignores the fallback. Counting both made this check demand two copies of every word in
    /// a document that used them, and report the second copy as lost. One real deck carried 118
    /// of these and was scored as losing 120 words it had not lost.
    /// </remarks>
    private static readonly XNamespace Compatibility =
        "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>
    /// Elements that are ordinary document flow. Anything else wrapping a lost word is worth
    /// naming, because it is the structure that explains the loss.
    /// </summary>
    private static readonly HashSet<XName> PlainFlow =
    [
        Word + "body", Word + "p", Word + "r", Word + "tbl", Word + "tr", Word + "tc",
        Word + "hyperlink", Word + "ins", Word + "sdtContent",
    ];

    /// <summary>Compares a composite against the Markdown produced from it.</summary>
    /// <param name="composite">The composite document, after assembly.</param>
    /// <param name="markdown">The Markdown docmd produced, frontmatter included or not.</param>
    public static TextCoverage Measure(XDocument composite, string markdown)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(markdown);

        var source = SourceWords(composite);
        var output = MarkdownTextReader.Words(markdown);
        var lost = Compare(source, output);

        return new TextCoverage(source.Count, lost, CausesIn(composite, lost));
    }

    /// <summary>The words a reader sees in the source document, in document order.</summary>
    public static IReadOnlyList<string> SourceWords(XDocument composite)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var body = composite.Root?.Element(WordNames.Docmd + "body");
        if (body is null)
        {
            return [];
        }

        // One pass over the body in document order, visiting every w:t exactly once.
        //
        // The obvious loop -- for each w:p, walk its descendants -- double counts. A paragraph
        // inside a text box or a block content control is a descendant of an outer paragraph,
        // so its text is collected once for the outer w:p and again for the inner one. Every
        // duplicated word then fails to match a second time in the output, the sequence order
        // breaks, and the comparison reports almost the whole document as lost. It claimed
        // 3,307 words missing from a document that had lost 8.
        //
        // Text is concatenated with no separator between runs, because that is what the format
        // means: w:t values are contiguous literal text, and Word splits them mid-word at rsid
        // and proofing boundaries constantly. A separator would demand "conver" and "sion" as
        // separate words from a document whose Markdown correctly says "conversion". Paragraph
        // boundaries do separate, since a paragraph's last word and the next one's first are
        // not one word.
        var text = new StringBuilder();
        foreach (var node in body.Descendants())
        {
            // A tracked deletion is not shown to a reader, so the Markdown must not carry it
            // and this must not demand it.
            if (node.Name == Word + "p")
            {
                text.Append(' ');
            }
            else if (node.Name == Word + "t"
                     && !node.Ancestors(Word + "del").Any()
                     && !node.Ancestors(Compatibility + "Fallback").Any())
            {
                text.Append(node.Value);
            }
            else if (node.Name == Word + "tab" || node.Name == Word + "br")
            {
                text.Append(' ');
            }
        }

        return MarkdownTextReader.Tokenize(text.ToString());
    }

    /// <summary>
    /// Source words the output does not contain, by counting occurrences rather than by
    /// aligning sequences.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A word appearing five times in the document and four times in the Markdown lost one
    /// occurrence. That is arithmetic, and it cannot cascade. Sequence alignment can: matching
    /// each source word against the next window of output greedily let the first "Statement" on
    /// a title page pair with a different "Statement" further down, which advanced the cursor
    /// past a block of text and reported every word behind it as missing. On one real document
    /// that turned a genuine loss of 8 words into a claim of 3,307.
    /// </para>
    /// <para>
    /// The trade is that counting is blind to reordering: text moved rather than dropped is not
    /// reported. docmd emits in document order by construction, so that case does not arise from
    /// this converter, and the order-sensitive check is kept where it belongs, in the tests. For
    /// a warning a user will act on, never overstating a loss matters far more than detecting a
    /// rearrangement the pipeline cannot produce.
    /// </para>
    /// </remarks>
    private static List<LostWord> Compare(IReadOnlyList<string> source, IReadOnlyList<string> output)
    {
        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in output)
        {
            available[word] = available.GetValueOrDefault(word) + 1;
        }

        var lost = new List<LostWord>();
        for (var i = 0; i < source.Count; i++)
        {
            if (available.TryGetValue(source[i], out var remaining) && remaining > 0)
            {
                available[source[i]] = remaining - 1;
                continue;
            }

            lost.Add(new LostWord(source[i], Context(source, i)));
        }

        return lost;
    }

    private static string Context(IReadOnlyList<string> words, int index)
    {
        var from = Math.Max(0, index - 4);
        var to = Math.Min(words.Count, index + 5);
        return string.Join(' ', words.Skip(from).Take(to - from));
    }

    /// <summary>
    /// Names the structures the lost words are actually sitting in.
    /// </summary>
    /// <remarks>
    /// Derived from where the missing words are, not from a list of constructs docmd is known to
    /// skip. That list went stale the moment the transform learned to read text boxes: it kept
    /// reporting "this document contains 118 text boxes, which docmd does not read" about
    /// documents whose text boxes had been read correctly. A warning that says something false
    /// is worse than no warning, and a hardcoded list of what we cannot handle is guaranteed to
    /// be wrong eventually, in the direction that embarrasses us.
    ///
    /// Reading it from the document instead means a wrapper nobody has heard of names itself the
    /// first time it costs someone a word.
    /// </remarks>
    private static IReadOnlyList<LossCause> CausesIn(XDocument composite, List<LostWord> lost)
    {
        var body = composite.Root?.Element(WordNames.Docmd + "body");
        if (body is null || lost.Count == 0)
        {
            return [];
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in lost.Take(50))
        {
            var carrier = body.Descendants(Word + "t")
                .FirstOrDefault(t => t.Value.Contains(word.Word, StringComparison.Ordinal));
            if (carrier is null)
            {
                continue;
            }

            foreach (var name in carrier.Ancestors()
                         // WordprocessingML only: the composite's own docmd: wrappers are
                         // ancestors of every node in it, so without this every loss would
                         // report "package" and "body" as though they explained something.
                         .Where(a => a.Name.Namespace == Word && !PlainFlow.Contains(a.Name))
                         .Select(a => a.Name.LocalName)
                         .Distinct(StringComparer.Ordinal))
            {
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }

        return [.. counts.OrderByDescending(c => c.Value)
                         .ThenBy(c => c.Key, StringComparer.Ordinal)
                         .Select(c => new LossCause(c.Key, c.Value))];
    }

    /// <summary>
    /// One line per problem, ready for stderr. Empty when the document converted intact, so a
    /// caller can print the result unconditionally and stay silent on the common case.
    /// </summary>
    /// <param name="coverage">The measurement to describe.</param>
    public static IReadOnlyList<string> Describe(TextCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        if (coverage.IsComplete)
        {
            return [];
        }

        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"! {coverage.LostWords.Count} of {coverage.SourceWords} words did not survive conversion "
                + $"({coverage.Fraction:P1} kept)."),
        };

        lines.AddRange(coverage.LostWords.Take(3).Select(
            word => $"!   missing '{word.Word}' near \"{word.Context}\""));

        if (coverage.LostWords.Count > 3)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"!   ...and {coverage.LostWords.Count - 3} more."));
        }

        foreach (var cause in coverage.Causes)
        {
            lines.Add(cause.Count == 1
                ? $"!   one of them sits inside <{cause.Construct}>."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"!   {cause.Count} of them sit inside <{cause.Construct}>."));
        }

        return lines;
    }
}
