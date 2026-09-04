namespace Ooxml.Md.Core.Opc;

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Read-only access to an OPC (Open Packaging Conventions) container — the ZIP that a
/// .docx or .pptx actually is.
/// </summary>
/// <remarks>
/// Parts reference each other by relationship id, never by path, and a relationship's
/// target is relative to the directory of the part that declares it. That resolution is
/// the reason this type exists rather than callers using ZipArchive directly.
/// </remarks>
public sealed class OpcPackage : IDisposable
{
    private static readonly XNamespace RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly ZipArchive _archive;
    private readonly Stream? _ownedStream;
    private readonly ConcurrentDictionary<string, IReadOnlyList<OpcRelationship>> _relationshipCache = new(StringComparer.Ordinal);

    private OpcPackage(ZipArchive archive, Stream? ownedStream)
    {
        _archive = archive;
        _ownedStream = ownedStream;
    }

    public static OpcPackage Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            return new OpcPackage(archive, leaveOpen ? null : stream);
        }
        catch (InvalidDataException ex)
        {
            // OpcPackage only takes ownership of the stream on success (recorded as
            // _ownedStream). On this failure path nothing else will ever dispose it, so
            // this method -- not each caller -- must do it itself, unless the caller asked
            // to keep owning the stream via leaveOpen.
            if (!leaveOpen)
            {
                stream.Dispose();
            }

            throw new OpcFormatException(
                "The file is not a readable OPC package. Word 97-2003 (.doc) files are a " +
                "different, binary format; re-save as .docx.", ex);
        }
    }

    public static OpcPackage OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new OpcFormatException($"File not found: {path}");
        }

        return Open(File.OpenRead(path));
    }

    public bool ContainsPart(string partName)
    {
        ArgumentNullException.ThrowIfNull(partName);
        return _archive.GetEntry(Normalise(partName)) is not null;
    }

    public Stream OpenPartStream(string partName)
    {
        ArgumentNullException.ThrowIfNull(partName);
        var entry = _archive.GetEntry(Normalise(partName))
            ?? throw new OpcFormatException($"Package part not found: {partName}");
        return entry.Open();
    }

    public XDocument ReadXmlPart(string partName)
        => TryReadXmlPart(partName)
           ?? throw new OpcFormatException($"Package part not found: {partName}");

    public XDocument? TryReadXmlPart(string partName)
    {
        ArgumentNullException.ThrowIfNull(partName);
        var entry = _archive.GetEntry(Normalise(partName));
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        // DtdProcessing.Prohibit: OOXML parts never legitimately carry a DTD, and
        // honouring one in an untrusted document is an entity-expansion vector.
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        });

        try
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new OpcFormatException($"Package part is not well-formed XML: {partName}", ex);
        }
    }

    public IReadOnlyList<OpcRelationship> RelationshipsFor(string partName)
    {
        ArgumentNullException.ThrowIfNull(partName);
        return _relationshipCache.GetOrAdd(Normalise(partName), LoadRelationships);
    }

    /// <summary>
    /// Resolves a relationship id declared by <paramref name="sourcePartName"/> to either a
    /// package part name or, for external relationships, the target URI verbatim.
    /// Returns null when the id is not declared.
    /// </summary>
    public string? ResolveTarget(string sourcePartName, string relationshipId)
    {
        ArgumentNullException.ThrowIfNull(sourcePartName);
        ArgumentNullException.ThrowIfNull(relationshipId);
        var relationship = RelationshipsFor(sourcePartName)
            .FirstOrDefault(r => string.Equals(r.Id, relationshipId, StringComparison.Ordinal));

        if (relationship is null)
        {
            return null;
        }

        if (relationship.IsExternal)
        {
            return relationship.Target;
        }

        var target = relationship.Target;
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }

        var directory = GetDirectory(Normalise(sourcePartName));
        var combined = directory.Length == 0 ? target : $"{directory}/{target}";
        return NormaliseDotSegments(combined);
    }

    private IReadOnlyList<OpcRelationship> LoadRelationships(string partName)
    {
        var relsPartName = $"{GetDirectory(partName)}/_rels/{GetFileName(partName)}.rels".TrimStart('/');
        var document = TryReadXmlPart(relsPartName);
        if (document?.Root is null)
        {
            // No .rels sibling is normal, not an error.
            return [];
        }

        return document.Root
            .Elements(RelationshipsNamespace + "Relationship")
            .Select(e => new OpcRelationship(
                Id: (string?)e.Attribute("Id") ?? "",
                Type: (string?)e.Attribute("Type") ?? "",
                Target: (string?)e.Attribute("Target") ?? "",
                IsExternal: string.Equals((string?)e.Attribute("TargetMode"), "External", StringComparison.Ordinal)))
            .ToArray();
    }

    private static string Normalise(string partName) => partName.Replace('\\', '/').TrimStart('/');

    private static string GetDirectory(string partName)
    {
        var index = partName.LastIndexOf('/');
        return index < 0 ? "" : partName[..index];
    }

    private static string GetFileName(string partName)
    {
        var index = partName.LastIndexOf('/');
        return index < 0 ? partName : partName[(index + 1)..];
    }

    private static string NormaliseDotSegments(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." && segments.Count > 0)
            {
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    public void Dispose()
    {
        _archive.Dispose();
        _ownedStream?.Dispose();
    }
}
