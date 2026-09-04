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

    private static void WriteBlocks(StringBuilder builder, IEnumerable<XElement> blocks, MarkdownOptions options, string indent)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (!first)
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
            builder.Append(indent).Append('#', level).Append(' ');
            WriteInline(builder, block.Nodes(), options);
            builder.Append(Newline);
        }
        else if (block.Name == MdNames.Para)
        {
            builder.Append(indent);
            WriteInline(builder, block.Nodes(), options);
            builder.Append(Newline);
        }
        else if (block.Name == MdNames.Blockquote)
        {
            var inner = new StringBuilder();
            WriteBlocks(inner, block.Elements(), options, indent: "");
            foreach (var line in inner.ToString().TrimEnd('\n').Split('\n'))
            {
                builder.Append(indent).Append("> ").Append(line).Append(Newline);
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
        foreach (var node in nodes)
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
                builder.Append("**");
                WriteInline(builder, element.Nodes(), options);
                builder.Append("**");
            }
            else if (element.Name == MdNames.Em)
            {
                builder.Append('*');
                WriteInline(builder, element.Nodes(), options);
                builder.Append('*');
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
            WriteBlocks(inner, item.Elements(), options, indent: "");

            var lines = inner.ToString().TrimEnd('\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                // A blank separator line between two blocks of the same item gets no
                // indent prefix -- indenting it would leave trailing whitespace, and two
                // trailing spaces is Markdown's hard-line-break syntax.
                string prefix;
                if (i == 0)
                {
                    prefix = indent + marker;
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
                WriteInline(builder, cells[column].Nodes(), options);
            }

            builder.Append(" | ");
        }

        builder.Length -= 1; // drop the trailing space
        builder.Append(Newline);
    }

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
