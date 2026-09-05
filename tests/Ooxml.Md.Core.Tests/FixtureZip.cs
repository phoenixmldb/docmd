namespace Ooxml.Md.Core.Tests;

using System.IO;
using System.IO.Compression;
using System.Linq;

/// <summary>
/// Turns a directory of OPC parts into an in-memory .docx. Fixtures are stored unzipped
/// so they are reviewable and diffable; see spec §13.1.
/// </summary>
public static class FixtureZip
{
    public static MemoryStream FromDirectory(string fixtureDirectory)
    {
        if (!Directory.Exists(fixtureDirectory))
        {
            // Loud, not vacuous. A missing fixture must never let a test pass having
            // examined nothing — see spec §13.3.
            throw new DirectoryNotFoundException($"Fixture directory not found: {fixtureDirectory}");
        }

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Ordered so the fixture zip itself is deterministic.
            var files = Directory.GetFiles(fixtureDirectory, "*", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var entryName = Path.GetRelativePath(fixtureDirectory, file).Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(entryStream);
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static string FixtureRoot(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}
