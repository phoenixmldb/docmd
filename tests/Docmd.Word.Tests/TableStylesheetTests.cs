namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using PhoenixmlDb.Xslt;
using Xunit;

public sealed class TableStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Cell(string text, string properties = "")
        => $"""<w:tc><w:tcPr>{properties}</w:tcPr><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc>""";

    private static async Task<string> ToMarkdownAsync(string bodyInner)
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles/></docmd:styles>
              <docmd:numbering><w:numbering/></docmd:numbering>
              <docmd:relationships/><docmd:properties/>
            </docmd:package>
            """);

        HeadingAnnotator.Annotate(composite);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(StylesheetLoader.Read("markdown.xslt"));
        return MarkdownSerializer.Serialize(XDocument.Parse(await transformer.TransformAsync(
            composite.ToString(), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Table_BecomesAPipeTableWithADelimiterRow()
        => (await ToMarkdownAsync($"""
            <w:tbl>
              <w:tr>{Cell("Item")}{Cell("Status")}</w:tr>
              <w:tr>{Cell("Vent")}{Cell("Pass")}</w:tr>
            </w:tbl>
            """))
            .Should().Be("| Item | Status |\n| --- | --- |\n| Vent | Pass |\n");

    [Fact]
    public async Task HorizontallyMergedCell_KeepsTheRowWidth()
    {
        // GFM has no colspan. A ragged row breaks the entire table for a parser, so the
        // spanned positions are emitted empty rather than omitted.
        var markdown = await ToMarkdownAsync($"""
            <w:tbl>
              <w:tr>{Cell("A")}{Cell("B")}</w:tr>
              <w:tr>{Cell("Wide", """<w:gridSpan w:val="2"/>""")}</w:tr>
            </w:tbl>
            """);

        markdown.Should().Be("| A | B |\n| --- | --- |\n| Wide |  |\n");
    }

    [Fact]
    public async Task VerticallyMergedContinuationCell_IsEmpty()
        => (await ToMarkdownAsync($"""
            <w:tbl>
              <w:tr>{Cell("A")}{Cell("B")}</w:tr>
              <w:tr>{Cell("Cont", """<w:vMerge/>""")}{Cell("C")}</w:tr>
            </w:tbl>
            """))
            .Should().Be("| A | B |\n| --- | --- |\n|  | C |\n");

    [Fact]
    public async Task MultiParagraphCell_IsJoinedOntoOneLine()
    {
        // A newline inside a cell terminates the row.
        var markdown = await ToMarkdownAsync("""
            <w:tbl><w:tr>
              <w:tc><w:p><w:r><w:t>One.</w:t></w:r></w:p><w:p><w:r><w:t>Two.</w:t></w:r></w:p></w:tc>
            </w:tr></w:tbl>
            """);

        markdown.Should().Be("| One. Two. |\n| --- |\n");
    }

    [Fact]
    public async Task NestedTable_IsFlattenedIntoItsContainingCell()
    {
        // GFM cannot express nesting. The words survive for retrieval; the structure
        // does not, and the audit reports it.
        var markdown = await ToMarkdownAsync($"""
            <w:tbl><w:tr>
              <w:tc><w:tbl><w:tr>{Cell("Inner")}</w:tr></w:tbl></w:tc>
              {Cell("Outer")}
            </w:tr></w:tbl>
            """);

        markdown.Should().Be("| Inner | Outer |\n| --- | --- |\n");
    }

    [Fact]
    public async Task CellContent_IsEscapedSoPipesDoNotSplitTheRow()
    {
        // A literal pipe in cell text would otherwise invent a column.
        var markdown = await ToMarkdownAsync($"""<w:tbl><w:tr>{Cell("a|b")}</w:tr></w:tbl>""");

        markdown.Should().Be("| a\\|b |\n| --- |\n");
    }
}
