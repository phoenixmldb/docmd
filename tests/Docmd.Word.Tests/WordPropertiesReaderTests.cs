namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class WordPropertiesReaderTests
{
    private static XDocument BuildComposite(string fixture)
    {
        using var package = OpcPackage.Open(FixtureZip.FromDirectory(FixtureZip.FixtureRoot(fixture)));
        return WordCompositeBuilder.Build(package);
    }

    [Fact]
    public void Read_ExtractsCorePropertiesFromTheComposite()
    {
        var composite = BuildComposite("composite-basic");

        var properties = WordPropertiesReader.Read(composite, "report.docx", "9f2c1a");

        properties.Title.Should().Be("Q3 Safety Review");
        properties.Author.Should().Be("A. Whitfield");
        properties.Created.Should().Be("2026-04-11T09:14:00Z");
        properties.Source.Should().Be("report.docx");
        properties.Sha256.Should().Be("9f2c1a");
    }

    [Fact]
    public void Read_YieldsAllNullOptionalFieldsWhenPropertiesAreAbsent()
    {
        // The fixture's docmd:properties carries no docmd:core / docmd:app children at
        // all -- neither part existed in the source package. Source and Sha256 are
        // docmd's own additions, not read from the composite, so they must survive.
        var composite = new XDocument(
            new XElement(WordNames.Docmd + "package",
                new XAttribute(XNamespace.Xmlns + "docmd", WordNames.Docmd.NamespaceName),
                new XElement(WordNames.Docmd + "properties")));

        var properties = WordPropertiesReader.Read(composite, "report.docx", "9f2c1a");

        properties.Title.Should().BeNull();
        properties.Author.Should().BeNull();
        properties.Created.Should().BeNull();
        properties.Modified.Should().BeNull();
        properties.Revision.Should().BeNull();
        properties.Company.Should().BeNull();
        properties.Source.Should().Be("report.docx");
        properties.Sha256.Should().Be("9f2c1a");
    }
}
