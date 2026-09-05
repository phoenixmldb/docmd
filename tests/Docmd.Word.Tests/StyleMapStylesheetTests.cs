namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.StyleMapping;
using Xunit;

/// <summary>
/// House styles carry meaning the format does not: <c>CautionNote</c> is a warning and nothing in
/// OOXML says so. These exercise the map end to end, through the real stylesheet.
/// </summary>
public sealed class StyleMapStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static async Task<string> ConvertAsync(string bodyInner, string map, string stylesInner = "")
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles>{stylesInner}</w:styles></docmd:styles>
              <docmd:numbering><w:numbering/></docmd:numbering>
              <docmd:relationships/><docmd:properties/>
            </docmd:package>
            """, LoadOptions.PreserveWhitespace);

        composite.Root!.Add(StyleMap.Parse(map).ToXml(XNamespace.Get("https://phoenixml.dev/docmd")));
        HeadingAnnotator.Annotate(composite);

        var mdXml = await MarkdownTransform.RunAsync(composite, TestContext.Current.CancellationToken);
        return MarkdownSerializer.Serialize(mdXml);
    }

    private static string Para(string style, string text) =>
        $"""<w:p><w:pPr><w:pStyle w:val="{style}"/></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>""";

    [Fact]
    public async Task MappedStyle_BecomesAHeadingAtTheGivenLevel()
        => (await ConvertAsync(Para("CorpTitle", "Scope"), "CorpTitle: { as: heading, level: 2 }"))
            .Should().Be("## Scope\n");

    [Fact]
    public async Task MappedStyle_BecomesABlockquoteWithItsPrefix()
        => (await ConvertAsync(Para("CautionNote", "Disconnect power first"),
                               """CautionNote: { as: blockquote, prefix: "Caution: " }"""))
            .Should().Be("> Caution: Disconnect power first\n");

    [Fact]
    public async Task MappedStyle_MatchesOnTheLocalisedStyleNameToo()
        // w:styleId is not w:name, and a map written against either should work — a template
        // author sees the name in Word's UI, never the id.
        => (await ConvertAsync(
                Para("Style17", "Scope"),
                "Corporate Heading: { as: heading, level: 1 }",
                """<w:style w:styleId="Style17"><w:name w:val="Corporate Heading"/></w:style>"""))
            .Should().Be("# Scope\n");

    [Fact]
    public async Task MappedList_GroupsConsecutiveItemsIntoOneList()
    {
        // The reason mapped list kinds join the numbering grouping key rather than emitting a
        // list each: otherwise consecutive steps become separate single-item lists with blank
        // lines between them, which reads as three lists rather than one procedure.
        var markdown = await ConvertAsync(
            Para("ProcedureStep", "Disconnect power") +
            Para("ProcedureStep", "Remove the panel") +
            Para("ProcedureStep", "Inspect the vent"),
            "ProcedureStep: { as: ordered-list-item }");

        markdown.Should().Be("1. Disconnect power\n2. Remove the panel\n3. Inspect the vent\n");
    }

    [Fact]
    public async Task MappedList_HonoursBulletVersusOrdered()
        => (await ConvertAsync(Para("Bullet", "One") + Para("Bullet", "Two"),
                               "Bullet: { as: list-item }"))
            .Should().Be("- One\n- Two\n");

    [Fact]
    public async Task MappedCharacterStyle_BecomesACodeSpan()
        => (await ConvertAsync(
                """<w:p><w:r><w:t>Order </w:t></w:r><w:r><w:rPr><w:rStyle w:val="PartNumber"/></w:rPr><w:t>AS9110-4</w:t></w:r></w:p>""",
                "PartNumber: { as: inline-code }"))
            .Should().Be("Order `AS9110-4`\n");

    [Fact]
    public async Task MappedCharacterStyle_ReplacesTheInferredFormatting()
    {
        // An explicit instruction beats inference, exactly as it does for paragraphs. A run that
        // is bold AND mapped to code cannot be both: a code span renders no markup inside it.
        var markdown = await ConvertAsync(
            """<w:p><w:r><w:rPr><w:b/><w:rStyle w:val="PartNumber"/></w:rPr><w:t>AS9110</w:t></w:r></w:p>""",
            "PartNumber: { as: inline-code }");

        markdown.Should().Be("`AS9110`\n");
    }

    [Fact]
    public async Task MappedParagraph_BeatsTheInferredHeading()
    {
        // Priority 4. A style that Word would call a heading, deliberately demoted by the map.
        var markdown = await ConvertAsync(
            """<w:p><w:pPr><w:pStyle w:val="Heading1"/><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Not a heading</w:t></w:r></w:p>""",
            "Heading1: { as: para }");

        markdown.Should().Be("Not a heading\n");
    }

    [Fact]
    public async Task NoMap_LeavesEverythingToTheBuiltInBehaviour()
        => (await ConvertAsync(
                """<w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Scope</w:t></w:r></w:p>""",
                ""))
            .Should().Be("# Scope\n");

    [Fact]
    public async Task UnmappedStyle_FallsThroughUntouched()
        => (await ConvertAsync(Para("SomethingElse", "Body text"), "CorpTitle: { as: heading }"))
            .Should().Be("Body text\n");
}
