namespace Ooxml.Md.Core.Assets;

/// <summary>Writes assets beside the Markdown. The default, and the whole free tier.</summary>
/// <param name="outputDirectory">Directory assets are written under, on disk.</param>
/// <param name="baseUrl">
/// When set, the returned URI is this prefix plus the relative path, while the bytes still
/// land locally. That is the entirety of --asset-base-url: the customer's existing sync
/// step moves the files, and docmd only has to say the right thing.
/// </param>
// baseUrl is a URL prefix fragment (e.g. "https://cdn.example.com/docs"), concatenated
// with a relative path rather than used as a standalone URI, so System.Uri is not the
// right parameter type despite what CA1054 suggests. The brief's contract (spec §10.1)
// fixes this constructor's shape verbatim.
#pragma warning disable CA1054
public sealed class FileSystemAssetSink(string outputDirectory, string? baseUrl) : IAssetSink
#pragma warning restore CA1054
{
    public async Task<Uri> WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        var destination = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var file = File.Create(destination);
        await using (file.ConfigureAwait(false))
        {
            await content.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        return string.IsNullOrEmpty(baseUrl)
            ? new Uri(relativePath, UriKind.Relative)
            : new Uri($"{baseUrl.TrimEnd('/')}/{relativePath}", UriKind.Absolute);
    }
}
