namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.StyleMapping;
using Xunit;

/// <summary>
/// A style map fails silently by nature: a misspelled key never matches, and the output is
/// identical to passing no map. These pin the report that makes the failure visible -- and,
/// just as importantly, keep it from crying wolf about styles docmd already handles.
/// </summary>
public sealed class StyleUsageReportTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static XDocument Composite(string bodyInner, string stylesInner = "")
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles>{stylesInner}</w:styles></docmd:styles>
              <docmd:numbering><w:numbering/></docmd:numbering>
              <docmd:relationships/><docmd:properties/>
            </docmd:package>
            """, LoadOptions.PreserveWhitespace);

        // The report reads the annotator's verdict, so it has to have run -- exactly as it has
        // by the time DocumentConverter builds the report.
        HeadingAnnotator.Annotate(composite);
        return composite;
    }

    private static string Para(string style, string text = "Text") =>
        $"""<w:p><w:pPr><w:pStyle w:val="{style}"/></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>""";

    [Fact]
    public void AnEntryMatchingNoStyle_IsReported()
        => StyleUsageReport.Build(Composite(Para("Caution")), StyleMap.Parse("Cautoin: { as: blockquote }"))
            .EntriesThatMatchedNothing.Should().ContainSingle().Which.Should().Be("Cautoin");

    [Fact]
    public void AnEntryMatchingAStyle_IsNotReported()
        => StyleUsageReport.Build(Composite(Para("Caution")), StyleMap.Parse("Caution: { as: blockquote }"))
            .EntriesThatMatchedNothing.Should().BeEmpty();

    [Fact]
    public void AnEntryMatchingByLocalisedName_IsNotReported()
        => StyleUsageReport.Build(
                Composite(Para("Style17"), """<w:style w:styleId="Style17"><w:name w:val="Corporate Heading"/></w:style>"""),
                StyleMap.Parse("Corporate Heading: { as: heading }"))
            .EntriesThatMatchedNothing.Should().BeEmpty("a map may name either the id or the name");

    [Fact]
    public void AnUncoveredStyle_IsSuggested()
        => StyleUsageReport.Build(Composite(Para("HouseNote")), StyleMap.Empty)
            .MostUsedUnmappedStyles.Should().ContainSingle()
            .Which.Should().Be(new UnmappedStyle("HouseNote", 1));

    [Fact]
    public void AStyleDocmdAlreadyMadeAHeading_IsNotSuggested()
    {
        // The load-bearing rule. Suggesting "Heading1" reads as "docmd missed this", and acting
        // on it adds an entry that changes no output -- worse than saying nothing at all.
        var composite = Composite(
            """<w:p><w:pPr><w:pStyle w:val="Heading1"/><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Scope</w:t></w:r></w:p>""");

        StyleUsageReport.Build(composite, StyleMap.Empty)
            .MostUsedUnmappedStyles.Should().BeEmpty();
    }

    [Fact]
    public void AStyleDocmdAlreadyMadeAListItem_IsNotSuggested()
        => StyleUsageReport.Build(
                Composite("""<w:p><w:pPr><w:pStyle w:val="ListBullet"/><w:numPr><w:numId w:val="3"/></w:numPr></w:pPr><w:r><w:t>One</w:t></w:r></w:p>"""),
                StyleMap.Empty)
            .MostUsedUnmappedStyles.Should().BeEmpty();

    [Fact]
    public void AStyleUsedBothWaysIsSuggested_CountingOnlyTheUsesDocmdLeftAlone()
    {
        // The same style can be a heading on one paragraph and nothing on another. The count
        // has to be the uses a map entry would actually change, or it overstates the case.
        var composite = Composite(
            """<w:p><w:pPr><w:pStyle w:val="Mixed"/><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>A</w:t></w:r></w:p>"""
            + Para("Mixed", "B") + Para("Mixed", "C"));

        StyleUsageReport.Build(composite, StyleMap.Empty)
            .MostUsedUnmappedStyles.Should().ContainSingle()
            .Which.Count.Should().Be(2);
    }

    [Fact]
    public void WordsOwnPlumbing_IsNotSuggested()
        => StyleUsageReport.Build(
                Composite(Para("Normal") + Para("ListParagraph")
                          + """<w:p><w:r><w:rPr><w:rStyle w:val="Hyperlink"/></w:rPr><w:t>link</w:t></w:r></w:p>"""),
                StyleMap.Empty)
            .MostUsedUnmappedStyles.Should().BeEmpty("these carry no house meaning");

    [Fact]
    public void AMappedStyle_IsNotSuggested()
        => StyleUsageReport.Build(Composite(Para("HouseNote")), StyleMap.Parse("HouseNote: { as: blockquote }"))
            .MostUsedUnmappedStyles.Should().BeEmpty();

    [Fact]
    public void ACharacterStyle_IsSuggested()
        // Word's own Strong style bolds nothing unless the run also carries w:b, so a document
        // leaning on it loses its emphasis entirely without a map entry.
        => StyleUsageReport.Build(
                Composite("""<w:p><w:r><w:rPr><w:rStyle w:val="Strong"/></w:rPr><w:t>x</w:t></w:r></w:p>"""),
                StyleMap.Empty)
            .MostUsedUnmappedStyles.Should().ContainSingle().Which.Style.Should().Be("Strong");

    [Fact]
    public void SuggestionsAreBusiestFirst_AndCapped()
    {
        var body = string.Concat(Enumerable.Range(1, 7)
            .Select(i => string.Concat(Enumerable.Repeat(Para($"House{i}"), i))));

        var suggestions = StyleUsageReport.Build(Composite(body), StyleMap.Empty, topUnmapped: 3)
            .MostUsedUnmappedStyles;

        suggestions.Should().HaveCount(3);
        suggestions.Select(s => s.Style).Should().Equal("House7", "House6", "House5");
    }

    [Fact]
    public void TheDisplayNameIsShown_WhenItDiffersFromTheId()
        => StyleUsageReport.Build(
                Composite(Para("BodyTextFirstIndent"),
                          """<w:style w:styleId="BodyTextFirstIndent"><w:name w:val="Body Text First Indent"/></w:style>"""),
                StyleMap.Empty)
            .MostUsedUnmappedStyles.Should().ContainSingle()
            .Which.Style.Should().Be("BodyTextFirstIndent (Body Text First Indent)",
                "a map may name either, and the id alone is often unrecognisable");
}
