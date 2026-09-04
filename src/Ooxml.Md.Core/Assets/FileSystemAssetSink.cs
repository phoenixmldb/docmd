namespace Ooxml.Md.Core.Assets;

/// <summary>Writes assets beside the Markdown. The default, and the whole free tier.</summary>
/// <param name="outputDirectory">Directory assets are written under, on disk.</param>
/// <param name="baseUrl">
/// When set, the returned URI is this prefix plus the relative path, while the bytes still
/// land locally. That is the entirety of --asset-base-url: the customer's existing sync
/// step moves the files, and docmd only has to say the right thing. Typed as a
/// <see cref="Uri"/>, not a string, so a malformed value fails loudly at the argument
/// boundary (e.g. a CLI-side <c>Uri.TryCreate(raw, UriKind.Absolute, out var baseUri)</c>)
/// instead of surviving to the first <see cref="WriteAsync"/> call deep in the pipeline.
/// </param>
public sealed class FileSystemAssetSink(string outputDirectory, Uri? baseUrl) : IAssetSink
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

        // Deliberately string concatenation, not `new Uri(baseUrl, relativePath)`: that
        // constructor treats the base as a *file*, not a directory, unless it already ends
        // in '/', so "https://cdn.example.com/docs" combined with "img/x.png" silently
        // drops "docs" and produces "https://cdn.example.com/img/x.png". Concatenating the
        // base's literal text is the only way the whole prefix is guaranteed to survive.
        return baseUrl is null
            ? new Uri(relativePath, UriKind.Relative)
            : new Uri($"{baseUrl.OriginalString.TrimEnd('/')}/{relativePath}", UriKind.Absolute);
    }
}
