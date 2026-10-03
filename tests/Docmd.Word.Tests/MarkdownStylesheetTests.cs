namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class MarkdownStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Runs the real stylesheet over a hand-built composite and returns md-XML.</summary>
    private static async Task<XDocument> TransformAsync(string bodyInner, string stylesInner = "", string numberingInner = "")
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles>{stylesInner}</w:styles></docmd:styles>
              <docmd:numbering><w:numbering>{numberingInner}</w:numbering></docmd:numbering>
              <docmd:relationships/>
              <docmd:properties/>
            </docmd:package>
            """, LoadOptions.PreserveWhitespace);

        HeadingAnnotator.Annotate(composite);
        return await MarkdownTransform.RunAsync(composite, TestContext.Current.CancellationToken);
    }

    /// <summary>Transforms, then serialises — the two stages callers actually compose.</summary>
    private static async Task<string> ToMarkdownAsync(string bodyInner, string stylesInner = "", string numberingInner = "")
        => MarkdownSerializer.Serialize(await TransformAsync(bodyInner, stylesInner, numberingInner));

    [Fact]
    public async Task Paragraph_BecomesAParagraph()
        => (await ToMarkdownAsync("""<w:p><w:r><w:t>Hello.</w:t></w:r></w:p>"""))
            .Should().Be("Hello.\n");

    [Fact]
    public async Task Heading_BecomesHashesAtTheAnnotatedLevel()
        => (await ToMarkdownAsync("""
            <w:p><w:pPr><w:outlineLvl w:val="1"/></w:pPr><w:r><w:t>Findings</w:t></w:r></w:p>
            """))
            .Should().Be("## Findings\n");

    [Fact]
    public async Task Heading_DropsInlineBoldToAvoidNoise()
    {
        // "# **Title**" is valid but adds nothing -- the heading is already prominent.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr>
              <w:r><w:rPr><w:b/></w:rPr><w:t>Title</w:t></w:r>
            </w:p>
            """);

        markdown.Should().Be("# Title\n");
    }

    [Fact]
    public async Task Runs_CarryBoldAndItalic()
        => (await ToMarkdownAsync("""
            <w:p>
              <w:r><w:rPr><w:b/></w:rPr><w:t>bold</w:t></w:r>
              <w:r><w:t> and </w:t></w:r>
              <w:r><w:rPr><w:i/></w:rPr><w:t>italic</w:t></w:r>
            </w:p>
            """))
            .Should().Be("**bold** and *italic*\n");

    [Fact]
    public async Task Runs_NestBoldAndItalicTogether()
        => (await ToMarkdownAsync("""
            <w:p><w:r><w:rPr><w:b/><w:i/></w:rPr><w:t>both</w:t></w:r></w:p>
            """))
            .Should().Be("***both***\n");

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    public async Task RunWithBoldTurnedOff_IsNotBold(string offValue)
        // w:b is an ST_OnOff toggle. "<w:b w:val='0'/>" turns bold OFF against a style
        // that turns it on, which is how every ordinary run inside a bold-styled block is
        // written; an existence test bolds all of them.
        => (await ToMarkdownAsync($"""
            <w:p><w:r><w:rPr><w:b w:val="{offValue}"/></w:rPr><w:t>plain</w:t></w:r></w:p>
            """))
            .Should().Be("plain\n");

    [Fact]
    public async Task RunWithItalicTurnedOff_IsNotItalic()
        => (await ToMarkdownAsync("""
            <w:p><w:r><w:rPr><w:i w:val="0"/></w:rPr><w:t>plain</w:t></w:r></w:p>
            """))
            .Should().Be("plain\n");

    [Fact]
    public async Task OutlineLevelNine_IsBodyTextNotASixthLevelHeading()
        // Level 9 is ECMA-376's body text. Emitted as a heading it became "######", which
        // looks like a deliberate deep heading rather than the mistake it was.
        => (await ToMarkdownAsync("""
            <w:p><w:pPr><w:outlineLvl w:val="9"/></w:pPr><w:r><w:t>Ordinary body text</w:t></w:r></w:p>
            """))
            .Should().Be("Ordinary body text\n");

    [Fact]
    public async Task Runs_SplitMidWordByWordAreRejoined()
    {
        // Word splits runs constantly -- spell-check state, rsids, formatting boundaries.
        // Emitting one md:text per run would produce "**re**suming" style artefacts.
        var markdown = await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t>resum</w:t></w:r>
              <w:r><w:t>ing</w:t></w:r>
            </w:p>
            """);

        markdown.Should().Be("resuming\n");
    }

    [Fact]
    public async Task PreservedSpaces_AreHonoured()
        => (await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t xml:space="preserve">before </w:t></w:r>
              <w:r><w:t>after</w:t></w:r>
            </w:p>
            """))
            .Should().Be("before after\n");

    [Fact]
    public async Task SpaceOnlyRun_BetweenBoldRuns_SurvivesAsASpace()
    {
        // A run whose entire content is one space is what Word puts between a bold phrase
        // and the next word, on either side of a w:hyperlink, and wherever an rsid or
        // proofing boundary falls. XDocument.Parse's default LoadOptions.None discards
        // whitespace-only text nodes, so this used to serialise as "**Safety****Review**"
        // -- two words welded together with four asterisks between them.
        var markdown = await ToMarkdownAsync("""
            <w:p>
              <w:r><w:rPr><w:b/></w:rPr><w:t>Safety</w:t></w:r>
              <w:r><w:t xml:space="preserve"> </w:t></w:r>
              <w:r><w:rPr><w:b/></w:rPr><w:t>Review</w:t></w:r>
            </w:p>
            """);

        markdown.Should().Be("**Safety** **Review**\n");
    }

    [Fact]
    public async Task SpaceOnlyRun_BetweenPlainRuns_SurvivesAsASpace()
        => (await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t>Hello</w:t></w:r>
              <w:r><w:t xml:space="preserve"> </w:t></w:r>
              <w:r><w:t>world</w:t></w:r>
            </w:p>
            """))
            .Should().Be("Hello world\n");

    [Fact]
    public async Task LineBreak_BecomesAHardBreak()
        => (await ToMarkdownAsync("""
            <w:p><w:r><w:t>one</w:t><w:br/><w:t>two</w:t></w:r></w:p>
            """))
            .Should().Be("one  \ntwo\n");

    [Fact]
    public async Task Tab_BecomesASingleSpace()
    {
        // Word's tab is a word separator, not decoration. Markdown has no tab stops, so
        // the layout cannot survive -- but omitting the element welded "Name" and "Value"
        // into "NameValue", which loses the words too.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:r><w:t>Name</w:t><w:tab/><w:t>Value</w:t></w:r></w:p>
            """);

        markdown.Should().Be("Name Value\n");
    }

    [Fact]
    public async Task TabInItsOwnRun_BecomesASingleSpace()
        // Word puts the tab in a run of its own at least as often as inline: the run has
        // no w:t at all, so the emit guard has to admit it.
        => (await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t>Name</w:t></w:r>
              <w:r><w:tab/></w:r>
              <w:r><w:t>Value</w:t></w:r>
            </w:p>
            """))
            .Should().Be("Name Value\n");

    [Fact]
    public async Task EmptyParagraphs_AreDropped()
    {
        // Word documents are full of empty paragraphs used as vertical spacing. Emitting
        // them produces runs of blank lines that mean nothing to a reader or an indexer.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:r><w:t>One.</w:t></w:r></w:p>
            <w:p/>
            <w:p><w:r><w:t>Two.</w:t></w:r></w:p>
            """);

        markdown.Should().Be("One.\n\nTwo.\n");
    }

    [Fact]
    public async Task DeletedText_IsNotEmitted()
    {
        // w:delText, not w:t. A naive //w:t harvest silently accepts every tracked change
        // while appearing to work. Full revision handling arrives in Plan 2; for now the
        // default (accept) must at least not resurrect deleted words.
        var markdown = await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t>The vent </w:t></w:r>
              <w:del><w:r><w:delText>was</w:delText></w:r></w:del>
              <w:ins><w:r><w:t>is</w:t></w:r></w:ins>
              <w:r><w:t> compliant</w:t></w:r>
            </w:p>
            """);

        markdown.Should().Be("The vent is compliant\n");
    }

    // ---------------------------------------------------------------------------------------
    // Wrappers a reader never sees, holding text a reader does see.
    //
    // markdown.xslt descends through inline w:sdt, w:fldSimple and w:smartTag, and deliberately
    // does NOT descend into w:instrText or w:del. That behaviour arrived as a fix and shipped
    // with nothing pinning it: the block-level w:sdt route has a test, the inline route had
    // none, and neither exclusion was asserted anywhere. This repo has already paid for that
    // exact gap once - the w:tab branch of SourceWords was deleted, 356 tests stayed green, and
    // 382 phantom losses appeared in the next corpus run. These tests close it.
    //
    // The oracle is why the inclusions are not optional. docmd:text-nodes walks the DESCENDANT
    // axis, so TextCoverageReport already counts this text as words a reader can see; a
    // transform that cannot reach it does not merely omit it, it reports itself as lossy.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task InlineContentControl_KeepsItsText()
    {
        // A content control that is a child of w:p, so its runs are NOT children of w:p. This
        // is the form carrying the variable parts of a templated document - the customer name,
        // the revision, the approval block - which are the fields most worth indexing.
        var markdown = await ToMarkdownAsync(
            """<w:p><w:sdt><w:sdtPr/><w:sdtContent><w:r><w:t>Inside control</w:t></w:r></w:sdtContent></w:sdt></w:p>""");

        markdown.Should().Be("Inside control\n");
    }

    [Fact]
    public async Task InlineContentControl_AsAWholeParagraph_LeavesNoBlankLine()
    {
        // Two axes used to disagree about what counts as a paragraph's text. The empty-paragraph
        // suppression rule asks docmd:visible-text (descendant axis) and so SAW this text and
        // declined to suppress; the inline emitters used the child axis and could not reach it.
        // The words were lost AND a stray blank line was left where they had been. Asserting the
        // exact output pins both halves: fixing the blank line alone would have made the loss
        // less visible rather than less real.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:r><w:t>Before.</w:t></w:r></w:p>
            <w:p><w:sdt><w:sdtPr/><w:sdtContent><w:r><w:t>Controlled</w:t></w:r></w:sdtContent></w:sdt></w:p>
            <w:p><w:r><w:t>After.</w:t></w:r></w:p>
            """);

        markdown.Should().Be("Before.\n\nControlled\n\nAfter.\n");
    }

    [Fact]
    public async Task FieldResult_IsEmitted()
    {
        // w:fldSimple holds a cached result, which is what Word shows. Measured across the
        // corpus the stylesheet was built against, those results were 103 DOCPROPERTY, 12 SEQ,
        // a TITLE and an AUTHOR, with no PAGE and no TOC - every one of them content.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:r><w:t>Title: </w:t></w:r><w:fldSimple w:instr="DOCPROPERTY Title"><w:r><w:t>Zoning Code</w:t></w:r></w:fldSimple></w:p>
            """);

        markdown.Should().Be("Title: Zoning Code\n");
    }

    [Fact]
    public async Task SmartTag_KeepsItsWords()
    {
        // A smart tag is a recognition marker around ordinary words. It contributes nothing of
        // its own and must contribute all of its children.
        var markdown = await ToMarkdownAsync(
            """<w:p><w:smartTag><w:r><w:t>Acme Corp</w:t></w:r></w:smartTag><w:r><w:t> filed.</w:t></w:r></w:p>""");

        markdown.Should().Be("Acme Corp filed.\n");
    }

    [Fact]
    public async Task FieldCode_IsNotEmitted()
    {
        // The other half of the whitelist, and the half that fails loudly if it ever breaks.
        // w:instrText is the field CODE, not its result. Emitting it would put a raw field
        // instruction into a document someone indexes, which is worse than omission because it
        // invents content rather than missing it.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:r><w:instrText>PAGE \* MERGEFORMAT</w:instrText></w:r><w:r><w:t>Page text.</w:t></w:r></w:p>
            """);

        markdown.Should().Be("Page text.\n");
        markdown.Should().NotContain("MERGEFORMAT");
    }

    [Fact]
    public async Task NestedWrappers_AreAllTransparent()
    {
        // The templates recurse through each other, so a control inside a smart tag inside a
        // control has to work. Real templated documents nest these routinely, and a whitelist
        // that only handled one level would look correct on a simpler fixture.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:sdt><w:sdtPr/><w:sdtContent><w:smartTag><w:sdt><w:sdtPr/><w:sdtContent><w:r><w:t>Deeply nested</w:t></w:r></w:sdtContent></w:sdt></w:smartTag></w:sdtContent></w:sdt></w:p>
            """);

        markdown.Should().Be("Deeply nested\n");
    }

    [Fact]
    public async Task Heading_KeepsWordsApartAcrossATab()
    {
        // Found by the text-preservation oracle on a real signature block: a heading built from
        // docmd:visible-text dropped w:tab entirely and welded the words on either side. The
        // inline path and the table-cell path had both already been fixed for exactly this;
        // visible-text, which serves headings and code blocks, was missed.
        var markdown = await ToMarkdownAsync(
            """<w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Name</w:t><w:tab/><w:t>Value</w:t></w:r></w:p>""");

        markdown.Should().Be("# Name Value\n");
    }

    [Fact]
    public async Task Heading_KeepsWordsApartAcrossALineBreak()
        => (await ToMarkdownAsync(
                """<w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>First</w:t><w:br/><w:t>Second</w:t></w:r></w:p>"""))
            .Should().Be("# First Second\n");

    [Fact]
    public async Task TextBoxContent_BecomesBlocksAfterItsAnchorParagraph()
    {
        // A text box holds paragraphs, not runs, so its words cannot be emitted inline where the
        // box is anchored. Before this they were not emitted at all: an invoice lost its entire
        // certification statement, and a callout is exactly the summarising sentence a retrieval
        // index most wants.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:r><w:t>Before</w:t></w:r></w:p>
            <w:p><w:r><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml"><v:textbox><w:txbxContent>
              <w:p><w:r><w:t>Boxed callout</w:t></w:r></w:p>
            </w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>
            <w:p><w:r><w:t>After</w:t></w:r></w:p>
            """);

        markdown.Should().Be("Before\n\nBoxed callout\n\nAfter\n");
    }

    [Fact]
    public async Task AParagraphHoldingOnlyATextBox_DoesNotAlsoEmitABlankParagraph()
        // The anchor paragraph has no words of its own. Counting the box's words as the
        // paragraph's made it look non-empty, so it emitted an empty para beside the content.
        => (await ToMarkdownAsync("""
            <w:p><w:r><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml"><v:textbox><w:txbxContent>
              <w:p><w:r><w:t>Only this</w:t></w:r></w:p>
            </w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>
            """))
            .Should().Be("Only this\n");

    [Fact]
    public async Task ATextBoxInsideATableCell_IsNotCountedTwice()
    {
        // The cell extractor walked every descendant w:p and then every descendant w:t of each,
        // so a paragraph containing a text box contributed the box's words and the box's own
        // paragraph contributed them again. A real invoice printed its certification twice.
        var markdown = await ToMarkdownAsync("""
            <w:tbl><w:tr><w:tc>
              <w:p><w:r><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml"><v:textbox><w:txbxContent>
                <w:p><w:r><w:t>certified</w:t></w:r></w:p>
              </w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>
            </w:tc></w:tr></w:tbl>
            """);

        markdown.Split("certified").Length.Should().Be(2, "the word should appear exactly once");
    }
    // ---------------------------------------------------------------------------------------
    // Leading whitespace at a paragraph start (#38).
    //
    // w:tab emits one space, and four or more at a paragraph start is CommonMark's indented
    // code block: the words survive verbatim but are marked up as code, so a reader sees a
    // monospaced block and an index sees a code span where prose was intended. Deeply
    // tab-indented paragraphs are a manual-layout habit from older word processors, which is
    // exactly what docmd converts.
    //
    // Only the FIRST physical line can do this. CommonMark does not let an indented code block
    // interrupt a paragraph, so a continuation line after a hard break is a lazy continuation
    // whatever its indentation -- which is why the fix is scoped to the first line and the
    // tests below pin that scope rather than trimming everything in sight.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task FourLeadingTabs_DoNotBecomeACodeBlock()
    {
        var markdown = await ToMarkdownAsync(
            """<w:p><w:r><w:tab/><w:tab/><w:tab/><w:tab/><w:t>Deeply indented prose.</w:t></w:r></w:p>""");

        markdown.Should().Be("Deeply indented prose.\n");
    }

    [Fact]
    public async Task OneLeadingTab_IsKept()
    {
        // Below CommonMark's four-space threshold, so it renders identically either way and is
        // not the defect. Trimming it anyway was measured first: it moved 1,100 lines across
        // all 13 corpus documents where only 25 were the code-block bug. Output docmd's users
        // commit and review is not worth reflowing for a change nothing renders.
        var markdown = await ToMarkdownAsync(
            """<w:p><w:r><w:tab/><w:t>Slightly indented.</w:t></w:r></w:p>""");

        markdown.Should().Be(" Slightly indented.\n");
    }

    [Fact]
    public async Task LeadingWhitespaceAfterAHardBreak_IsLeftAlone()
    {
        // The scope guard. A continuation line cannot start a code block, so its indentation
        // is harmless, and stripping it would discard layout the document actually carries.
        // If this test ever starts failing, the trim has widened beyond the defect.
        var markdown = await ToMarkdownAsync(
            """<w:p><w:r><w:t>First line.</w:t><w:br/><w:tab/><w:tab/><w:tab/><w:tab/><w:t>Indented continuation.</w:t></w:r></w:p>""");

        markdown.Should().Be("First line.  \n    Indented continuation.\n");
    }


}
