namespace Ooxml.Md.Core.Markdown;

/// <summary>
/// Placeholder escaping so <see cref="MarkdownSerializer"/> compiles and its tests pass.
/// </summary>
/// <remarks>
/// Public because Task 6 replaces this with the real implementation, whose tests live in
/// <c>Ooxml.Md.Core.Tests</c> -- a separate assembly -- and therefore need public access.
/// Replaced properly in Task 6.
/// </remarks>
public static class MarkdownEscaper
{
    public static string EscapeInline(string text) => text;

    // CA1054/CA1055 want a System.Uri-shaped API, but Markdown destinations are frequently
    // not valid System.Uri values (bare relative paths, "img/x.png", empty strings for a
    // missing href) and this is text serialisation, not a network API -- the rule's premise
    // (misuse of a URI as a plain string) does not hold for a Markdown emitter.
#pragma warning disable CA1054, CA1055
    public static string EscapeUrl(string url) => url;
#pragma warning restore CA1054, CA1055

    public static string CodeSpan(string content) => $"`{content}`";
}
