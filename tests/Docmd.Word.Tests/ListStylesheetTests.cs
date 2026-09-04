namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class ListStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>numId 1 is a bullet list; numId 2 is decimal. Both have three levels.</summary>
    private const string Numbering = """
        <w:num w:numId="1"><w:abstractNumId w:val="10"/></w:num>
        <w:num w:numId="2"><w:abstractNumId w:val="20"/></w:num>
        <w:abstractNum w:abstractNumId="10">
          <w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/></w:lvl>
          <w:lvl w:ilvl="1"><w:numFmt w:val="bullet"/></w:lvl>
          <w:lvl w:ilvl="2"><w:numFmt w:val="bullet"/></w:lvl>
        </w:abstractNum>
        <w:abstractNum w:abstractNumId="20">
          <w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl>
          <w:lvl w:ilvl="1"><w:numFmt w:val="lowerLetter"/></w:lvl>
        </w:abstractNum>
        """;

    private static string Item(string text, int numId, int ilvl) => $"""
        <w:p><w:pPr><w:numPr><w:ilvl w:val="{ilvl}"/><w:numId w:val="{numId}"/></w:numPr></w:pPr>
          <w:r><w:t>{text}</w:t></w:r></w:p>
        """;

    private static async Task<string> ToMarkdownAsync(string bodyInner)
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles/></docmd:styles>
              <docmd:numbering><w:numbering>{Numbering}</w:numbering></docmd:numbering>
              <docmd:relationships/><docmd:properties/>
            </docmd:package>
            """, LoadOptions.PreserveWhitespace);

        HeadingAnnotator.Annotate(composite);
        return MarkdownSerializer.Serialize(
            await MarkdownTransform.RunAsync(composite, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BulletList_BecomesHyphens()
        => (await ToMarkdownAsync(Item("One", 1, 0) + Item("Two", 1, 0)))
            .Should().Be("- One\n- Two\n");

    [Fact]
    public async Task NumberedList_BecomesDigits()
        => (await ToMarkdownAsync(Item("First", 2, 0) + Item("Second", 2, 0)))
            .Should().Be("1. First\n2. Second\n");

    [Fact]
    public async Task NestedItems_AreIndentedUnderTheirParent()
        => (await ToMarkdownAsync(
                Item("Top", 1, 0) + Item("Child", 1, 1) + Item("Back", 1, 0)))
            .Should().Be("- Top\n  - Child\n- Back\n");

    [Fact]
    public async Task ThreeLevelsNest()
        => (await ToMarkdownAsync(
                Item("A", 1, 0) + Item("B", 1, 1) + Item("C", 1, 2)))
            .Should().Be("- A\n  - B\n    - C\n");

    [Fact]
    public async Task ListEnds_WhenAnOrdinaryParagraphFollows()
        => (await ToMarkdownAsync(
                Item("One", 1, 0) + """<w:p><w:r><w:t>After.</w:t></w:r></w:p>"""))
            .Should().Be("- One\n\nAfter.\n");

    [Fact]
    public async Task TwoAdjacentListsWithDifferentNumIds_DoNotMerge()
    {
        // Distinct numIds are distinct lists even when adjacent. Merging them would
        // renumber the second one from where the first left off.
        var markdown = await ToMarkdownAsync(Item("Bullet", 1, 0) + Item("Number", 2, 0));

        markdown.Should().Be("- Bullet\n\n1. Number\n");
    }

    [Fact]
    public async Task ListWithNoMatchingNumberingDefinition_FallsBackToBullets()
    {
        // numId 99 is not defined. A missing definition must degrade, never throw:
        // this is a batch tool and one malformed document cannot stop a corpus.
        var markdown = await ToMarkdownAsync(Item("Orphan", 99, 0));

        markdown.Should().Be("- Orphan\n");
    }
}
