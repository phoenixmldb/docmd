namespace Ooxml.Md.Core.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class AssetRewriterTests
{
    private static XDocument MdWithImage(string src) => new(
        new XElement(MdNames.Document,
            new XElement(MdNames.Para,
                new XElement(MdNames.Image, new XAttribute("src", src), new XAttribute("alt", "Fig")))));

    private static OpcPackage OpenMinimal()
        => OpcPackage.Open(FixtureZip.FromDirectory(FixtureZip.FixtureRoot("with-image")));

    [Fact]
    public async Task Rewrite_WritesTheAssetAndReplacesTheSource()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/image1.png");
            using var package = OpenMinimal();
            var sink = new FileSystemAssetSink(directory, baseUrl: null);

            var issues = await AssetRewriter.RewriteAsync(mdXml, package, sink, "report", TestContext.Current.CancellationToken);

            issues.Should().BeEmpty();
            var written = Path.Combine(directory, "img", "report", "image1.png");
            File.Exists(written).Should().BeTrue();
            // Not just that a file landed, but that OpenPartStream's bytes made it through
            // the sink intact -- this is the only direct exercise OpenPartStream gets.
            File.ReadAllText(written).Should().Be("PNG-BYTES");
            mdXml.Descendants(MdNames.Image).Single()
                 .Attribute("src")!.Value.Should().Be("img/report/image1.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_UsesTheBaseUrlWhenGiven()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/image1.png");
            using var package = OpenMinimal();
            var sink = new FileSystemAssetSink(directory, baseUrl: new Uri("https://cdn.example.com/docs"));

            await AssetRewriter.RewriteAsync(mdXml, package, sink, "report", TestContext.Current.CancellationToken);

            // Bytes still land locally; only what the Markdown says changed.
            File.Exists(Path.Combine(directory, "img", "report", "image1.png")).Should().BeTrue();
            mdXml.Descendants(MdNames.Image).Single().Attribute("src")!.Value
                 .Should().Be("https://cdn.example.com/docs/img/report/image1.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_ReportsMetafilesRatherThanDroppingThem()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/diagram.emf");
            using var package = OpenMinimal();

            var issues = await AssetRewriter.RewriteAsync(
                mdXml, package, new FileSystemAssetSink(directory, null), "report", TestContext.Current.CancellationToken);

            // Passed through, so nothing is lost -- but reported, because no Markdown
            // renderer displays EMF and the user needs to know before publishing.
            issues.Should().ContainSingle(i => i.Reason.Contains("EMF", StringComparison.Ordinal));
            var written = Path.Combine(directory, "img", "report", "diagram.emf");
            File.Exists(written).Should().BeTrue();
            // Passed through means byte-for-byte, not merely present.
            File.ReadAllText(written).Should().Be("EMF-BYTES");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_ReportsAMissingPartAndLeavesTheDocumentUsable()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/absent.png");
            using var package = OpenMinimal();

            var issues = await AssetRewriter.RewriteAsync(
                mdXml, package, new FileSystemAssetSink(directory, null), "report", TestContext.Current.CancellationToken);

            issues.Should().ContainSingle(i => i.PartName == "word/media/absent.png");
            // The image element is removed rather than left pointing at nothing.
            mdXml.Descendants(MdNames.Image).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_WritesEachPartOnceEvenWhenReferencedRepeatedly()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = new XDocument(
                new XElement(MdNames.Document,
                    new XElement(MdNames.Para, new XElement(MdNames.Image,
                        new XAttribute("src", "word/media/image1.png"), new XAttribute("alt", "A"))),
                    new XElement(MdNames.Para, new XElement(MdNames.Image,
                        new XAttribute("src", "word/media/image1.png"), new XAttribute("alt", "B")))));

            using var package = OpenMinimal();
            // A spy, not just FileSystemAssetSink directly: that sink overwrites
            // idempotently, so asserting on the two @src values alone cannot tell "written
            // once" from "written twice with the same result". Counting calls can.
            var sink = new CountingAssetSink(new FileSystemAssetSink(directory, null));
            await AssetRewriter.RewriteAsync(mdXml, package, sink, "report", TestContext.Current.CancellationToken);

            sink.WriteCount.Should().Be(1);
            mdXml.Descendants(MdNames.Image)
                 .Select(e => e.Attribute("src")!.Value)
                 .Should().AllBe("img/report/image1.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Counts calls so a test can prove "written once", not just infer it from an
    /// idempotent sink producing the same result twice.</summary>
    private sealed class CountingAssetSink(IAssetSink inner) : IAssetSink
    {
        public int WriteCount { get; private set; }

        public async Task<Uri> WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct)
        {
            WriteCount++;
            return await inner.WriteAsync(relativePath, content, contentType, ct).ConfigureAwait(false);
        }
    }
}
