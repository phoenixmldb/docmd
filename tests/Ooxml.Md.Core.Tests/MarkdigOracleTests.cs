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
    public void ParagraphText_SurvivesTheRoundTrip(string literal)
    {
        // If this fails, the serialiser emitted something that a real Markdown parser
        // read as markup rather than as the document's words.
        RoundTripParagraphText(literal).Should().Be(literal);
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
