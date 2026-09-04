namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;
using Ooxml.Md.Core.Opc;

/// <summary>
/// Pipeline stage 2: composes the WordprocessingML parts a transform needs into a single
/// XML document.
/// </summary>
/// <remarks>
/// The alternative — a custom URI resolver letting the stylesheet call
/// <c>document('styles.xml')</c> inside the archive — was rejected. Composing makes the
/// transform a pure function of one input, so the composite can be dumped, diffed and used
/// directly as a fixture. See spec §4.3.
/// </remarks>
public static class WordCompositeBuilder
{
    public static XDocument Build(OpcPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var mainPartName = FindMainDocumentPart(package);
        var mainDocument = package.ReadXmlPart(mainPartName);
        var body = mainDocument.Root?.Element(WordNames.W + "body")
            ?? throw new OpcFormatException($"The main document part '{mainPartName}' has no w:body.");

        var mainDirectory = mainPartName.Contains('/', StringComparison.Ordinal)
            ? mainPartName[..mainPartName.LastIndexOf('/')]
            : "";

        var styles = package.TryReadXmlPart(Sibling(mainDirectory, "styles.xml"))?.Root;
        var numbering = package.TryReadXmlPart(Sibling(mainDirectory, "numbering.xml"))?.Root;
        var core = package.TryReadXmlPart("docProps/core.xml")?.Root;
        var app = package.TryReadXmlPart("docProps/app.xml")?.Root;

        var relationships = package.RelationshipsFor(mainPartName).Select(relationship =>
            new XElement(WordNames.Docmd + "relationship",
                new XAttribute("id", relationship.Id),
                new XAttribute("type", relationship.Type),
                new XAttribute("target", package.ResolveTarget(mainPartName, relationship.Id) ?? relationship.Target),
                new XAttribute("external", relationship.IsExternal ? "true" : "false")));

        var properties = new XElement(WordNames.Docmd + "properties");
        if (core is not null)
        {
            properties.Add(new XElement(WordNames.Docmd + "core", new XElement(core)));
        }

        if (app is not null)
        {
            properties.Add(new XElement(WordNames.Docmd + "app", new XElement(app)));
        }

        return new XDocument(
            new XElement(WordNames.Docmd + "package",
                new XAttribute(XNamespace.Xmlns + "docmd", WordNames.Docmd.NamespaceName),
                new XAttribute("main-part", mainPartName),
                // docmd:body/styles/numbering wrap the original w:body/w:styles/w:numbering
                // element rather than splicing its children in directly. This is
                // deliberate, not redundant nesting: the stylesheets match on the wrapped
                // element itself (Task 7's entry point is
                // "docmd:body/w:body", its recursive template matches "w:body", and it
                // selects numbering via "docmd:numbering/w:numbering"), so removing the
                // wrapper breaks every one of those selectors. See the Task 3 fix-round
                // note in the SDD report for the assertions that got this wrong first.
                new XElement(WordNames.Docmd + "body", new XElement(body)),
                // Always present even when the source part is absent, so the stylesheet
                // never has to test for existence before selecting into them.
                new XElement(WordNames.Docmd + "styles", styles is null ? null : new XElement(styles)),
                new XElement(WordNames.Docmd + "numbering", numbering is null ? null : new XElement(numbering)),
                new XElement(WordNames.Docmd + "relationships", relationships),
                properties));
    }

    private static string FindMainDocumentPart(OpcPackage package)
    {
        // The main part is whatever the package relationship points at. Hardcoding
        // word/document.xml works for Word's own output but not for every producer.
        // package.RelationshipsFor("") returns the package-level relationships at
        // _rels/.rels; see OpcPackageTests.RelationshipsFor_ResolvesThePackageLevelRelsForTheEmptyPartName.
        var relationship = package.RelationshipsFor("")
            .FirstOrDefault(r => string.Equals(r.Type, WordNames.MainDocumentRelationshipType, StringComparison.Ordinal));

        var resolved = relationship is null
            ? null
            : package.ResolveTarget("", relationship.Id);

        return resolved
            ?? throw new OpcFormatException(
                "The package declares no main document relationship, so it is not a Word document.");
    }

    private static string Sibling(string directory, string fileName) =>
        directory.Length == 0 ? fileName : string.Create(CultureInfo.InvariantCulture, $"{directory}/{fileName}");
}
