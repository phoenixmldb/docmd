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
        <w:num w:numId="3"><w:abstractNumId w:val="90"/></w:num>
        <w:num w:numId="3"><w:abstractNumId w:val="91"/></w:num>
        <w:num w:numId="4"><w:abstractNumId w:val="30"/></w:num>
        <w:abstractNum w:abstractNumId="30">
          <w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl>
          <w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl>
        </w:abstractNum>
        """;

    private static string Item(string text, int numId, int ilvl) => $"""
        <w:p><w:pPr><w:numPr><w:ilvl w:val="{ilvl}"/><w:numId w:val="{numId}"/></w:numPr></w:pPr>
          <w:r><w:t>{text}</w:t></w:r></w:p>
        """;

    private static async Task<string> ToMarkdownAsync(string bodyInner, string numbering = Numbering)
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles/></docmd:styles>
              <docmd:numbering><w:numbering>{numbering}</w:numbering></docmd:numbering>
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

    [Fact]
    public async Task DuplicateNumIdDefinition_DegradesToABulletInsteadOfThrowing()
    {
        // numId 3 is declared twice in the numbering part (merged documents do this).
        // fn:string() over the resulting two-item sequence is a dynamic error, and it
        // escaped ConvertAsync and ended the whole batch run -- the exact outcome the
        // comment above docmd:is-ordered promises cannot happen. Neither of the abstract
        // ids it names is defined, so the first one wins and the list degrades to bullets.
        var markdown = await ToMarkdownAsync(Item("One", 3, 0) + Item("Two", 3, 0));

        markdown.Should().Be("- One\n- Two\n");
    }

    [Fact]
    public async Task DuplicateLevelDefinition_StillReadsTheFormatInsteadOfThrowing()
    {
        // The same defect one hop further along: abstractNum 30 declares w:ilvl 0 twice.
        var markdown = await ToMarkdownAsync(Item("One", 4, 0) + Item("Two", 4, 0));

        markdown.Should().Be("1. One\n2. Two\n");
    }

    [Fact]
    public async Task NumIdZero_IsNotAList()
    {
        // w:numId 0 is how Word CANCELS numbering a paragraph would otherwise inherit
        // from its style. Read as an id, opted-out paragraphs became bullets and
        // consecutive ones grouped into a list the document does not contain.
        var markdown = await ToMarkdownAsync(Item("One.", 0, 0) + Item("Two.", 0, 0));

        markdown.Should().Be("One.\n\nTwo.\n");
    }

    [Fact]
    public async Task NumPrWithNoNumId_KeepsItsText()
    {
        // Numbering inherited from the paragraph style: w:numPr carries w:ilvl but no
        // w:numId, so w:body's grouping key is '' and the paragraph is applied directly
        // rather than through build-list. A rule suppressing w:p[w:pPr/w:numPr] deleted
        // its words entirely. The marker is lost, the sentence is not.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/></w:numPr></w:pPr>
              <w:r><w:t>Inherited numbering.</w:t></w:r></w:p>
            """);

        markdown.Should().Be("Inherited numbering.\n");
    }

    [Fact]
    public async Task NumberedParagraphInsideAContentControl_KeepsItsText()
    {
        // The second route to the same deletion: a block-level w:sdt wrapping a numbered
        // paragraph. w:body groups the w:sdt (not the w:p), and the built-in rule walks
        // into it, so the paragraph arrives at the template rules directly.
        var markdown = await ToMarkdownAsync($"""
            <w:sdt><w:sdtPr/><w:sdtContent>{Item("Inside a content control.", 1, 0)}</w:sdtContent></w:sdt>
            """);

        markdown.Should().Be("Inside a content control.\n");
    }
    // ---------------------------------------------------------------------------------------
    // Guarded casts. markdown.xslt reads three integers straight out of the document, and all
    // three used to abort the whole conversion on a value that would not cast: exit 1, no
    // output file, and in a batch the document is lost rather than degraded. That contradicts
    // the promise stated in the stylesheet itself -- one malformed document must never stop a
    // corpus conversion -- which the duplicate-w:numId and undefined-numId cases already keep.
    //
    // Filed as #37, which named the first of the three. Probing for siblings found the other
    // two, which is the habit this repository has earned: the "one definition of what counts
    // as text" refactor found five readers where a commit message had claimed four.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task NonNumericIlvlInTheDocument_DegradesToLevelZero()
    {
        // w:ilvl/@w:val is a spec-typed integer, so a non-numeric value means a more deeply
        // malformed file than the duplicate-numId case -- which is an argument about priority,
        // not about whether aborting is acceptable. numId 2 is decimal, so degrading to level 0
        // keeps the item ordered and keeps its words.
        var markdown = await ToMarkdownAsync(
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="notanumber"/><w:numId w:val="2"/></w:numPr></w:pPr><w:r><w:t>Item text</w:t></w:r></w:p>""");

        markdown.Should().Be("1. Item text\n");
    }

    [Fact]
    public async Task NonNumericIlvlInNumbering_DegradesToABullet()
    {
        // The same cast, one hop away and in a different part: w:lvl/@w:ilvl inside
        // numbering.xml. #37 did not mention this one. No level then matches, so numFmt
        // resolves to the empty string and the list degrades to a bullet -- exactly what an
        // undefined numId already does.
        var numbering = """
            <w:num w:numId="7"><w:abstractNumId w:val="70"/></w:num>
            <w:abstractNum w:abstractNumId="70">
              <w:lvl w:ilvl="notanumber"><w:numFmt w:val="decimal"/></w:lvl>
            </w:abstractNum>
            """;

        var markdown = await ToMarkdownAsync(
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="7"/></w:numPr></w:pPr><w:r><w:t>Numbered item</w:t></w:r></w:p>""",
            numbering);

        markdown.Should().Be("- Numbered item\n");
    }

    // Numbering continues across an interruption (#39).
    //
    // Word stores no nesting and no list object: every item is a top-level w:p carrying a
    // numId. w:body groups ADJACENT paragraphs sharing a numId, so a note in the middle of a
    // procedure splits one list into two runs, and the second run began again at 1. Word shows
    // 3, because the paragraphs share a numId and numbering continues.
    //
    // No existing check could catch this: every word survives, so the coverage oracle sees
    // nothing, and the output is valid Markdown. Only a reader notices.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInterruptedOrderedList_ContinuesItsNumbering()
    {
        var markdown = await ToMarkdownAsync(
            Item("First", 2, 0)
            + Item("Second", 2, 0)
            + """<w:p><w:r><w:t>An interrupting note.</w:t></w:r></w:p>"""
            + Item("Third", 2, 0));

        markdown.Should().Be("1. First\n2. Second\n\nAn interrupting note.\n\n3. Third\n");
    }

    [Fact]
    public async Task TwoSeparateOrderedLists_EachStartAtOne()
    {
        // The counterpart, and the reason the count is scoped to one numId: two genuinely
        // different lists must not continue each other. numId 2 and numId 4 are both decimal.
        var markdown = await ToMarkdownAsync(
            Item("Alpha", 2, 0)
            + """<w:p><w:r><w:t>Between.</w:t></w:r></w:p>"""
            + Item("Beta", 4, 0));

        markdown.Should().Be("1. Alpha\n\nBetween.\n\n1. Beta\n");
    }

    [Fact]
    public async Task AnInterruptedBulletList_IsUnaffected()
        // Bullets have no number to continue, so the start attribute must not change them.
        => (await ToMarkdownAsync(
                Item("One", 1, 0)
                + """<w:p><w:r><w:t>Note.</w:t></w:r></w:p>"""
                + Item("Two", 1, 0)))
            .Should().Be("- One\n\nNote.\n\n- Two\n");

}
