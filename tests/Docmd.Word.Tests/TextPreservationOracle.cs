namespace Docmd.Word.Tests;

using System.Text;
using System.Xml.Linq;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

/// <summary>One word the source document showed that the Markdown does not.</summary>
/// <param name="Missing">The token that could not be matched.</param>
/// <param name="SourceContext">Words around it in the source, for locating it by eye.</param>
/// <param name="OutputPosition">How far into the output's tokens matching had got.</param>
public sealed record LostText(string Missing, string SourceContext, int OutputPosition);

/// <summary>
/// Checks the property that matters most and that no other test in this repository asserts:
/// every word a reader can see in the .docx still appears, in order, in the Markdown.
/// </summary>
/// <remarks>
/// <para>
/// The existing Markdig oracle verifies the <em>serialiser</em> — that md-XML becomes Markdown
/// meaning what it says. Nothing verified the other two thirds of the pipeline. Composition and
/// the transform could drop a construct entirely and every test would stay green, because the
/// suite asserts what fixtures contain rather than what documents contain.
/// </para>
/// <para>
/// The check is deliberately one-directional. The source's tokens must appear in the output, in
/// order; the output may hold more. That is not laziness, it is the correct relation: Markdown
/// legitimately adds list markers, image alt text and link destinations that the source has
/// nowhere. Requiring equality would fail on correct output, and a check that fails on correct
/// output gets deleted. Order is still enforced, so reordering is caught even though insertion
/// is allowed.
/// </para>
/// <para>
/// Output text is read back with Markdig rather than by string matching, so escaping cannot
/// produce a false pass: a literal <c>_</c> that we escaped to <c>\_</c> reads back as <c>_</c>,
/// while a <c>*</c> we failed to escape reads back as emphasis markup and its word goes missing.
/// </para>
/// </remarks>
public static class TextPreservationOracle
{
    /// <summary>
    /// How far ahead of the last match a source word may legitimately appear. Generous enough
    /// for the text Markdown genuinely adds, small enough that a mismatch cannot run away.
    /// </summary>
    private const int LookaheadTokens = 100;

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Word = W;

    /// <summary>
    /// Wrappers whose text docmd knowingly drops, per docs/limitations.md: the inline forms of
    /// w:sdt, w:fldSimple and w:smartTag wrap runs that the stylesheet's child-axis selection
    /// never reaches.
    /// </summary>
    /// <remarks>
    /// Exempting them is what makes this oracle usable today rather than a permanently red test,
    /// and it also pins the limitation: when the wrappers are fixed, these exemptions stop being
    /// load-bearing, and a test asserting one document's text is fully preserved will say so.
    /// Nothing else may be added here without a corresponding entry in limitations.md.
    /// </remarks>
    private static readonly XName[] DroppedWrappers =
    [
        Word + "sdt",
        Word + "fldSimple",
        Word + "smartTag",
    ];

    /// <summary>Words a reader sees in the source, in document order.</summary>
    public static IReadOnlyList<string> SourceTokens(XDocument composite)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var body = composite.Root?.Element(WordNames.Docmd + "body");
        if (body is null)
        {
            return [];
        }

        // Text is gathered per paragraph and concatenated with NO separator between runs,
        // because that is what the format means: w:t values are contiguous literal text, and
        // Word splits them mid-word at rsid and proofing boundaries constantly. Inserting a
        // separator here made the oracle demand "conver" and "sion" as separate words from a
        // document whose Markdown correctly said "conversion" -- a false loss on correct
        // output, which is the one failure mode that gets a check deleted.
        var text = new StringBuilder();
        foreach (var paragraph in body.Descendants(Word + "p"))
        {
            foreach (var node in paragraph.Descendants())
            {
                // w:del is a tracked deletion: the reader does not see it, so neither should
                // the Markdown, and the oracle must not demand it.
                if (node.Ancestors(Word + "del").Any())
                {
                    continue;
                }

                if (node.Ancestors().Any(a => DroppedWrappers.Contains(a.Name)))
                {
                    continue;
                }

                if (node.Name == Word + "t")
                {
                    text.Append(node.Value);
                }
                else if (node.Name == Word + "tab" || node.Name == Word + "br")
                {
                    // Both become a space in the output; they are word separators, not
                    // decoration.
                    text.Append(' ');
                }
            }

            // Paragraphs (and therefore table cells, whose content is paragraphs) are separate
            // blocks. Their last and first words are not one word.
            text.Append(' ');
        }

        return Tokenize(text.ToString());
    }

    /// <summary>Words a reader sees in the Markdown, in document order.</summary>
    public static IReadOnlyList<string> OutputTokens(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var text = new StringBuilder();

        // Same rule as the source side: concatenate within a block, separate between blocks.
        // Markdig splits a run of text at every escape and entity, so "file\_name.txt" arrives
        // as three adjacent literals; separating those would invent two words that are not
        // there and report the file name as lost.
        foreach (var block in document.Descendants<LeafBlock>())
        {
            if (block is CodeBlock code && code.Lines.Lines is not null)
            {
                for (var i = 0; i < code.Lines.Count; i++)
                {
                    text.Append(code.Lines.Lines[i].Slice.ToString()).Append(' ');
                }
            }

            if (block.Inline is null)
            {
                text.Append(' ');
                continue;
            }

            foreach (var inline in block.Inline.Descendants())
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        text.Append(literal.ToString());
                        break;
                    case CodeInline inlineCode:
                        text.Append(inlineCode.Content);
                        break;
                    // "&lt;" is how a literal "<" survives; read back it is the source's
                    // character again, and without this the oracle reports it lost.
                    case HtmlEntityInline entity:
                        text.Append(entity.Transcoded.ToString());
                        break;
                    case LineBreakInline:
                        text.Append(' ');
                        break;
                    default:
                        break;
                }
            }

            text.Append(' ');
        }

        return Tokenize(text.ToString());
    }

    /// <summary>
    /// Returns every source word the Markdown failed to show, in order. Empty means the
    /// document's text survived conversion intact.
    /// </summary>
    public static IReadOnlyList<LostText> Check(XDocument composite, string markdown)
    {
        var source = SourceTokens(composite);
        var output = OutputTokens(markdown);

        var lost = new List<LostText>();
        var cursor = 0;

        for (var i = 0; i < source.Count; i++)
        {
            // Bounded lookahead, not "search the rest of the output". Matching greedily to the
            // end let a common token -- a comma, "the" -- match an occurrence thousands of
            // tokens later, which drags the cursor past everything between and reports the
            // whole remainder as lost. That produced 3,529 false losses on a document whose
            // token counts differed by one. The insertions this oracle must tolerate are local
            // (list markers, alt text, link text), never wholesale, so a window both models the
            // real relation and makes a desync self-correcting instead of terminal.
            var limit = Math.Min(output.Count, cursor + LookaheadTokens);
            var found = false;

            for (var j = cursor; j < limit; j++)
            {
                if (string.Equals(output[j], source[i], StringComparison.Ordinal))
                {
                    cursor = j + 1;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                lost.Add(new LostText(source[i], Context(source, i), cursor));
            }
        }

        return lost;
    }

    private static string Context(IReadOnlyList<string> tokens, int index)
    {
        var from = Math.Max(0, index - 4);
        var to = Math.Min(tokens.Count, index + 5);
        return string.Join(' ', tokens.Skip(from).Take(to - from));
    }

    /// <summary>
    /// Splits on whitespace and drops nothing else. Punctuation stays attached deliberately:
    /// "section 4." losing its full stop is a real defect, and normalising it away would hide
    /// exactly the escaping mistakes this is here to find.
    /// </summary>
    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
