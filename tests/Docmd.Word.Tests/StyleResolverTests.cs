namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Xunit;

public sealed class StyleResolverTests
{
    private static StyleResolver Resolve(string stylesXml) =>
        StyleResolver.FromComposite(XElement.Parse(stylesXml));

    private const string Ns = @"xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""";

    [Fact]
    public void EffectiveOutlineLevel_ReadsAnExplicitLevel()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="Heading1"><w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style>
            </w:styles>
            """);

        resolver.EffectiveOutlineLevel("Heading1").Should().Be(0);
    }

    [Fact]
    public void EffectiveOutlineLevel_WalksTheBasedOnChain()
    {
        // A corporate style named nothing like a heading, based on one that is. Name
        // matching would never find this; the chain does.
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="Heading2"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
              <w:style w:styleId="ProcedureTitle"><w:basedOn w:val="Heading2"/></w:style>
            </w:styles>
            """);

        resolver.EffectiveOutlineLevel("ProcedureTitle").Should().Be(1);
    }

    [Fact]
    public void EffectiveOutlineLevel_SurvivesACyclicBasedOnChain()
    {
        // Malformed documents exist. A naive walk here is an infinite loop that hangs
        // the converter on a customer's corpus with no diagnostic.
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="A"><w:basedOn w:val="B"/></w:style>
              <w:style w:styleId="B"><w:basedOn w:val="A"/></w:style>
            </w:styles>
            """);

        resolver.EffectiveOutlineLevel("A").Should().BeNull();
    }

    [Fact]
    public void LookupOutlineLevel_ReportsWhetherTheLevelCameFromAnAncestor()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="Heading2"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
              <w:style w:styleId="ProcedureTitle"><w:basedOn w:val="Heading2"/></w:style>
            </w:styles>
            """);

        resolver.LookupOutlineLevel("Heading2")!.Value.FromAncestor.Should().BeFalse();
        resolver.LookupOutlineLevel("ProcedureTitle")!.Value.FromAncestor.Should().BeTrue();
    }

    [Fact]
    public void EffectiveOutlineLevel_IsNullForAnUnknownStyle()
        => Resolve($"<w:styles {Ns}/>").EffectiveOutlineLevel("Nope").Should().BeNull();

    [Fact]
    public void DefaultFontHalfPoints_ReadsDocDefaults()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="20"/></w:rPr></w:rPrDefault></w:docDefaults>
            </w:styles>
            """);

        resolver.DefaultFontHalfPoints.Should().Be(20);
    }

    [Fact]
    public void DefaultFontHalfPoints_FallsBackToWordsDefault()
        => Resolve($"<w:styles {Ns}/>").DefaultFontHalfPoints.Should().Be(22);

    [Fact]
    public void NameOf_ReturnsTheLocalisedStyleName()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="berschrift1"><w:name w:val="heading 1"/></w:style>
            </w:styles>
            """);

        // styleId and name differ, and the name is what carries meaning across locales.
        resolver.NameOf("berschrift1").Should().Be("heading 1");
    }
}
