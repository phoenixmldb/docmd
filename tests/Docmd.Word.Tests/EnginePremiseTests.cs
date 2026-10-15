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

    /// <summary>
    /// A stylesheet a caller supplies must not read the environment of the docmd process --
    /// a licence key, a token a build gave us -- and write it into the Markdown.
    /// <para>
    /// At run time the engine has always answered nothing. A shadow attribute (and use-when,
    /// and a static variable) is evaluated while the stylesheet compiles, by a second
    /// evaluator, and that one read the real environment until Xslt 2.8.0
    /// (GHSA-5697-c8qg-472f, high, which names every version up to and including 2.7.0).
    /// What a shadow attribute reads becomes the attribute, so the value lands in the
    /// result. A pin that slid back below 2.8.0 leaks here, in one line, instead of in a
    /// customer's output.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Engine_AnswersNothingForAnEnvironmentVariable_WhileCompilingAndAtRuntime()
    {
        // The name is written out again in the stylesheet below; XSLT has no way to ask for it.
        const string name = "DOCMD_PREMISE_NOT_FOR_A_STYLESHEET";
        const string secret = "a-value-no-stylesheet-may-read";
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/doc">
                <xsl:value-of _select="'[{environment-variable('DOCMD_PREMISE_NOT_FOR_A_STYLESHEET')}]'"/>
                <xsl:value-of select="concat(
                  '[', string-join(environment-variable('DOCMD_PREMISE_NOT_FOR_A_STYLESHEET')), ']',
                  '[', string-join(available-environment-variables()), ']')"/>
                <xsl:text use-when="available-environment-variables() = 'DOCMD_PREMISE_NOT_FOR_A_STYLESHEET'">[listed]</xsl:text>
              </xsl:template>
            </xsl:stylesheet>
            """;

        Environment.SetEnvironmentVariable(name, secret);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet);

            var result = await transformer.TransformAsync(
                """<doc/>""", TestContext.Current.CancellationToken);

            result.Should().NotContain(secret, "no stylesheet may read the environment of this process");
            result.Should().Be("[][][]");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
