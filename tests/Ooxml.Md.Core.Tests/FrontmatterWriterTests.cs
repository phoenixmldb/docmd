namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core.Frontmatter;
using Xunit;

public sealed class FrontmatterWriterTests
{
    private static DocumentProperties Minimal => new() { Source = "report.docx", Sha256 = "9f2c1a" };

    [Fact]
    public void Write_EmitsFencesAndTheRequiredFields()
        => FrontmatterWriter.Write(Minimal)
            .Should().Be("---\nsource: report.docx\nsha256: 9f2c1a\n---\n\n");

    [Fact]
    public void Write_OmitsAbsentFieldsEntirely()
    {
        // "author:" with no value is noise in an index and a filterable field that
        // matches nothing.
        var yaml = FrontmatterWriter.Write(Minimal);

        yaml.Should().NotContain("author");
        yaml.Should().NotContain("title");
    }

    [Fact]
    public void Write_IncludesEveryFieldWhenPresent()
    {
        var yaml = FrontmatterWriter.Write(new DocumentProperties
        {
            Title = "Q3 Safety Review",
            Author = "A. Whitfield",
            Created = "2026-04-11",
            Modified = "2026-04-18",
            Revision = "7",
            Company = "Endpoint Systems",
            Source = "report.docx",
            Sha256 = "9f2c1a",
        });

        yaml.Should().Contain("title: Q3 Safety Review");
        yaml.Should().Contain("author: A. Whitfield");
        yaml.Should().Contain("created: '2026-04-11'");
        yaml.Should().Contain("company: Endpoint Systems");
    }

    [Fact]
    public void Write_QuotesValuesThatWouldOtherwiseChangeMeaning()
    {
        // A title containing a colon splits into a nested mapping unless quoted, silently
        // corrupting the document's metadata.
        var yaml = FrontmatterWriter.Write(Minimal with { Title = "Report: Phase 2" });

        yaml.Should().Contain("'Report: Phase 2'");
    }

    [Fact]
    public void Write_ContainsNoTimestamp()
    {
        // Determinism (spec §5). A converted-at field changes every file's hash on every
        // run and forces a whole corpus to re-embed for no content change.
        var yaml = FrontmatterWriter.Write(Minimal);

        yaml.Should().NotContain("converted");
        FrontmatterWriter.Write(Minimal).Should().Be(yaml);
    }
}
