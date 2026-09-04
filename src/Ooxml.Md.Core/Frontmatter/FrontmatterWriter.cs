namespace Ooxml.Md.Core.Frontmatter;

using System.Globalization;
using System.Text;

/// <summary>Writes the YAML frontmatter block, fences included.</summary>
/// <remarks>
/// Field order is fixed rather than reflective, because determinism requires it and
/// because a stable order makes the frontmatter diffable across releases. YamlDotNet is
/// referenced by this project (for a later task's style-map parsing); this writer does not
/// route through it, because a serialiser's field ordering and quoting rules are exactly
/// what determinism needs pinned, and both are free to change out from under us across
/// package versions.
/// </remarks>
public static class FrontmatterWriter
{
    // YAML 1.1's core schema resolves these (case-insensitively) as booleans rather than
    // strings when left bare. Several frontmatter consumers (e.g. PyYAML- and SnakeYAML-
    // based tooling) still follow 1.1 resolution rules, so this is the conservative set to
    // guard against.
    private static readonly string[] YamlBooleans = ["y", "n", "yes", "no", "true", "false", "on", "off"];

    public static string Write(DocumentProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var builder = new StringBuilder("---\n");
        Append(builder, "title", properties.Title);
        Append(builder, "author", properties.Author);
        Append(builder, "created", properties.Created);
        Append(builder, "modified", properties.Modified);
        Append(builder, "revision", properties.Revision);
        Append(builder, "company", properties.Company);
        Append(builder, "source", properties.Source);
        Append(builder, "sha256", properties.Sha256);
        builder.Append("---\n\n");
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Omit rather than emit empty: a null field is noise in an index and matches
            // nothing when filtered on.
            return;
        }

        builder.Append(key).Append(": ").Append(Quote(value)).Append('\n');
    }

    /// <summary>
    /// Quotes a scalar only when leaving it bare would change what it means: a value a
    /// YAML parser would resolve as a number, a boolean, or a date/timestamp instead of a
    /// string, or one carrying structural punctuation (a ": " sequence, a trailing colon,
    /// a leading indicator character, or leading/trailing whitespace).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT "quote because the value starts with a digit" -- a SHA-256 digest
    /// is 64 hex characters and very often starts with one, and quoting every digest would
    /// be pure noise on the single most important field this writer emits. The rule instead
    /// asks whether the whole value would actually be misread, e.g. "9f2c1a" is not a valid
    /// YAML number or date and stays bare, while "2026-04-11" is a valid YAML date and must
    /// be quoted to stay a string.
    /// </remarks>
    private static string Quote(string value)
    {
        var needsQuoting =
            value.Contains(": ", StringComparison.Ordinal) ||
            value.EndsWith(':') ||
            value.StartsWith('#') || value.StartsWith('&') || value.StartsWith('*') ||
            value.StartsWith('[') || value.StartsWith('{') || value.StartsWith('-') ||
            value.StartsWith(' ') || value.EndsWith(' ') ||
            LooksLikeYamlNumber(value) ||
            LooksLikeYamlTimestamp(value) ||
            YamlBooleans.Contains(value, StringComparer.OrdinalIgnoreCase);

        return needsQuoting ? $"'{value.Replace("'", "''", StringComparison.Ordinal)}'" : value;
    }

    /// <summary>A bare value that a YAML parser would resolve as an integer or a float.</summary>
    private static bool LooksLikeYamlNumber(string value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// A bare value shaped like a YAML timestamp: an ISO-8601 date, optionally followed by
    /// a time. Matched by digit position rather than a regex or <c>DateTime.TryParse</c>,
    /// so there is no culture dependency, no backtracking risk, and no chance of a loose
    /// date parser accepting something a YAML resolver would not.
    /// </summary>
    private static bool LooksLikeYamlTimestamp(string value)
    {
        if (value.Length < 10 ||
            !IsAsciiDigits(value.AsSpan(0, 4)) || value[4] != '-' ||
            !IsAsciiDigits(value.AsSpan(5, 2)) || value[7] != '-' ||
            !IsAsciiDigits(value.AsSpan(8, 2)))
        {
            return false;
        }

        // A bare date ("2026-04-11") is already a timestamp. Anything past the date only
        // keeps being one if a time separator follows -- otherwise this is some other
        // value that merely starts with what looks like a date, e.g. "2026-04-11-draft".
        return value.Length == 10 || value[10] is 'T' or 't' or ' ';
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
