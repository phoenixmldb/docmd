namespace Ooxml.Md.Core.Markdown;

using System.Xml.Linq;

/// <summary>
/// The md-XML vocabulary: the intermediate tree a stylesheet emits and the serialiser
/// consumes. Declared once so a typo is a compile error rather than a silently dropped
/// element.
/// </summary>
public static class MdNames
{
    public static readonly XNamespace Md = "https://phoenixml.dev/docmd/md";

    public static readonly XName Document = Md + "document";
    public static readonly XName Heading = Md + "heading";
    public static readonly XName Para = Md + "para";
    public static readonly XName List = Md + "list";
    public static readonly XName Item = Md + "item";
    public static readonly XName Table = Md + "table";
    public static readonly XName Row = Md + "row";
    public static readonly XName Cell = Md + "cell";
    public static readonly XName CodeBlock = Md + "code-block";
    public static readonly XName Blockquote = Md + "blockquote";
    public static readonly XName Hr = Md + "hr";

    public static readonly XName Text = Md + "text";
    public static readonly XName Strong = Md + "strong";
    public static readonly XName Em = Md + "em";
    public static readonly XName Code = Md + "code";
    public static readonly XName Link = Md + "link";
    public static readonly XName Image = Md + "image";
    public static readonly XName Br = Md + "br";
}
