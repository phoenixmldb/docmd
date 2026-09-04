namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Xunit;

public sealed class HeadingAnnotatorTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static XDocument Annotate(string bodyInner, string stylesInner = "")
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles>{stylesInner}</w:styles></docmd:styles>
              <docmd:numbering/>
              <docmd:relationships/>
              <docmd:properties/>
            </docmd:package>
            """);

        HeadingAnnotator.Annotate(composite);
        return composite;
    }

    private static XElement FirstParagraph(XDocument composite)
        => composite.Descendants(WordNames.W + "p").First();

    [Fact]
    public void Annotate_UsesAnExplicitParagraphOutlineLevel()
    {
        var composite = Annotate("""
            <w:p><w:pPr><w:outlineLvl w:val="1"/></w:pPr><w:r><w:t>Findings</w:t></w:r></w:p>
            """);

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("1");
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("OutlineLevel");
        paragraph.Attribute(WordNames.Docmd + "slug")!.Value.Should().Be("findings");
    }

    [Fact]
    public void Annotate_ResolvesAHeadingThroughTheBasedOnChain()
    {
        var composite = Annotate(
            """<w:p><w:pPr><w:pStyle w:val="ProcedureTitle"/></w:pPr><w:r><w:t>Bleed</w:t></w:r></w:p>""",
            """
            <w:style w:styleId="Heading2"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
            <w:style w:styleId="ProcedureTitle"><w:basedOn w:val="Heading2"/></w:style>
            """);

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("1");
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("BasedOn");
    }

    [Fact]
    public void Annotate_FallsBackToTheLocalisedStyleName()
    {
        var composite = Annotate(
            """<w:p><w:pPr><w:pStyle w:val="berschrift1"/></w:pPr><w:r><w:t>Einleitung</w:t></w:r></w:p>""",
            """<w:style w:styleId="berschrift1"><w:name w:val="heading 1"/></w:style>""");

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("0");
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("StyleName");
    }

    [Fact]
    public void Annotate_DetectsAHeadingMadeWithDirectFormattingOnly()
    {
        // The single most common real-world case: someone bolded a large line instead of
        // applying a style. Missing it silently merges two RAG chunks.
        var composite = Annotate("""
            <w:p>
              <w:r><w:rPr><w:b/><w:sz w:val="32"/></w:rPr><w:t>Maintenance Procedure</w:t></w:r>
            </w:p>
            <w:p><w:r><w:t>Disconnect power before servicing the unit.</w:t></w:r></w:p>
            """);

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("DirectFormat");
        // Outline levels are zero-based throughout, so 1 here is Markdown "##".
        // Always this level: inferring depth from font size fails differently on every
        // document, and a flat level still yields correct chunk boundaries.
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("1");
    }

    [Fact]
    public void Annotate_RejectsABoldOversizedLineEndingInAPeriod()
    {
        // Pins the trailing-period guard in isolation: short and well under the length
        // guard, so only HeadingAnnotator.LooksLikeADirectFormatHeading's EndsWith('.')
        // check can be rejecting it.
        var composite = Annotate("""
            <w:p>
              <w:r><w:rPr><w:b/><w:sz w:val="32"/></w:rPr><w:t>A short bold sentence.</w:t></w:r>
            </w:p>
            """);

        FirstParagraph(composite).Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("None");
    }

    [Fact]
    public void Annotate_RejectsABoldOversizedLineOverTheLengthGuardWithNoTrailingPeriod()
    {
        // Pins the length guard in isolation. No trailing period, so EndsWith('.') cannot
        // be why this is rejected -- only text.Length > MaxDirectFormatHeadingLength (120)
        // can be. A 130-character run of 'a' keeps the length self-evident from the
        // literal so a future edit can't silently drift it back under the threshold.
        var text = new string('a', 130);
        text.Length.Should().BeGreaterThan(120);

        var composite = Annotate($"""
            <w:p>
              <w:r><w:rPr><w:b/><w:sz w:val="32"/></w:rPr><w:t>{text}</w:t></w:r>
            </w:p>
            """);

        FirstParagraph(composite).Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("None");
    }

    [Fact]
    public void Annotate_RejectsAPartiallyBoldLine()
    {
        // Pins the allBold guard: one run in the paragraph is not bold, so the paragraph
        // as a whole must not be treated as direct-format even though it is otherwise
        // oversized, short, and period-free.
        var composite = Annotate("""
            <w:p>
              <w:r><w:rPr><w:b/><w:sz w:val="32"/></w:rPr><w:t>Bold part </w:t></w:r>
              <w:r><w:rPr><w:sz w:val="32"/></w:rPr><w:t>not-bold part</w:t></w:r>
            </w:p>
            """);

        FirstParagraph(composite).Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("None");
    }

    [Fact]
    public void Annotate_RejectsABoldLineOnlyThreeHalfPointsOverTheDefault()
    {
        // Default body size falls back to 22 half-points (11pt) when docDefaults says
        // nothing -- see StyleResolverTests.DefaultFontHalfPoints_FallsBackToWordsDefault.
        // DirectFormatSizeMargin is 4, so 25 (default + 3) must fall just short.
        var composite = Annotate("""
            <w:p><w:r><w:rPr><w:b/><w:sz w:val="25"/></w:rPr><w:t>Short bold line</w:t></w:r></w:p>
            """);

        FirstParagraph(composite).Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("None");
    }

    [Fact]
    public void Annotate_AcceptsABoldLineExactlyFourHalfPointsOverTheDefault()
    {
        // The margin check is >=, so 26 (default 22 + the full DirectFormatSizeMargin of
        // 4) is the smallest size that must be accepted.
        var composite = Annotate("""
            <w:p><w:r><w:rPr><w:b/><w:sz w:val="26"/></w:rPr><w:t>Short bold line</w:t></w:r></w:p>
            """);

        FirstParagraph(composite).Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("DirectFormat");
    }

    [Fact]
    public void Annotate_MarksOrdinaryParagraphsAsNotHeadings()
    {
        var composite = Annotate("""<w:p><w:r><w:t>Ordinary body text.</w:t></w:r></w:p>""");

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("None");
        paragraph.Attribute(WordNames.Docmd + "outline-level").Should().BeNull();
        paragraph.Attribute(WordNames.Docmd + "slug").Should().BeNull();
    }

    [Fact]
    public void Annotate_GivesRepeatedHeadingsDistinctSlugs()
    {
        var composite = Annotate("""
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Overview</w:t></w:r></w:p>
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Overview</w:t></w:r></w:p>
            """);

        var slugs = composite.Descendants(WordNames.W + "p")
            .Select(p => p.Attribute(WordNames.Docmd + "slug")!.Value).ToArray();

        slugs.Should().Equal("overview", "overview-1");
    }
}
