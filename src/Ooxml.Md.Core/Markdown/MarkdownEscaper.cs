namespace Ooxml.Md.Core.Markdown;

using System.Buffers;
using System.Globalization;
using System.Text;

/// <summary>
/// The single source of truth for turning literal document text into safe Markdown.
/// </summary>
/// <remarks>
/// Word documents carry literal <c>*</c>, <c>_</c>, <c>|</c>, <c>#</c> and <c>[</c> in
/// ordinary prose — part numbers, paths, wildcards. Emitted raw they become markup. The
/// proof that this is right is not the tests below but the Markdig differential oracle:
/// serialise, parse back, compare.
///
/// Internal by design (see plan Ruling 12): this is throwaway-shaped API, not something
/// downstream repos should build against. The test assembly reaches it through the
/// existing <c>[assembly: InternalsVisibleTo]</c> in <c>AssemblyInfo.cs</c>.
/// </remarks>
internal static class MarkdownEscaper
{
    /// <summary>
    /// Characters that can begin inline markup anywhere in a line. Deliberately narrower
    /// than CommonMark's full punctuation set: escaping everything is legal but produces
    /// backslash-strewn output that reads badly for humans, and these are the characters
    /// that actually change meaning.
    /// </summary>
    private static readonly SearchValues<char> InlineSpecials =
        SearchValues.Create(['\\', '`', '*', '_', '[', ']', '<', '>', '|', '~']);

    internal static string EscapeInline(string text)
    {
        if (string.IsNullOrEmpty(text) || text.AsSpan().IndexOfAny(InlineSpecials) < 0)
        {
            return text ?? "";
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (InlineSpecials.Contains(ch))
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes markers that only mean something at the start of a line. Applied by the
    /// serialiser after inline escaping, because whether a character is line-leading is a
    /// property of the assembled line, not of any single text node.
    /// </summary>
    internal static string EscapeLineStart(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line ?? "";
        }

        var leading = line.Length - line.TrimStart(' ').Length;
        var rest = line[leading..];
        if (rest.Length == 0)
        {
            return line;
        }

        // Block markers: heading, quote, bullet, thematic break.
        if (rest[0] is '#' or '>' or '-' or '+')
        {
            return string.Concat(line.AsSpan(0, leading), "\\", rest);
        }

        // Ordered-list marker: digits followed by '.' or ')'.
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits < rest.Length && rest[digits] is '.' or ')')
        {
            return string.Concat(line.AsSpan(0, leading), rest.AsSpan(0, digits), "\\", rest.AsSpan(digits));
        }

        return line;
    }

    /// <summary>
    /// Wraps content as a code span. Content is never escaped — that is what a code span
    /// is for — so a backtick inside is handled by widening the fence, per CommonMark.
    /// </summary>
    internal static string CodeSpan(string content)
    {
        var text = content ?? "";
        var longestRun = 0;
        var currentRun = 0;
        foreach (var ch in text)
        {
            currentRun = ch == '`' ? currentRun + 1 : 0;
            longestRun = Math.Max(longestRun, currentRun);
        }

        var fence = new string('`', longestRun + 1);

        // Padding spaces stop the fence from swallowing a leading/trailing backtick.
        return longestRun == 0 ? $"{fence}{text}{fence}" : $"{fence} {text} {fence}";
    }

    /// <summary>
    /// Percent-encodes the characters that would terminate an inline link destination.
    /// </summary>
    /// <remarks>
    /// '%' is deliberately NOT encoded. Encoding it first (as this did) re-encodes every
    /// URL that already carries a percent escape, which is most of the URLs in the target
    /// corpora: a SharePoint or OneDrive path is full of "%20", and any link to a C#
    /// resource carries "%23". ".../C%23" became ".../C%2523" and ".../My%20Docs/" became
    /// ".../My%2520Docs/" -- links that still look plausible and resolve to nothing. The
    /// job here is narrower than "make this a valid URL", which is the caller's document
    /// to get right: it is only to stop a destination ending early or being rejected
    /// where it sits, so only the delimiters and the ASCII controls are touched.
    /// </remarks>
    internal static string EscapeUrl(string url)
    {
        var text = url ?? "";
        if (text.AsSpan().IndexOfAny(UrlSpecials) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (UrlSpecials.Contains(ch))
            {
                builder.Append(CultureInfo.InvariantCulture, $"%{(int)ch:X2}");
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The characters that would end an inline link destination or be rejected inside
    /// one: the parentheses delimiting it, the space, and the ASCII control characters
    /// CommonMark forbids there.
    /// </summary>
    private static readonly SearchValues<char> UrlSpecials = SearchValues.Create(BuildUrlSpecials());

    private static char[] BuildUrlSpecials()
    {
        var specials = new List<char> { '(', ')', ' ', '\u007F' };
        for (var ch = '\u0000'; ch < ' '; ch++)
        {
            specials.Add(ch);
        }

        return [.. specials];
    }
}
