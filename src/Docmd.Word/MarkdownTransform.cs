namespace Docmd.Word;

using System.Xml.Linq;
using PhoenixmlDb.Xslt;

/// <summary>
/// Pipeline stage 4: runs the md-XML stylesheet over an annotated composite and returns
/// the result as an <see cref="XDocument"/>.
/// </summary>
/// <remarks>
/// This exists because the wiring between the stylesheet and the serialiser is itself a
/// place data can be destroyed, and it was: the conversion pipeline and all four
/// stylesheet test suites each re-created the transformer and each called the bare
/// <c>XDocument.Parse(text)</c>, which defaults to <see cref="LoadOptions.None"/> and
/// therefore DISCARDS whitespace-only text nodes. A run containing nothing but a space is
/// what Word puts between a bold phrase and the next word, on either side of a
/// <c>w:hyperlink</c>, and wherever an rsid or proofing boundary falls, so dropping those
/// nodes welded words together ("Hello world" became "Helloworld", bold "Safety Review"
/// became "**Safety****Review**"). Both the stylesheet and the serialiser were correct;
/// only the join between them was wrong. Because the tests re-created the same join, they
/// reproduced the bug instead of catching it — which is the argument for there being
/// exactly one copy of it, here.
/// </remarks>
public static class MarkdownTransform
{
    /// <summary>Transforms an annotated composite into md-XML.</summary>
    public static async Task<XDocument> RunAsync(XDocument composite, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(StylesheetLoader.Read("markdown.xslt")).ConfigureAwait(false);

        // DisableFormatting rather than the default indenting serialisation: re-indenting
        // on the way in adds whitespace text nodes the source never had. The stylesheet's
        // xsl:strip-space would drop them again, but "add data, then hope something else
        // removes it" is not a property worth relying on in the one place whose entire job
        // is not changing the document.
        var result = await transformer
            .TransformAsync(composite.ToString(SaveOptions.DisableFormatting), ct)
            .ConfigureAwait(false);

        // PreserveWhitespace is load-bearing -- see the remarks above.
        return XDocument.Parse(result, LoadOptions.PreserveWhitespace);
    }
}
