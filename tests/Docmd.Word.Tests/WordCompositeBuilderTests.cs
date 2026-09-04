namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class WordCompositeBuilderTests
{
    private static XDocument BuildComposite(string fixture)
    {
        using var package = OpcPackage.Open(FixtureZip.FromDirectory(FixtureZip.FixtureRoot(fixture)));
        return WordCompositeBuilder.Build(package);
    }

    [Fact]
    public void Build_PlacesTheBodyUnderTheCompositeRoot()
    {
        var composite = BuildComposite("composite-basic");

        composite.Root!.Name.Should().Be(WordNames.Docmd + "package");
        // docmd:body wraps the original w:body element rather than splicing its children
        // in directly -- the stylesheet's entry point matches "docmd:body/w:body".
        composite.Root.Element(WordNames.Docmd + "body")!
                 .Element(WordNames.W + "body")!
                 .Element(WordNames.W + "p").Should().NotBeNull();
    }

    [Fact]
    public void Build_IncludesStylesAndTolerAtesAbsentNumbering()
    {
        var composite = BuildComposite("composite-basic");

        // docmd:styles wraps the original w:styles element for the same reason docmd:body
        // wraps w:body -- the stylesheet selects into "docmd:styles/w:styles".
        composite.Root!.Element(WordNames.Docmd + "styles")!
                 .Element(WordNames.W + "styles")!
                 .Element(WordNames.W + "style").Should().NotBeNull();
        // numbering.xml is absent from this fixture; the element must still exist and be
        // empty, so the stylesheet never has to test for its presence.
        composite.Root.Element(WordNames.Docmd + "numbering")!.Elements().Should().BeEmpty();
    }

    [Fact]
    public void Build_FlattensRelationshipsWithResolvedTargets()
    {
        var composite = BuildComposite("composite-basic");

        var image = composite.Root!.Element(WordNames.Docmd + "relationships")!
            .Elements(WordNames.Docmd + "relationship")
            .Single(e => (string?)e.Attribute("id") == "rId7");

        // Already resolved -- the stylesheet must never do path arithmetic.
        image.Attribute("target")!.Value.Should().Be("word/media/image1.png");
        image.Attribute("external")!.Value.Should().Be("false");
    }

    [Fact]
    public void Build_IncludesCorePropertiesWhenPresent()
    {
        var composite = BuildComposite("composite-basic");

        composite.Root!.Element(WordNames.Docmd + "properties")!
                 .Element(WordNames.Docmd + "core").Should().NotBeNull();
    }

    [Fact]
    public void Build_ThrowsWhenThereIsNoMainDocumentRelationship()
    {
        using var empty = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(empty, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("placeholder.txt");
        }

        empty.Position = 0;
        using var package = OpcPackage.Open(empty);

        var act = () => WordCompositeBuilder.Build(package);

        act.Should().Throw<OpcFormatException>().WithMessage("*main document*");
    }
}
