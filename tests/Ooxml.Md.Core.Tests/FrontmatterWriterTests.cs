namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core.Frontmatter;
using Xunit;
using YamlDotNet.Serialization;

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

    // Adversarial review found three silent-corruption classes in Quote() with no
    // dedicated coverage: a bare null-ish token reads back as YAML null (dropping the
    // field's actual content), a bare hex or sexagesimal token reads back as a number
    // (not caught by long/double.TryParse), and an unescaped internal line break spans
    // a scalar across physical lines with no key on the continuation -- corrupting every
    // field after it, not just the one carrying the break. Each case below asserts the
    // full emitted block, not a substring, so a regression can't hide behind a partial
    // match.
    [Theory]
    [InlineData("Off", "'Off'")] // YAML 1.1 boolean
    [InlineData("Yes", "'Yes'")] // YAML 1.1 boolean
    [InlineData("42", "'42'")] // decimal integer
    [InlineData("3.14", "'3.14'")] // decimal float
    [InlineData("0x1F", "'0x1F'")] // YAML 1.1 hex integer -- resolves to 31 unquoted
    [InlineData("1:30", "'1:30'")] // YAML 1.1 sexagesimal integer -- resolves to 90 unquoted
    [InlineData("null", "'null'")] // YAML null token -- resolves to null unquoted
    public void Write_QuotesValuesAYamlParserWouldMisreadAsSomethingOtherThanAString(string title, string expected)
    {
        var yaml = FrontmatterWriter.Write(Minimal with { Title = title });

        yaml.Should().Be($"---\ntitle: {expected}\nsource: report.docx\nsha256: 9f2c1a\n---\n\n");
    }

    /// <summary>
    /// Writes the frontmatter, then reads it back with a real YAML parser and returns the
    /// mapping it saw.
    /// </summary>
    /// <remarks>
    /// The same argument as the Markdig oracle for the Markdown serialiser (spec §13.2):
    /// asserting the exact string this writer emits proves it emits what we intended, not
    /// that a parser agrees. Both failure modes below are invisible to a string
    /// comparison written by the same person who wrote the quoting rule. YamlDotNet is
    /// already a dependency of the production project, so this costs nothing.
    /// </remarks>
    private static Dictionary<string, string> ReadBackWithARealYamlParser(string title)
    {
        var yaml = FrontmatterWriter.Write(Minimal with { Title = title });
        var body = yaml["---\n".Length..yaml.IndexOf("\n---\n", StringComparison.Ordinal)];

        return new DeserializerBuilder().Build().Deserialize<Dictionary<string, string>>(body);
    }

    [Theory]
    // " #" starts a comment: this one is not a parse error but a SILENT truncation --
    // "Rev # 3" reads back as "Rev", keeping its key and losing its value.
    [InlineData("Rev # 3")]
    [InlineData("Rev #")]
    // Everything below begins with a YAML indicator character. Unquoted, these are syntax
    // errors, and a syntax error inside the frontmatter loses the ENTIRE block -- every
    // field of it -- not just the field that carries the offending character.
    [InlineData("\"Best Practice\" Guide")]
    [InlineData("'Draft' copy")]
    [InlineData("!Urgent review")]
    [InlineData("?Unresolved")]
    [InlineData("|Pipeline overview")]
    [InlineData(">Forwarded: notes")]
    [InlineData("@mentions and handles")]
    [InlineData("`literal` naming")]
    [InlineData("%complete")]
    [InlineData(",leading comma")]
    [InlineData("]stray bracket")]
    [InlineData("}stray brace")]
    // Already handled before this pass; included so the oracle covers the whole rule
    // rather than only the part that was broken.
    [InlineData("#hashtag")]
    [InlineData("&anchor")]
    [InlineData("*alias")]
    [InlineData("[flow start")]
    [InlineData("{flow start")]
    [InlineData("-dash start")]
    [InlineData(":colon start")]
    [InlineData("Report: Phase 2")]
    [InlineData("Trailing space ")]
    public void Write_SurvivesARoundTripThroughARealYamlParser(string title)
        => ReadBackWithARealYamlParser(title)["title"].Should().Be(title);

    [Fact]
    public void Write_KeepsAHashThatIsNotACommentUnquoted()
        // "C#" carries no space before the '#', so it is not a comment and needs no
        // quoting. Over-quoting is not free: it is noise on every field it touches.
        => FrontmatterWriter.Write(Minimal with { Title = "C# Coding Standard" })
            .Should().Contain("title: C# Coding Standard");

    [Fact]
    public void Write_NormalizesAnInternalLineFeedToASpaceInsteadOfBreakingTheBlock()
    {
        // An unescaped "\n" here would make the "title:" line span two physical lines,
        // with the second carrying no key -- corrupting the rest of the frontmatter, not
        // just this field.
        var yaml = FrontmatterWriter.Write(Minimal with { Title = "Line one\nLine two" });

        yaml.Should().Be("---\ntitle: Line one Line two\nsource: report.docx\nsha256: 9f2c1a\n---\n\n");
    }

    [Fact]
    public void Write_NormalizesAnInternalCarriageReturnLineFeedToASingleSpace()
    {
        // "\r\n" must collapse to one space, not two -- otherwise Windows-authored titles
        // would get double-spaced relative to Unix-authored ones for no reason.
        var yaml = FrontmatterWriter.Write(Minimal with { Title = "Line one\r\nLine two" });

        yaml.Should().Be("---\ntitle: Line one Line two\nsource: report.docx\nsha256: 9f2c1a\n---\n\n");
    }

    [Fact]
    public void Write_NormalizesAnInternalTabToASpace()
    {
        var yaml = FrontmatterWriter.Write(Minimal with { Title = "Line one\tLine two" });

        yaml.Should().Be("---\ntitle: Line one Line two\nsource: report.docx\nsha256: 9f2c1a\n---\n\n");
    }
}
