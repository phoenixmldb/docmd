namespace Ooxml.Md.Core.Markdown;

using System.Globalization;
using System.Text;
using System.Xml.Linq;

/// <summary>
/// Turns an md-XML tree into Markdown text.
/// </summary>
/// <remarks>
/// This exists so that every whitespace, blank-line and escaping rule lives in one place.
/// The alternative — emitting text directly from XSLT — spreads those rules across every
/// template, which is where converters classically produce subtly broken output. See
/// spec §4.5.
/// </remarks>
public static class MarkdownSerializer
{
    /// <summary>Always LF. A byte-identical guarantee that varies by host OS is not one.</summary>
    private const char Newline = '\n';

    public static string Serialize(XDocument mdXml, MarkdownOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mdXml);
        var effective = options ?? MarkdownOptions.Default;

        var root = mdXml.Root ?? throw new ArgumentException("The md-XML document has no root.", nameof(mdXml));
        var builder = new StringBuilder();
        WriteBlocks(builder, root.Elements(), effective, indent: "");

        var text = builder.ToString().TrimEnd('\n');
        return text.Length == 0 ? "" : text + Newline;
    }

    private static void WriteBlocks(StringBuilder builder, IEnumerable<XElement> blocks, MarkdownOptions options, string indent, bool blankLineBeforeNestedList = true)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (!first && (blankLineBeforeNestedList || block.Name != MdNames.List))
            {
                builder.Append(Newline);
            }

            first = false;
            WriteBlock(builder, block, options, indent);
        }
    }

    private static void WriteBlock(StringBuilder builder, XElement block, MarkdownOptions options, string indent)
    {
        if (block.Name == MdNames.Heading)
        {
            var level = Math.Clamp(ReadInt(block, "level") ?? 1, 1, 6);
            var line = new StringBuilder();
            WriteInline(line, block.Nodes(), options);
            builder.Append(indent).Append('#', level).Append(' ').Append(TrimTrailingHorizontalWhitespace(line.ToString()));
            builder.Append(Newline);
        }
        else if (block.Name == MdNames.Para)
        {
            var line = new StringBuilder();
            WriteInline(line, block.Nodes(), options);

            // Real Word/LibreOffice output routinely leaves a stray space at the very end
            // of a paragraph's or list item's text (the space Word inserts before a
            // paragraph mark survives into w:t). It is invisible when rendered -- a lone
            // trailing space is not CommonMark's two-space hard break -- but it is noise
            // in the committed file and a magnet for editors/linters that strip trailing
            // whitespace on save, which would otherwise make a re-conversion look like a
            // diff against nothing. Trimmed only at the very end of the assembled text, so
            // a deliberate mid-paragraph hard break (md:br's "  \n") is untouched.
            var trimmed = TrimTrailingHorizontalWhitespace(line.ToString());

            // A hard break (md:br) puts a literal '\n' inside the assembled text, so
            // whether a character is line-leading is a property of each physical line, not
            // of the paragraph as a whole. Escaping only the string's own start would leave
            // a continuation line like "- item" unescaped, and CommonMark reads that as a
            // list item interrupting the paragraph.
            foreach (var physicalLine in trimmed.Split(Newline))
            {
                builder.Append(indent)
                       .Append(MarkdownEscaper.EscapeLineStart(physicalLine))
                       .Append(Newline);
            }
        }
        else if (block.Name == MdNames.Blockquote)
        {
            var inner = new StringBuilder();
            WriteBlocks(inner, block.Elements(), options, indent: "");
            foreach (var line in inner.ToString().TrimEnd('\n').Split('\n'))
            {
                // The blank line separating two paragraphs of one quote gets a bare '>'
                // rather than "> ": the space is invisible, is trailing whitespace an
                // editor or linter will strip on save (making a re-conversion look like a
                // diff against nothing), and two of them would be a hard break. Unlike a
                // list item -- where WriteList drops the prefix entirely on a blank line --
                // the marker itself must stay: a genuinely empty line TERMINATES a
                // blockquote in CommonMark, splitting one quote into two.
                builder.Append(indent).Append(line.Length == 0 ? ">" : "> " + line).Append(Newline);
            }
        }
        else if (block.Name == MdNames.CodeBlock)
        {
            var language = (string?)block.Attribute("language") ?? "";
            builder.Append(indent).Append("```").Append(language).Append(Newline);
            foreach (var line in block.Value.TrimEnd('\n').Split('\n'))
            {
                builder.Append(indent).Append(line).Append(Newline);
            }

            builder.Append(indent).Append("```").Append(Newline);
        }
        else if (block.Name == MdNames.Hr)
        {
            builder.Append(indent).Append("---").Append(Newline);
        }
        else if (block.Name == MdNames.List)
        {
            WriteList(builder, block, options, indent);
        }
        else if (block.Name == MdNames.Table)
        {
            WriteTable(builder, block, options, indent);
        }
        // Unknown block elements are skipped rather than throwing: a stylesheet ahead of
        // this serialiser must degrade, never crash a batch conversion.
    }

    private static void WriteInline(StringBuilder builder, IEnumerable<XNode> nodes, MarkdownOptions options)
    {
        foreach (var node in MergeAdjacentMarkup(nodes))
        {
            if (node is not XElement element)
            {
                continue;
            }

            if (element.Name == MdNames.Text)
            {
                builder.Append(MarkdownEscaper.EscapeInline(element.Value));
            }
            else if (element.Name == MdNames.Strong)
            {
                WriteEmphasis(builder, element, options, "**");
            }
            else if (element.Name == MdNames.Em)
            {
                WriteEmphasis(builder, element, options, "*");
            }
            else if (element.Name == MdNames.Code)
            {
                // Code spans are NOT escaped -- that is their entire purpose. The fence
                // is widened instead if the content contains backticks.
                builder.Append(MarkdownEscaper.CodeSpan(element.Value));
            }
            else if (element.Name == MdNames.Link)
            {
                builder.Append('[');
                WriteInline(builder, element.Nodes(), options);
                builder.Append("](").Append(MarkdownEscaper.EscapeUrl((string?)element.Attribute("href") ?? "")).Append(')');
            }
            else if (element.Name == MdNames.Image)
            {
                builder.Append("![")
                       .Append(MarkdownEscaper.EscapeInline((string?)element.Attribute("alt") ?? ""))
                       .Append("](")
                       .Append(MarkdownEscaper.EscapeUrl((string?)element.Attribute("src") ?? ""))
                       .Append(')');
            }
            else if (element.Name == MdNames.Br)
            {
                // Two trailing spaces is the portable hard break.
                builder.Append("  ").Append(Newline);
            }
        }
    }

    /// <summary>
    /// Writes an emphasis span with its delimiters moved inside any surrounding
    /// whitespace.
    /// </summary>
    /// <remarks>
    /// CommonMark only closes an emphasis run when the closing delimiter is
    /// right-flanking, i.e. not preceded by whitespace. "**bold **and more" therefore
    /// renders as literal asterisks, not bold -- confirmed against Markdig. Selecting a
    /// word together with its trailing space before pressing Ctrl+B is everyday Word
    /// behaviour, so the span arriving here with an outer space on one or both sides is
    /// the common case, not a malformed one. Emitting the whitespace outside the
    /// delimiters preserves both the words and their spacing while keeping the emphasis.
    /// A span with no letters or digits in it gets no delimiters at all. Whitespace is the
    /// obvious case ("****" is four literal asterisks), but punctuation is the one that bites:
    /// "*.*" renders correctly in isolation and breaks the instant it touches a non-space
    /// character, leaving the asterisks visible as text. All of these are wrong, confirmed
    /// against Markdig:
    /// <code>
    /// **b***.*   renders as  b*.*        x*.*y   renders as  x*.*y
    /// **b***,*   renders as  b*,*        x**.**y renders as  x**.**y
    /// </code>
    /// while the same shapes with a word inside are fine, so this is about the content and not
    /// about adjacency. Word leaves a full stop italic whenever the sentence before it was
    /// italicised, which is an artifact of how text gets selected rather than anything the
    /// author meant, and one real document lost 3,440 of its 4,664 words to the pattern. The
    /// words are what matter; an italic full stop is not expressible in Markdown and not worth
    /// corrupting a sentence to attempt.
    /// </remarks>
    private static void WriteEmphasis(StringBuilder builder, XElement element, MarkdownOptions options, string delimiter)
    {
        var inner = new StringBuilder();
        WriteInline(inner, element.Nodes(), options);
        var text = inner.ToString();

        var trimmed = text.Trim();
        if (!trimmed.Any(char.IsLetterOrDigit))
        {
            builder.Append(text);
            return;
        }

        var leadingLength = text.Length - text.TrimStart().Length;
        builder.Append(text, 0, leadingLength)
               .Append(delimiter)
               .Append(trimmed)
               .Append(delimiter)
               .Append(text, leadingLength + trimmed.Length, text.Length - leadingLength - trimmed.Length);
    }

    /// <summary>
    /// Combines adjacent sibling md:strong/md:em elements of the same kind into one.
    /// </summary>
    /// <remarks>
    /// A real Word run is not a stable unit: spell-check, grammar-check and rsid
    /// boundaries constantly split one visually-continuous bold or italic word across two
    /// or more w:r elements that carry identical formatting, and the stylesheet emits one
    /// md:strong/md:em per run because it stays ignorant of Markdown text (see this file's
    /// own header remarks). Left unmerged, "conversion" split as "conver" | "sion" would
    /// serialise as "**conver****sion**" -- indistinguishable once rendered, but visibly
    /// fragmented in the raw text a RAG index or a human reading the file directly
    /// actually sees, which is the single most visible defect a real document exposes
    /// (see docmd's real-document tests). This is a structural property of the md-XML
    /// tree, not of any one caller, so it runs here -- inside <see cref="WriteInline"/>
    /// itself -- and therefore applies uniformly to every block that has inline content
    /// (paragraphs, headings, table cells, list items, link and image alt text) and
    /// recursively to nested emphasis, without either the stylesheet or any one call site
    /// having to know about it.
    /// </remarks>
    private static List<XNode> MergeAdjacentMarkup(IEnumerable<XNode> nodes)
    {
        var merged = new List<XNode>();
        foreach (var node in nodes)
        {
            if (node is XElement element && (element.Name == MdNames.Strong || element.Name == MdNames.Em))
            {
                if (merged.Count > 0 && merged[^1] is XElement previous && previous.Name == element.Name)
                {
                    previous.Add(element.Nodes());
                    continue;
                }

                // Copied rather than referenced: a merge target must be safe to mutate
                // in-place without touching the mdXml document the caller owns.
                merged.Add(new XElement(element.Name, element.Nodes()));
                continue;
            }

            merged.Add(node);
        }

        return merged;
    }

    private static void WriteList(StringBuilder builder, XElement list, MarkdownOptions options, string indent)
    {
        var ordered = string.Equals((string?)list.Attribute("ordered"), "true", StringComparison.Ordinal);
        var number = ReadInt(list, "start") ?? 1;

        foreach (var item in list.Elements(MdNames.Item))
        {
            var marker = ordered
                ? string.Create(CultureInfo.InvariantCulture, $"{number}. ")
                : "- ";
            number++;

            // Continuation lines align under the marker, which is what makes nesting work.
            var childIndent = indent + new string(' ', marker.Length);
            var inner = new StringBuilder();

            // A nested list sits directly under its parent item's own content -- indentation
            // already marks the continuation unambiguously, the way it does for every other
            // physical line of the item. Blank-separating it instead (the general rule for two
            // sibling blocks of the same item, e.g. two paragraphs) reads as a loose list, which
            // is exactly what Word's flat, deeply-nested numPr runs must NOT become: a genuinely
            // nested outline blows up into a wall of blank lines, one per level.
            WriteBlocks(inner, item.Elements(), options, indent: "", blankLineBeforeNestedList: false);

            var lines = inner.ToString().TrimEnd('\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                // A blank separator line between two blocks of the same item gets no
                // indent prefix -- indenting it would leave trailing whitespace, and two
                // trailing spaces is Markdown's hard-line-break syntax.
                string prefix;
                if (i == 0)
                {
                    // An empty item still needs its marker -- dropping it would delete the
                    // item -- but not the space after it, which is trailing whitespace on
                    // an otherwise blank line. "-" and "1." are both valid empty items.
                    prefix = lines[i].Length == 0
                        ? indent + marker.TrimEnd(' ')
                        : indent + marker;
                }
                else if (lines[i].Length == 0)
                {
                    prefix = "";
                }
                else
                {
                    prefix = childIndent;
                }

                builder.Append(prefix).Append(lines[i]).Append(Newline);
            }
        }
    }

    private static void WriteTable(StringBuilder builder, XElement table, MarkdownOptions options, string indent)
    {
        var rows = table.Elements(MdNames.Row).ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        var columnCount = rows.Max(r => r.Elements(MdNames.Cell).Count());

        // GFM has no way to express a headerless table. When the source explicitly marks
        // its first row as not a header (header="false"), a data row must not be silently
        // promoted -- a synthetic empty header row is emitted instead, ahead of the
        // delimiter, so the table still parses and no data is misrepresented.
        var firstRowIsHeader = !string.Equals((string?)rows[0].Attribute("header"), "false", StringComparison.Ordinal);
        if (!firstRowIsHeader)
        {
            WriteTableRow(builder, options, indent, columnCount, Array.Empty<XElement>());
            WriteTableDelimiter(builder, indent, columnCount);
        }

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var cells = rows[rowIndex].Elements(MdNames.Cell).ToArray();
            WriteTableRow(builder, options, indent, columnCount, cells);

            // GFM requires the delimiter row immediately after the header row.
            if (rowIndex == 0 && firstRowIsHeader)
            {
                WriteTableDelimiter(builder, indent, columnCount);
            }
        }
    }

    private static void WriteTableRow(StringBuilder builder, MarkdownOptions options, string indent, int columnCount, XElement[] cells)
    {
        builder.Append(indent).Append("| ");
        for (var column = 0; column < columnCount; column++)
        {
            if (column < cells.Length)
            {
                // A cell built through a temporary buffer, not directly into the row, so a
                // stray trailing space Word left in the cell's own text (the same artefact
                // Para trims -- see its comment) does not sit next to the " | " delimiter.
                var cell = new StringBuilder();
                WriteInline(cell, cells[column].Nodes(), options);
                builder.Append(TrimTrailingHorizontalWhitespace(cell.ToString()));
            }

            builder.Append(" | ");
        }

        builder.Length -= 1; // drop the trailing space
        builder.Append(Newline);
    }

    /// <summary>
    /// Strips trailing ASCII spaces and tabs only -- never '\n', so a deliberate
    /// mid-paragraph hard break (md:br renders as two spaces then a literal '\n') is left
    /// intact. Word/LibreOffice routinely leaves a stray space at the end of a run's text
    /// right before the paragraph mark; this removes exactly that, and nothing else.
    /// </summary>
    private static string TrimTrailingHorizontalWhitespace(string text) => text.TrimEnd(' ', '\t');

    private static void WriteTableDelimiter(StringBuilder builder, string indent, int columnCount)
    {
        builder.Append(indent).Append('|');
        for (var column = 0; column < columnCount; column++)
        {
            builder.Append(" --- |");
        }

        builder.Append(Newline);
    }

    private static int? ReadInt(XElement element, string attributeName)
        => int.TryParse((string?)element.Attribute(attributeName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
