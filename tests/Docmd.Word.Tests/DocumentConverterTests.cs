namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using Docmd.Word;
using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class DocumentConverterTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    /// <summary>Materialises a fixture directory as a real .docx on disk.</summary>
    private string StageDocx(string fixture, string name)
    {
        var path = Path.Combine(_workspace, name);
        using var zip = FixtureZip.FromDirectory(FixtureZip.FixtureRoot(fixture));
        using var file = File.Create(path);
        zip.CopyTo(file);
        return path;
    }

    private ConversionOptions Options => new() { OutputDirectory = _workspace };

    [Fact]
    public async Task Convert_ProducesFrontmatterFollowedByBody()
    {
        var result = await DocumentConverter.ConvertAsync(
            StageDocx("composite-basic", "report.docx"), Options, TestContext.Current.CancellationToken);

        result.Markdown.Should().StartWith("---\n");
        result.Markdown.Should().Contain("title: Q3 Safety Review");
        result.Markdown.Should().Contain("Hello");
    }

    [Fact]
    public async Task Convert_ComputesTheSourceHash()
    {
        var result = await DocumentConverter.ConvertAsync(
            StageDocx("composite-basic", "report.docx"), Options, TestContext.Current.CancellationToken);

        result.Properties.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        result.Properties.Source.Should().Be("report.docx");
    }

    [Fact]
    public async Task Convert_OmitsFrontmatterWhenAsked()
    {
        var options = Options with { IncludeFrontmatter = false };

        var result = await DocumentConverter.ConvertAsync(
            StageDocx("composite-basic", "report.docx"), options, TestContext.Current.CancellationToken);

        result.Markdown.Should().NotStartWith("---");
    }

    [Fact]
    public async Task Write_PutsTheMarkdownBesideTheOutputDirectory()
    {
        await DocumentConverter.WriteAsync(
            StageDocx("composite-basic", "report.docx"), Options, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_workspace, "report.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Convert_RejectsSomethingThatIsNotAnOoxmlPackage()
    {
        var path = Path.Combine(_workspace, "legacy.doc");
        await File.WriteAllTextAsync(path, "not a zip", TestContext.Current.CancellationToken);

        var act = async () => await DocumentConverter.ConvertAsync(path, Options, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OpcFormatException>();
    }

    [Fact]
    public async Task Convert_WritesAssetsBeforeSerialisingTheMarkdown()
    {
        // Regression test for the phase ordering: 5a (write assets, collect their URIs)
        // must run before 5b (serialise). If the two were swapped, RewriteAsync would
        // still eventually run and the file below would still land on disk, but the
        // Markdown string returned here would have been computed from the md-XML tree
        // *before* AssetRewriter replaced the image's @src -- so it would still read the
        // raw OPC part name ("word/media/image1.png") instead of the sink's URI.
        var result = await DocumentConverter.ConvertAsync(
            StageDocx("with-image", "report.docx"), Options, TestContext.Current.CancellationToken);

        result.Markdown.Should().Contain("img/report/image1.png");
        result.Markdown.Should().NotContain("word/media/image1.png");
        File.Exists(Path.Combine(_workspace, "img", "report", "image1.png")).Should().BeTrue();
    }

    [Fact]
    public async Task Convert_OmitsImagesWhenAsked()
    {
        var options = Options with { IncludeImages = false };

        var result = await DocumentConverter.ConvertAsync(
            StageDocx("with-image", "report.docx"), options, TestContext.Current.CancellationToken);

        result.Markdown.Should().NotContain("![");
        Directory.Exists(Path.Combine(_workspace, "img")).Should().BeFalse();
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
