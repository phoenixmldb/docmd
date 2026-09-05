namespace Docmd.Word.Tests;

using System.Globalization;
using System.Threading.Tasks;
using Docmd.Word;
using FluentAssertions;
using Xunit;

/// <summary>
/// Byte-identical output for the same input is a product guarantee (spec §5), not a
/// marketing claim, so it gets a test rather than a comment. Files land in git repos and
/// RAG stores that re-index on content change; one unstable byte flips a hash and
/// re-embeds a corpus whose text did not move.
/// </summary>
public sealed class DeterminismTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private static string Sample => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "sample.docx");

    private ConversionOptions Options => new() { OutputDirectory = _workspace };

    [Fact]
    public void Fixture_IsStaged()
        // Without this the tests below would pass vacuously if the copy wiring broke.
        => File.Exists(Sample).Should().BeTrue("the real Word fixture must reach the output directory");

    [Fact]
    public async Task Converting_Twice_ProducesIdenticalBytes()
    {
        var first = await DocumentConverter.ConvertAsync(Sample, Options, TestContext.Current.CancellationToken);
        var second = await DocumentConverter.ConvertAsync(Sample, Options, TestContext.Current.CancellationToken);

        second.Markdown.Should().Be(first.Markdown);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task Converting_UnderAnotherCulture_ProducesIdenticalBytes(string culture)
    {
        // tr-TR lowercases 'I' to dotless 'ı', which changes slugs; de-DE uses a comma
        // decimal separator. Either silently breaks the byte-identical guarantee.
        var baseline = await DocumentConverter.ConvertAsync(Sample, Options, TestContext.Current.CancellationToken);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var underCulture = await DocumentConverter.ConvertAsync(Sample, Options, TestContext.Current.CancellationToken);

            underCulture.Markdown.Should().Be(baseline.Markdown);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Output_ContainsNoTimestamp()
    {
        var result = await DocumentConverter.ConvertAsync(Sample, Options, TestContext.Current.CancellationToken);

        result.Markdown.Should().NotContain(
            DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture) + "-" +
            DateTime.UtcNow.Month.ToString("D2", CultureInfo.InvariantCulture) + "-" +
            DateTime.UtcNow.Day.ToString("D2", CultureInfo.InvariantCulture),
            "no conversion timestamp may appear in the output");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
