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
}
