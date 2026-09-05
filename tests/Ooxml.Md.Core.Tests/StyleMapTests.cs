namespace Ooxml.Md.Core.Tests;

using System.Xml.Linq;
using FluentAssertions;
using Ooxml.Md.Core.StyleMapping;
using Xunit;

/// <summary>
/// A style map is hand-written configuration, so every malformed shape has to fail loudly with
/// the offending key named. A map that silently does nothing is indistinguishable from not
/// passing one, which is the worst possible outcome for a file someone wrote by hand.
/// </summary>
public sealed class StyleMapTests
{
    [Fact]
    public void Parse_ReadsTheSpecsOwnExample()
    {
        var map = StyleMap.Parse("""
            CautionNote:   { as: blockquote, prefix: "Caution: " }
            ProcedureStep: { as: ordered-list-item }
            PartNumber:    { as: inline-code }
            CorpTitle:     { as: heading, level: 1 }
            """);

        map.Rules.Should().HaveCount(4);
        map.Rules.Should().ContainSingle(r => r.Key == "CautionNote")
           .Which.Should().BeEquivalentTo(new StyleRule("CautionNote", "blockquote", null, "Caution: "));
        map.Rules.Should().ContainSingle(r => r.Key == "CorpTitle")
           .Which.Level.Should().Be(1);
    }

    [Fact]
    public void Parse_DefaultsAHeadingWithNoLevelToOne()
        => StyleMap.Parse("T: { as: heading }").Rules[0].Level.Should().Be(1);

    [Fact]
    public void Parse_TreatsAnEmptyDocumentAsAnEmptyMap()
        => StyleMap.Parse("").Rules.Should().BeEmpty();

    [Theory]
    [InlineData("T: { as: bogus }", "bogus")]
    [InlineData("T: { }", "as")]
    [InlineData("T: { as: heading, level: 9 }", "level")]
    [InlineData("T: { as: heading, colour: red }", "colour")]
    [InlineData("T: { as: blockquote, level: 2 }", "level")]
    [InlineData("T: just-a-string", "mapping")]
    [InlineData("""T: { as: ordered-list-item, prefix: "Step: " }""", "prefix")]
    [InlineData("""T: { as: inline-code, prefix: "x" }""", "prefix")]
    public void Parse_RejectsMalformedRulesAndSaysWhy(string yaml, string expectedInMessage)
    {
        var act = () => StyleMap.Parse(yaml);

        act.Should().Throw<StyleMapException>()
           .WithMessage("*T*", "the message must name the offending key")
           .And.Message.Should().Contain(expectedInMessage);
    }

    [Fact]
    public void Parse_AcceptsAPrefixOnEveryKindThatCanEmitOne()
        // The complement of the rejection above -- otherwise a narrowed PrefixableKinds would
        // pass the rejection tests while quietly breaking configurations that used to work.
        => StyleMap.PrefixableKinds.Should().AllSatisfy(kind =>
            StyleMap.Parse($$"""T: { as: {{kind}}, prefix: "P: " }""")
                .Rules[0].Prefix.Should().Be("P: "));

    [Fact]
    public void Parse_RejectsSomethingThatIsNotAMappingAtAll()
        => FluentActions.Invoking(() => StyleMap.Parse("- a\n- b"))
            .Should().Throw<StyleMapException>().WithMessage("*mapping of style name*");

    [Fact]
    public void Parse_RejectsInvalidYaml()
        => FluentActions.Invoking(() => StyleMap.Parse("T: { as: heading"))
            .Should().Throw<StyleMapException>().WithMessage("*not valid YAML*");

    [Fact]
    public void ToXml_RendersWhatTheStylesheetLooksUp()
    {
        XNamespace docmd = "https://phoenixml.dev/docmd";

        var xml = StyleMap.Parse("""
            CautionNote: { as: blockquote, prefix: "Caution: " }
            CorpTitle:   { as: heading, level: 2 }
            """).ToXml(docmd);

        xml.Name.Should().Be(docmd + "style-map");
        var caution = xml.Elements().Single(e => (string?)e.Attribute("key") == "CautionNote");
        caution.Attribute("as")!.Value.Should().Be("blockquote");
        caution.Attribute("prefix")!.Value.Should().Be("Caution: ");
        caution.Attribute("level").Should().BeNull("only headings carry a level");

        xml.Elements().Single(e => (string?)e.Attribute("key") == "CorpTitle")
           .Attribute("level")!.Value.Should().Be("2");
    }

    [Fact]
    public void EmptyMap_RendersAnElementRatherThanNothing()
    {
        XNamespace docmd = "https://phoenixml.dev/docmd";

        // Always present, so the stylesheet never has to test for existence before selecting
        // into it -- the same reason docmd:styles and docmd:numbering are always emitted.
        StyleMap.Empty.ToXml(docmd).Name.Should().Be(docmd + "style-map");
        StyleMap.Empty.ToXml(docmd).Elements().Should().BeEmpty();
    }
}
