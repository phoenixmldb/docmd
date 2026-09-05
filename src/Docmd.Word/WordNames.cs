namespace Docmd.Word;

using System.Xml.Linq;

/// <summary>XML namespaces used across assembly and the stylesheets, declared once.</summary>
public static class WordNames
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace Docmd = "https://phoenixml.dev/docmd";
    public static readonly XNamespace Md = "https://phoenixml.dev/docmd/md";

    public const string MainDocumentRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
}
