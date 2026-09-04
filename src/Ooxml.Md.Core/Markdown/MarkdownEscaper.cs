namespace Ooxml.Md.Core.Markdown;

/// <summary>
/// Placeholder escaping so <see cref="MarkdownSerializer"/> compiles and its tests pass.
/// </summary>
/// <remarks>
/// Internal: this is a throwaway that Task 6 replaces with the real implementation. Its
/// tests live in <c>Ooxml.Md.Core.Tests</c> -- a separate assembly -- which is granted
/// access via <c>[assembly: InternalsVisibleTo]</c> rather than by widening this type's own
/// surface. Replaced properly in Task 6.
/// </remarks>
internal static class MarkdownEscaper
{
    internal static string EscapeInline(string text) => text;

    internal static string EscapeUrl(string url) => url;

    internal static string CodeSpan(string content) => $"`{content}`";
}
