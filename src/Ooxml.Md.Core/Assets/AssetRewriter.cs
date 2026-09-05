namespace Ooxml.Md.Core.Assets;

using System.Xml.Linq;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.Opc;

/// <summary>Something worth telling the user about an asset. Never fatal.</summary>
public sealed record AssetIssue(string PartName, string Reason);

/// <summary>
/// Emit phase 5a: writes every referenced asset through the sink, then replaces each
/// md:image/@src with the URI the sink returned.
/// </summary>
/// <remarks>
/// The ordering is structural, not stylistic. Rewriting URLs inside finished Markdown text
/// would mean pattern-matching link syntax, which breaks the first time a filename contains
/// a bracket. Doing it on the tree, before serialisation, cannot break that way.
/// </remarks>
public static class AssetRewriter
{
    // Uppercase, per CA1308: ToUpperInvariant is the round-trippable normalisation form,
    // and this comparison is internal (never displayed), so there is nothing lost by
    // preferring it over ToLowerInvariant.
    private static readonly string[] UnrenderableExtensions = [".EMF", ".WMF"];

    public static async Task<IReadOnlyList<AssetIssue>> RewriteAsync(
        XDocument mdXml, OpcPackage package, IAssetSink sink, string documentStem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mdXml);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(sink);

        var issues = new List<AssetIssue>();
        // Each part is written once however often it is referenced.
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var image in mdXml.Descendants(MdNames.Image).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var partName = (string?)image.Attribute("src") ?? "";

            if (written.TryGetValue(partName, out var existing))
            {
                image.SetAttributeValue("src", existing);
                continue;
            }

            if (!package.ContainsPart(partName))
            {
                // Remove rather than leave a link pointing at nothing: a broken image in
                // a RAG corpus is noise, and in a rendered document it is an error icon.
                issues.Add(new AssetIssue(partName, "Referenced image part is missing from the package."));
                image.Remove();
                continue;
            }

            var fileName = partName[(partName.LastIndexOf('/') + 1)..];
            var extension = Path.GetExtension(fileName).ToUpperInvariant();
            if (Array.IndexOf(UnrenderableExtensions, extension) >= 0)
            {
                // Passed through so nothing is lost, but reported: no Markdown renderer
                // displays EMF/WMF, and the user needs to know before publishing.
                issues.Add(new AssetIssue(partName,
                    $"Image is {extension.TrimStart('.')}, which Markdown renderers do not display."));
            }

            var relativePath = $"img/{documentStem}/{fileName}";
            var content = package.OpenPartStream(partName);
            await using (content.ConfigureAwait(false))
            {
                var uri = await sink.WriteAsync(relativePath, content, ContentTypeFor(extension), ct).ConfigureAwait(false);
                var reference = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
                written[partName] = reference;
                image.SetAttributeValue("src", reference);
            }
        }

        return issues;
    }

    /// <summary>Expects an uppercase extension (including the leading dot), matching how
    /// <see cref="RewriteAsync"/> normalises it.</summary>
    private static string ContentTypeFor(string extension) => extension switch
    {
        ".PNG" => "image/png",
        ".JPG" or ".JPEG" => "image/jpeg",
        ".GIF" => "image/gif",
        ".SVG" => "image/svg+xml",
        ".BMP" => "image/bmp",
        ".TIF" or ".TIFF" => "image/tiff",
        ".EMF" => "image/x-emf",
        ".WMF" => "image/x-wmf",
        _ => "application/octet-stream",
    };
}
