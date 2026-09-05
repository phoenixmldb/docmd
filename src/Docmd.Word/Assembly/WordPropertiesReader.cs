namespace Docmd.Word.Assembly;

using System.Xml.Linq;
using Ooxml.Md.Core.Frontmatter;

/// <summary>Reads docProps out of the composite into the frontmatter model.</summary>
public static class WordPropertiesReader
{
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace Ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

    public static DocumentProperties Read(XDocument composite, string sourceFileName, string sha256)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var properties = composite.Root?.Element(WordNames.Docmd + "properties");
        var core = properties?.Element(WordNames.Docmd + "core")?.Element(Cp + "coreProperties");
        var app = properties?.Element(WordNames.Docmd + "app")?.Element(Ep + "Properties");

        return new DocumentProperties
        {
            Title = Trim(core?.Element(Dc + "title")?.Value),
            Author = Trim(core?.Element(Dc + "creator")?.Value),
            // Passed through verbatim: they are already ISO-8601 in the XML, and
            // reformatting would introduce a culture dependency for no gain.
            Created = Trim(core?.Element(DcTerms + "created")?.Value),
            Modified = Trim(core?.Element(DcTerms + "modified")?.Value),
            Revision = Trim(core?.Element(Cp + "revision")?.Value),
            Company = Trim(app?.Element(Ep + "Company")?.Value),
            Source = sourceFileName,
            Sha256 = sha256,
        };
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
