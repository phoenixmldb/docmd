namespace Ooxml.Md.Core.Assets;

/// <summary>
/// Where extracted assets are written, and what URI the Markdown should reference.
/// </summary>
/// <remarks>
/// The seam that separates "where the bytes land" from "what the Markdown says". Cloud
/// sinks (Pixault, blob storage) implement this and ship as separate packages so their
/// SDKs never burden the base tool. See spec §10.1.
/// </remarks>
public interface IAssetSink
{
    /// <summary>Writes one asset and returns the URI to reference it by.</summary>
    Task<Uri> WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct);
}
