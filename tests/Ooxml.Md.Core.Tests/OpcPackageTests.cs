namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class OpcPackageTests
{
    private static OpcPackage OpenMinimal() =>
        OpcPackage.Open(FixtureZip.FromDirectory(FixtureZip.FixtureRoot("minimal")));

    [Fact]
    public void Fixture_IsStaged()
    {
        // Guards the build wiring. Without this every test below could pass vacuously.
        Directory.Exists(FixtureZip.FixtureRoot("minimal")).Should().BeTrue();
    }

    [Fact]
    public void ReadXmlPart_ReturnsTheParsedPart()
    {
        using var package = OpenMinimal();

        var document = package.ReadXmlPart("word/document.xml");

        document.Root!.Name.LocalName.Should().Be("document");
    }

    [Fact]
    public void ContainsPart_IsFalseForAnAbsentPart()
    {
        using var package = OpenMinimal();

        package.ContainsPart("word/numbering.xml").Should().BeFalse();
    }

    [Fact]
    public void ReadXmlPart_ThrowsForAnAbsentPart()
    {
        using var package = OpenMinimal();

        var act = () => package.ReadXmlPart("word/numbering.xml");

        act.Should().Throw<OpcFormatException>().WithMessage("*word/numbering.xml*");
    }

    [Fact]
    public void ResolveTarget_ResolvesRelativeToTheSourcePartDirectory()
    {
        using var package = OpenMinimal();

        // rId7 targets "media/image1.png" from within word/document.xml, so the part is
        // word/media/image1.png -- NOT media/image1.png. Getting this wrong produces
        // missing images that look like a stylesheet bug much later.
        package.ResolveTarget("word/document.xml", "rId7").Should().Be("word/media/image1.png");
    }

    [Fact]
    public void ResolveTarget_ReturnsExternalTargetsUnchanged()
    {
        using var package = OpenMinimal();

        package.ResolveTarget("word/document.xml", "rId8").Should().Be("https://example.com/spec");
    }

    [Fact]
    public void RelationshipsFor_ReportsExternalTargets()
    {
        using var package = OpenMinimal();

        var relationships = package.RelationshipsFor("word/document.xml");

        relationships.Should().ContainSingle(r => r.Id == "rId8")
                     .Which.IsExternal.Should().BeTrue();
    }

    [Fact]
    public void RelationshipsFor_IsEmptyWhenThePartHasNoRelsFile()
    {
        using var package = OpenMinimal();

        // Absence of a .rels sibling is normal and must not throw.
        package.RelationshipsFor("word/styles.xml").Should().BeEmpty();
    }

    [Fact]
    public void RelationshipsFor_ResolvesThePackageLevelRelsForTheEmptyPartName()
    {
        using var package = OpenMinimal();

        // Package-level relationships (the ones that locate the main document part) live
        // at _rels/.rels, addressed by the empty part name -- not by any part called
        // "_rels/.rels" itself. Task 3 depends on this to find word/document.xml.
        var relationships = package.RelationshipsFor("");

        relationships.Should().ContainSingle(r => r.Id == "rId1")
                     .Which.Target.Should().Be("word/document.xml");
    }

    [Fact]
    public void ResolveTarget_ResolvesRelativeToTheEmptySourceDirectory()
    {
        using var package = OpenMinimal();

        // Resolving against the package root (an empty source directory) must return the
        // target unchanged, not prefixed with a stray "/" or "./".
        package.ResolveTarget("", "rId1").Should().Be("word/document.xml");
    }

    [Fact]
    public void Open_ThrowsForSomethingThatIsNotAZip()
    {
        using var notAZip = new MemoryStream("this is not a zip"u8.ToArray());

        var act = () => OpcPackage.Open(notAZip);

        act.Should().Throw<OpcFormatException>();
    }
}
