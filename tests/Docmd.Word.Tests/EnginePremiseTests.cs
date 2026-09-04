namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

/// <summary>
/// The whole product assumes PhoenixmlDb.Xslt can run a stylesheet that produces text.
/// This is a canary, not a formality: if a package bump breaks text output or parameter
/// passing, every golden-file test in the suite fails at once with a confusing diff, and
/// this test says why in one line.
/// </summary>
public sealed class EnginePremiseTests
{
    [Fact]
    public async Task Engine_TransformsToText_AndAcceptsAParameter()
    {
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:param name="greeting" select="'unset'"/>
              <xsl:template match="/doc">
                <xsl:value-of select="concat($greeting, ':', @name)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetParameter("greeting", "hello");

        var result = await transformer.TransformAsync(
            """<doc name="world"/>""", TestContext.Current.CancellationToken);

        result.Should().Be("hello:world");
    }
}
