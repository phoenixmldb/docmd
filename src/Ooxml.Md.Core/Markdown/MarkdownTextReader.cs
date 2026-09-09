namespace Ooxml.Md.Core.Markdown;

using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

/// <summary>
/// Recovers the words a reader would see from a Markdown document, by parsing it with an
/// implementation docmd did not write.
/// </summary>
/// <remarks>
/// <para>
/// Reading the output back through a real parser is the whole point, and string matching is not
/// a substitute. An underscore docmd escaped correctly reads back as an underscore and matches
/// its source; an asterisk docmd failed to escape reads back as emphasis markup and its word
/// goes missing. The failure surfaces as a lost word instead of hiding behind a comparison that
/// our own escaping rules would have passed.
/// </para>
/// <para>
/// Text within a block is concatenated with no separator, and only blocks are separated. Markdig
/// splits a run of text at every escape and entity, so <c>file\_name.txt</c> arrives as three
/// adjacent literals; separating those would invent two words the document never had.
/// </para>
/// </remarks>
public static class MarkdownTextReader
{
    /// <summary>
    /// A pipeline shaped like the flavour docmd emits, not the widest one Markdig offers.
    /// </summary>
    /// <remarks>
    /// UseAdvancedExtensions turns on list extras, which read "a." and "i." at the start of a
    /// line as ordered-list markers. GFM does not, so docmd correctly leaves them as text, and
    /// a reader configured that way then reported every one of them as a word the conversion
    /// had lost: 44 of them in one deployment runbook whose steps are lettered. Holding the
    /// output to a grammar wider than the one it targets manufactures losses that are not there.
    ///
    /// Emphasis extras are limited to strikethrough for the same reason: the rest add
    /// subscript, superscript and inserted-text syntax that GFM has no notion of, so a caret or
    /// a tilde in ordinary prose would be read as markup and its neighbours reported missing.
    /// </remarks>
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseAutoLinks()
            .UseTaskLists()
            .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
            .Build();

    /// <summary>The words a reader sees, in document order.</summary>
    public static IReadOnlyList<string> Words(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var text = new StringBuilder();

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
                    // "&lt;" is how a literal "<" survives serialisation; read back it is the
                    // source's character again, and without this it would look lost.
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
    /// Splits text into the words this reader compares.
    /// </summary>
    /// <remarks>
    /// Public because whatever produces the "expected" side of a comparison must split words
    /// exactly the same way this does, or the two disagree about where words begin and report
    /// losses that are really tokenisation differences. Sharing one implementation is the point;
    /// two that merely look alike is the bug.
    ///
    /// Splits on whitespace and drops nothing else. Punctuation stays attached deliberately:
    /// "section 4." losing its full stop is a real defect, and normalising it away would hide
    /// exactly the escaping mistakes this exists to find.
    /// </remarks>
    /// <param name="text">Text to split.</param>
    public static string[] Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
