namespace Docmd.Word;

using System.Security.Cryptography;
using System.Text;
using Docmd.Word.Assembly;
using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Frontmatter;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.Opc;

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

        // 4: transform. The transform-and-parse join lives in one place (MarkdownTransform)
        // because it is itself a place data can be lost -- see that type's remarks.
        var mdXml = await MarkdownTransform.RunAsync(composite, options.StylesheetPath, ct).ConfigureAwait(false);

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
        // Hoisted so the async dispose can be explicit about its context, matching Task
        // 10's resolution of the identical CA2007 diagnostic in AssetRewriter.cs and
        // FileSystemAssetSink.cs -- one idiom for "await using triggers CA2007" everywhere
        // in this codebase, not a second one introduced here.
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
    }
}
