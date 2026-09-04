namespace Docmd.Word;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Docmd.Word.Assembly;
using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Frontmatter;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.Opc;
using PhoenixmlDb.Xslt;

/// <summary>Runs the five pipeline stages over one document.</summary>
public static class DocumentConverter
{
    public static async Task<ConversionResult> ConvertAsync(
        string inputPath, ConversionOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentNullException.ThrowIfNull(options);

        // Runs before OpcPackage.OpenFile so a non-zip file still surfaces as
        // OpcFormatException (mapped by the CLI to exit code 2), not an unrelated read error.
        var sha256 = await ComputeSha256Async(inputPath, ct).ConfigureAwait(false);
        var stem = Path.GetFileNameWithoutExtension(inputPath);

        // 1 + 2: open and compose.
        using var package = OpcPackage.OpenFile(inputPath);
        var composite = WordCompositeBuilder.Build(package);

        // 3: annotate.
        HeadingAnnotator.Annotate(composite);

        // 4: transform.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(StylesheetLoader.Read("markdown.xslt")).ConfigureAwait(false);
        var mdXml = XDocument.Parse(await transformer.TransformAsync(composite.ToString(), ct).ConfigureAwait(false));

        // 5a: assets first, so 5b can serialise the URIs the sink returned. Inverting this
        // would still write every asset to disk, but the Markdown string below would be
        // built from the pre-rewrite tree and would still name the raw OPC part.
        IReadOnlyList<AssetIssue> issues = [];
        if (options.IncludeImages)
        {
            var sink = new FileSystemAssetSink(options.OutputDirectory, options.AssetBaseUrl);
            issues = await AssetRewriter.RewriteAsync(mdXml, package, sink, stem, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var image in mdXml.Descendants(MdNames.Image).ToArray())
            {
                image.Remove();
            }
        }

        // 5b: serialise.
        var body = MarkdownSerializer.Serialize(mdXml, new MarkdownOptions { Flavour = options.Flavour });
        var properties = WordPropertiesReader.Read(composite, Path.GetFileName(inputPath), sha256);
        var markdown = options.IncludeFrontmatter
            ? FrontmatterWriter.Write(properties) + body
            : body;

        return new ConversionResult(markdown, properties, issues);
    }

    public static async Task<ConversionResult> WriteAsync(
        string inputPath, ConversionOptions options, CancellationToken ct)
    {
        var result = await ConvertAsync(inputPath, options, ct).ConfigureAwait(false);

        Directory.CreateDirectory(options.OutputDirectory);
        var destination = Path.Combine(
            options.OutputDirectory,
            Path.GetFileNameWithoutExtension(inputPath) + ".md");

        // UTF-8 without a BOM: a BOM is invisible, breaks byte-comparison expectations,
        // and confuses some downstream tooling.
        await File.WriteAllTextAsync(destination, result.Markdown, new UTF8Encoding(false), ct)
                  .ConfigureAwait(false);
        return result;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        // A plain `using`, not `await using`: FileStream's synchronous Dispose is fine
        // here (the read itself is already async via HashDataAsync), and `await using`
        // would insert an un-ConfigureAwait'd DisposeAsync call that CA2007 flags.
        using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
