namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
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
            """, LoadOptions.PreserveWhitespace);

        HeadingAnnotator.Annotate(composite);
        return MarkdownSerializer.Serialize(
            await MarkdownTransform.RunAsync(composite, TestContext.Current.CancellationToken));
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
    public async Task CellWithARunSplitMidWord_IsNotSpaceSeparated()
    {
        // The cell text was built by joining every w:t with a space, so a word Word's own
        // spell-check split across two runs -- which it does constantly -- came out as
        // "conver sion". Runs within one paragraph join with nothing; only the paragraph
        // boundary is a space, as MultiParagraphCell_IsJoinedOntoOneLine pins.
        var markdown = await ToMarkdownAsync("""
            <w:tbl><w:tr>
              <w:tc><w:p>
                <w:r><w:t>The</w:t></w:r>
                <w:r><w:t xml:space="preserve"> full </w:t></w:r>
                <w:r><w:t>conver</w:t></w:r>
                <w:r><w:t>sion</w:t></w:r>
              </w:p></w:tc>
            </w:tr></w:tbl>
            """);

        markdown.Should().Be("| The full conversion |\n| --- |\n");
    }

    [Fact]
    public async Task CellWithALineBreak_KeepsTheWordsApart()
    {
        // A break cannot survive as a break inside a pipe row, but the words it separates
        // must still be separated. This space used to arrive by accident, from the w:t
        // separator the fix above removed.
        var markdown = await ToMarkdownAsync("""
            <w:tbl><w:tr>
              <w:tc><w:p><w:r><w:t>A</w:t><w:br/><w:t>B</w:t></w:r></w:p></w:tc>
            </w:tr></w:tbl>
            """);

        markdown.Should().Be("| A B |\n| --- |\n");
    }

    [Fact]
    public async Task CellWithATab_KeepsTheWordsApart()
        // The same accident, and the same repair. Outside a table this is handled by the
        // inline emitters, which a cell never reaches.
        => (await ToMarkdownAsync("""
            <w:tbl><w:tr>
              <w:tc><w:p><w:r><w:t>Name</w:t><w:tab/><w:t>Value</w:t></w:r></w:p></w:tc>
            </w:tr></w:tbl>
            """))
            .Should().Be("| Name Value |\n| --- |\n");

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
