namespace Ooxml.Md.Core;

using System.Globalization;
using System.Text;

/// <summary>
/// Produces stable, unique anchors for headings. One instance per document.
/// </summary>
/// <remarks>
/// Deliberately stateful: uniqueness requires remembering what has already been issued,
/// and the Markdown and the HTML companion must receive the <em>same</em> slug for the
/// same heading, which is why this runs once during annotation rather than inside each
/// emitter. See spec §4.4.
/// </remarks>
public sealed class Slugger
{
    private const string Fallback = "section";
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public string Slug(string text)
    {
        var builder = new StringBuilder(text?.Length ?? 0);
        var lastWasHyphen = true; // suppresses a leading hyphen

        // CA1308 wants ToUpperInvariant for round-trippable normalisation, but a slug is
        // a display anchor, not a security-sensitive comparison key -- Markdown headings
        // are conventionally lowercased, so lowercasing is the correct, deliberate choice.
#pragma warning disable CA1308
        var lowered = (text ?? "").ToLowerInvariant();
#pragma warning restore CA1308

        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var candidate = builder.ToString().Trim('-');
        if (candidate.Length == 0)
        {
            candidate = Fallback;
        }

        if (!_seen.TryGetValue(candidate, out var count))
        {
            _seen[candidate] = 0;
            return candidate;
        }

        count++;
        _seen[candidate] = count;
        return string.Create(CultureInfo.InvariantCulture, $"{candidate}-{count}");
    }
}
