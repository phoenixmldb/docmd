namespace Docmd.Word.Tests;

using System.Diagnostics;
using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using FluentAssertions;
using PhoenixmlDb.XQuery.Functions;
using Xunit;

/// <summary>
/// The limits docmd puts on a stylesheet it was handed. A stylesheet passed with
/// <c>--stylesheet</c> is code somebody else may have written, and the engine's defaults are
/// deliberately permissive: no regex time limit and no resource policy at all.
/// </summary>
/// <remarks>
/// Issue 48. Cancellation is not the lever here, and that is the whole point of these tests:
/// the built-in stylesheet returns to a template often enough that a token stops it, but a
/// transform whose entire running time sits inside one <c>matches()</c> call never looks at
/// the token. Measured on the engine before writing this: a catastrophically backtracking
/// pattern ran 91,227 ms to completion with a token cancelled at 1,000 ms, and stopped at
/// 2,013 ms when <c>RegexMatchTimeout</c> was set instead.
/// </remarks>
public sealed class StylesheetLimitsTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private static XDocument Input => XDocument.Parse("<doc/>");

    private async Task<string> WriteStylesheetAsync(string name, string xslt)
    {
        var path = Path.Combine(_workspace, name);
        await File.WriteAllTextAsync(path, xslt, TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task BacktrackingPattern_IsAbandonedRatherThanWaitedOn()
    {
        // The reproduction from issue 48, verbatim: 28 a's and a b, against a pattern that
        // cannot match it without exponential backtracking. Uncancelled, this transform runs
        // for 91,893 ms.
        var path = await WriteStylesheetAsync("backtrack.xslt", """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <xsl:variable name="subject" as="xs:string"
                    select="concat(string-join(for $i in 1 to 28 return 'a', ''), 'b')"/>
                <result matched="{matches($subject, '^(a+)+$')}"/>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var started = Stopwatch.StartNew();
        var act = async () => await MarkdownTransform.RunAsync(
            Input, path, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<XQueryException>()).Which
            .Message.Should().Contain("regular-expression", "the diagnostic has to name what ran long");
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "the limit is what stops this; without one the call takes 90 seconds");
    }

    [Fact]
    public void TheDefaultTimeout_IsWhatAConversionCarries()
    {
        // The flag exists because a legitimate stylesheet on a large document can spend real
        // time in a regex, so this is a default rather than a constant -- but it has to BE a
        // default, or --stylesheet is unbounded again for everyone who does not pass the flag.
        MarkdownTransform.DefaultRegexMatchTimeout.Should().BeGreaterThan(TimeSpan.Zero);
        new ConversionOptions { OutputDirectory = "." }.RegexMatchTimeout
            .Should().Be(MarkdownTransform.DefaultRegexMatchTimeout);
    }

    [Fact]
    public async Task UserStylesheet_ReadsAFileBesideItself()
    {
        // The legitimate case the resource policy must not break: a lookup table shipped
        // alongside the stylesheet that uses it.
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "lookup.txt"), "BESIDE", TestContext.Current.CancellationToken);

        var path = await WriteStylesheetAsync("reads-sibling.xslt", """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <result><xsl:value-of select="unparsed-text('lookup.txt')"/></result>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var mdXml = await MarkdownTransform.RunAsync(
            Input, path, TestContext.Current.CancellationToken);

        mdXml.Root!.Value.Should().Be("BESIDE");
    }

    [Fact]
    public async Task UserStylesheet_CannotReadOutsideItsOwnDirectory()
    {
        var elsewhere = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var outside = Path.Combine(elsewhere, "outside.txt");
            await File.WriteAllTextAsync(outside, "SECRET", TestContext.Current.CancellationToken);

            var path = await WriteStylesheetAsync("reads-outside.xslt", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output method="xml" indent="no"/>
                  <xsl:template match="/">
                    <result><xsl:value-of select="unparsed-text('{{new Uri(outside).AbsoluteUri}}')"/></result>
                  </xsl:template>
                </xsl:stylesheet>
                """);

            var act = async () => await MarkdownTransform.RunAsync(
                Input, path, TestContext.Current.CancellationToken);

            // What matters is that the read does not happen; the engine's own exception type
            // for a denial is its business, so this asserts the outcome, not the wrapper.
            (await act.Should().ThrowAsync<Exception>()).Which
                .ToString().Should().NotContain("SECRET", "a denied read must not return the content");
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
