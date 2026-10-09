namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using Docmd.Cli;
using FluentAssertions;
using Xunit;

/// <summary>
/// Exercises the exit-code contract (spec §12) through the program's real entry point.
/// </summary>
public sealed class ProgramTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private static Task<int> RunAsync(params string[] args) => Program.Run(args);

    private static string Sample => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "sample.docx");

    [Fact]
    public async Task Run_ReturnsUsageErrorForAnInputFileThatDoesNotExist()
    {
        var missing = Path.Combine(_workspace, "absent.docx");

        (await RunAsync(missing, "-o", _workspace)).Should().Be(2);
    }

    [Fact]
    public async Task Run_PrintStylesheetSucceeds()
        // Exit 0 matters: this is meant to be redirected into a file
        // (docmd --print-stylesheet > mine.xslt), and a non-zero exit breaks that in a
        // shell running with `set -e`.
        => (await RunAsync("--print-stylesheet")).Should().Be(0);

    [Fact]
    public async Task Run_ReturnsUsageErrorForAStylesheetThatDoesNotExist()
        => (await RunAsync("report.docx", "--stylesheet", "/does/not/exist.xslt")).Should().Be(2);

    [Theory]
    [InlineData("audit")]
    [InlineData("register")]
    [InlineData("license")]
    public async Task Run_ReturnsUsageErrorForASubcommandThatIsNotImplemented(string subcommand)
        // Spec §12 reserves 1 for an unexpected internal error. Returning it for a
        // deliberately unimplemented subcommand leaves a CI script unable to tell a bug
        // from a command it should not have run.
        => (await RunAsync(subcommand)).Should().Be(2);

    [Fact]
    public async Task Run_ReturnsUsageErrorForAnEmptyInputPath()
        // This reached DocumentConverter, whose ArgumentException landed in the top-level
        // catch-all and exited 1.
        => (await RunAsync("")).Should().Be(2);

    [Fact]
    public async Task Run_ReturnsUsageErrorForAFileThatIsNotAnOoxmlPackage()
    {
        var path = Path.Combine(_workspace, "legacy.doc");
        await File.WriteAllTextAsync(path, "not a zip", TestContext.Current.CancellationToken);

        (await RunAsync(path, "-o", _workspace)).Should().Be(2);
    }

    [Fact]
    public async Task Run_ReturnsSuccessForHelpAndVersion()
    {
        (await RunAsync("--help")).Should().Be(0);
        (await RunAsync("--version")).Should().Be(0);
    }

    [Fact]
    public async Task Run_ReturnsUsageErrorWhenAStylesheetRunsPastTheRegexLimit()
    {
        // Exit 2 rather than 1 (issue 48): a stylesheet that will not finish is input, like a
        // file that is not a package, and a CI script has to be able to tell the two apart.
        // --regex-timeout 1 keeps this test near a second instead of the default ten.
        var stylesheet = Path.Combine(_workspace, "backtrack.xslt");
        await File.WriteAllTextAsync(stylesheet, """
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
            """, TestContext.Current.CancellationToken);

        (await RunAsync(Sample, "-o", _workspace, "--stylesheet", stylesheet, "--regex-timeout", "1"))
            .Should().Be(2);
    }

    [Fact]
    public async Task Run_ReturnsUsageErrorWhenAStylesheetReadsOutsideItsOwnDirectory()
    {
        var elsewhere = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(elsewhere, "outside.txt");
            await File.WriteAllTextAsync(target, "outside", TestContext.Current.CancellationToken);

            var stylesheet = Path.Combine(_workspace, "reads-outside.xslt");
            await File.WriteAllTextAsync(stylesheet, $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output method="xml" indent="no"/>
                  <xsl:template match="/">
                    <result><xsl:value-of select="unparsed-text('{{new Uri(target).AbsoluteUri}}')"/></result>
                  </xsl:template>
                </xsl:stylesheet>
                """, TestContext.Current.CancellationToken);

            (await RunAsync(Sample, "-o", _workspace, "--stylesheet", stylesheet)).Should().Be(2);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
