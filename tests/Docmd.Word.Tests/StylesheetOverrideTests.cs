namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

/// <summary>
/// The transform is a stylesheet rather than compiled code so that it can be replaced. These
/// pin that it actually can be — printing the built-in one, editing it, and running the result.
/// </summary>
public sealed class StylesheetOverrideTests : IDisposable
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private static XDocument Composite(string bodyInner)
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles/></docmd:styles>
              <docmd:numbering><w:numbering/></docmd:numbering>
              <docmd:relationships/><docmd:properties/>
            </docmd:package>
            """);

        HeadingAnnotator.Annotate(composite);
        return composite;
    }

    private const string Heading =
        """<w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Scope</w:t></w:r></w:p>""";

    [Fact]
    public void PrintedStylesheet_IsTheOneThatActuallyRuns()
    {
        // The whole point of --print-stylesheet is that you start from the real thing at the
        // version you have, rather than reconstructing it from the repository.
        var printed = StylesheetLoader.Read("markdown.xslt");

        printed.Should().StartWith("<?xml");
        var act = () => XDocument.Parse(printed);
        act.Should().NotThrow("a stylesheet you cannot parse is not a starting point");
        printed.Should().Contain("docmd:visible-text", "the real templates must be present, not a stub");
    }

    [Fact]
    public async Task UserStylesheet_ReplacesTheBuiltInOne()
    {
        var printed = StylesheetLoader.Read("markdown.xslt");
        var edited = printed.Replace(
            """<md:text><xsl:value-of select="docmd:visible-text(.)"/></md:text>""",
            """<md:text><xsl:text>SECTION </xsl:text><xsl:value-of select="docmd:visible-text(.)"/></md:text>""",
            StringComparison.Ordinal);
        edited.Should().NotBe(printed, "the edit anchor must still exist in the shipped stylesheet");

        var path = Path.Combine(_workspace, "mine.xslt");
        await File.WriteAllTextAsync(path, edited, TestContext.Current.CancellationToken);

        var mdXml = await MarkdownTransform.RunAsync(
            Composite(Heading), path, TestContext.Current.CancellationToken);

        MarkdownSerializer.Serialize(mdXml).Should().Be("# SECTION Scope\n");
    }

    [Fact]
    public async Task NoStylesheetPath_UsesTheBuiltInOne()
    {
        var mdXml = await MarkdownTransform.RunAsync(
            Composite(Heading), stylesheetPath: null, TestContext.Current.CancellationToken);

        MarkdownSerializer.Serialize(mdXml).Should().Be("# Scope\n");
    }

    [Fact]
    public async Task MissingStylesheet_ThrowsSomethingTheCliMapsToBadInput()
    {
        // FileNotFoundException is an IOException, which Program already maps to exit 2 --
        // a usage error rather than an internal one.
        var act = async () => await MarkdownTransform.RunAsync(
            Composite(Heading),
            Path.Combine(_workspace, "absent.xslt"),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which
            .Should().BeAssignableTo<IOException>();
    }

    [Fact]
    public async Task UserStylesheet_ResolvesItsOwnRelativeImports()
    {
        // The stylesheet's own directory is the base URI, so a user splitting their overrides
        // across files gets relative xsl:import working from where those files live rather
        // than from the process's working directory -- which is wherever docmd was invoked.
        var overrides = Path.Combine(_workspace, "overrides.xslt");
        await File.WriteAllTextAsync(overrides, """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:md="https://phoenixml.dev/docmd/md">
              <xsl:template name="marker"><md:text>IMPORTED </md:text></xsl:template>
            </xsl:stylesheet>
            """, TestContext.Current.CancellationToken);

        var printed = StylesheetLoader.Read("markdown.xslt");
        var edited = printed
            .Replace("""<xsl:output method="xml" indent="no"/>""",
                     """<xsl:output method="xml" indent="no"/><xsl:import href="overrides.xslt"/>""",
                     StringComparison.Ordinal)
            .Replace("""<md:text><xsl:value-of select="docmd:visible-text(.)"/></md:text>""",
                     """<xsl:call-template name="marker"/><md:text><xsl:value-of select="docmd:visible-text(.)"/></md:text>""",
                     StringComparison.Ordinal);

        var main = Path.Combine(_workspace, "main.xslt");
        await File.WriteAllTextAsync(main, edited, TestContext.Current.CancellationToken);

        var mdXml = await MarkdownTransform.RunAsync(
            Composite(Heading), main, TestContext.Current.CancellationToken);

        MarkdownSerializer.Serialize(mdXml).Should().Be("# IMPORTED Scope\n");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
