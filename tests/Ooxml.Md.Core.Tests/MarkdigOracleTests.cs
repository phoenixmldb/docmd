namespace Ooxml.Md.Core.Tests;

using System.Xml.Linq;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Ooxml.Md.Core.Markdown;
using Xunit;

/// <summary>
/// Serialise, parse back with an independent Markdown implementation, and compare. This
/// answers what a golden file cannot: does the Markdown we wrote MEAN what we intended?
/// One mechanism catches every escaping bug rather than one test per character.
/// Markdig is test-only and never ships in the product. See spec §13.2.
/// </summary>
public sealed class MarkdigOracleTests
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static string RoundTripParagraphText(string literal)
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Para,
                    new XElement(MdNames.Text, literal))));

        var markdown = MarkdownSerializer.Serialize(mdXml);
        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);

        var paragraph = parsed.Descendants<ParagraphBlock>().Single();
        return string.Concat(paragraph.Inline!.Descendants<LiteralInline>().Select(l => l.ToString()));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("part number A*B*C")]
    [InlineData("path/to/my_file_name.txt")]
    [InlineData("a | b | c")]
    [InlineData("see [note] below")]
    [InlineData("regex ^[a-z]+$ matches")]
    [InlineData("100% of 5 < 10 > 2")]
    [InlineData(@"windows\path\here")]
    [InlineData("# not a heading")]
    [InlineData("1. not a list item")]
    [InlineData("- not a bullet")]
    [InlineData("> not a quote")]
    [InlineData("emphasis*without*spaces")]
    [InlineData("back`tick inside")]
    [InlineData("approximately ~~5mm~~ across")]
    public void ParagraphText_SurvivesTheRoundTrip(string literal)
    {
        // If this fails, the serialiser emitted something that a real Markdown parser
        // read as markup rather than as the document's words.
        RoundTripParagraphText(literal).Should().Be(literal);
    }

    /// <summary>
    /// Round-trips an emphasis span embedded in ordinary text, returning both what a
    /// reader ends up seeing and whether the span was actually read as emphasis.
    /// </summary>
    /// <remarks>
    /// The oracle previously covered only md:text inside md:para, which is why an
    /// emphasis bug this obvious survived: CommonMark closes an emphasis run only when
    /// its closing delimiter is right-flanking, so "**bold **and more" renders as literal
    /// asterisks and the word is not bold at all. Extending the same mechanism to
    /// emphasis catches every variant of that at once, which is the argument spec §13.2
    /// makes for having an oracle in the first place.
    /// </remarks>
    private static (string Text, bool WasEmphasised) RoundTripEmphasis(XName kind, string literal)
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Para,
                    new XElement(MdNames.Text, "before "),
                    new XElement(kind, new XElement(MdNames.Text, literal)),
                    new XElement(MdNames.Text, "after"))));

        var parsed = Markdig.Markdown.Parse(MarkdownSerializer.Serialize(mdXml), Pipeline);
        var paragraph = parsed.Descendants<ParagraphBlock>().Single();
        var expectedDelimiters = kind == MdNames.Strong ? 2 : 1;

        return (
            string.Concat(paragraph.Inline!.Descendants<LiteralInline>().Select(l => l.ToString())),
            paragraph.Inline!.Descendants<EmphasisInline>()
                     .Any(e => e.DelimiterChar == '*' && e.DelimiterCount == expectedDelimiters));
    }

    [Theory]
    [InlineData("bold")]
    [InlineData("bold ")]
    [InlineData(" bold")]
    [InlineData(" bold ")]
    [InlineData("part A*B")]
    [InlineData("file_name")]
    [InlineData("100% done")]
    public void StrongSpan_SurvivesTheRoundTrip(string literal)
    {
        var (text, wasEmphasised) = RoundTripEmphasis(MdNames.Strong, literal);

        // Both halves matter. The words must survive unchanged, INCLUDING the spaces the
        // author put inside the selection; and the span must still be bold, rather than
        // degrading to a paragraph with four literal asterisks in it.
        text.Should().Be("before " + literal + "after");
        wasEmphasised.Should().BeTrue();
    }

    [Theory]
    [InlineData("italic")]
    [InlineData("italic ")]
    [InlineData(" italic")]
    [InlineData(" italic ")]
    public void EmSpan_SurvivesTheRoundTrip(string literal)
    {
        var (text, wasEmphasised) = RoundTripEmphasis(MdNames.Em, literal);

        text.Should().Be("before " + literal + "after");
        wasEmphasised.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://example.com/spec")]
    // Already percent-encoded destinations are the common case in the target corpora, and
    // re-encoding '%' silently rewrote them into links that resolve to nothing.
    [InlineData("https://example.com/topics/C%23")]
    [InlineData("https://sharepoint.example.com/sites/My%20Docs/Handbook.docx")]
    // Characters that would end the destination early must be encoded, not passed through.
    [InlineData("img/my report/fig (1).png")]
    [InlineData("https://example.com/search?q=a&b=c#frag")]
    public void LinkDestination_SurvivesTheRoundTrip(string href)
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Para,
                    new XElement(MdNames.Link, new XAttribute("href", href),
                        new XElement(MdNames.Text, "the spec")))));

        var parsed = Markdig.Markdown.Parse(MarkdownSerializer.Serialize(mdXml), Pipeline);

        // One link, not a paragraph that trailed off into text because the destination
        // ended early on a space or a bracket.
        var link = parsed.Descendants<LinkInline>().Single();
        string.Concat(link.Descendants<LiteralInline>().Select(l => l.ToString())).Should().Be("the spec");

        // Compared after decoding, because encoding a delimiter is a legitimate change to
        // the spelling of a URL while changing what it addresses is not. "C%23" read back
        // as "C%2523" decodes to "C%23", not "C#", and fails here.
        Uri.UnescapeDataString(link.Url!).Should().Be(Uri.UnescapeDataString(href));
    }

    [Fact]
    public void CodeSpanContent_SurvivesUnescaped()
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Para,
                    new XElement(MdNames.Code, new XElement(MdNames.Text, "a*b_c")))));

        var markdown = MarkdownSerializer.Serialize(mdXml);
        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);

        // The reader must see a*b_c, with no backslashes introduced.
        parsed.Descendants<CodeInline>().Single().Content.Should().Be("a*b_c");
    }

    [Fact]
    public void HeadingLevel_SurvivesTheRoundTrip()
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Heading, new XAttribute("level", 3),
                    new XElement(MdNames.Text, "Findings"))));

        var parsed = Markdig.Markdown.Parse(MarkdownSerializer.Serialize(mdXml), Pipeline);

        var heading = parsed.Descendants<HeadingBlock>().Single();
        heading.Level.Should().Be(3);
    }

    [Fact]
    public void TableStructure_SurvivesTheRoundTrip()
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Table,
                    new XElement(MdNames.Row, new XAttribute("header", "true"),
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Item")),
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Status"))),
                    new XElement(MdNames.Row,
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Vent")),
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Pass"))))));

        var parsed = Markdig.Markdown.Parse(MarkdownSerializer.Serialize(mdXml), Pipeline);

        parsed.Descendants<Markdig.Extensions.Tables.Table>().Should().ContainSingle();
    }
}
