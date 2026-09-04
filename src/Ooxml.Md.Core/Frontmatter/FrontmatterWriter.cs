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

    // Both the YAML 1.1 and 1.2 core schemas resolve these (case-insensitively) as the
    // null scalar rather than a string when left bare -- silently dropping the field's
    // actual content on the read side, not just changing its type.
    private static readonly string[] YamlNulls = ["~", "null"];

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
    /// YAML parser would resolve as a number (decimal, hex, or sexagesimal), a boolean, a
    /// null, or a date/timestamp instead of a string, or one carrying structural
    /// punctuation (a ": " sequence, a trailing colon, a leading indicator character, or
    /// leading/trailing whitespace).
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
        // Frontmatter values are display and filter metadata, not content -- an internal
        // line break here does not carry meaning worth preserving, and letting it through
        // corrupts far more than the one field: a plain scalar's "key: value" line would
        // span multiple physical lines with no key on the continuation, breaking every
        // field that follows it, not just this one. Collapsing to a single space (rather
        // than switching to double-quoted style for these values) keeps this function's
        // quoting to the one style it already has -- a second style would bring its own
        // backslash-escape rules to get wrong, inside the very function whose job is
        // stopping things from going wrong.
        var normalized = value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

        var needsQuoting =
            normalized.Contains(": ", StringComparison.Ordinal) ||
            normalized.EndsWith(':') ||
            normalized.StartsWith('#') || normalized.StartsWith('&') || normalized.StartsWith('*') ||
            normalized.StartsWith('[') || normalized.StartsWith('{') || normalized.StartsWith('-') ||
            normalized.StartsWith(' ') || normalized.EndsWith(' ') ||
            LooksLikeYamlNumber(normalized) ||
            LooksLikeYamlHex(normalized) ||
            LooksLikeYamlSexagesimal(normalized) ||
            LooksLikeYamlTimestamp(normalized) ||
            YamlBooleans.Contains(normalized, StringComparer.OrdinalIgnoreCase) ||
            YamlNulls.Contains(normalized, StringComparer.OrdinalIgnoreCase);

        return needsQuoting ? $"'{normalized.Replace("'", "''", StringComparison.Ordinal)}'" : normalized;
    }

    /// <summary>A bare value that a YAML parser would resolve as a decimal integer or a float.</summary>
    private static bool LooksLikeYamlNumber(string value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// A bare value shaped like a YAML 1.1 hex integer, e.g. "0x1F" -- read back as 31 by
    /// PyYAML/SnakeYAML-style resolvers, not the string "0x1F". Neither <c>long.TryParse</c>
    /// nor <c>double.TryParse</c> recognises this form, so it needs its own check.
    /// </summary>
    private static bool LooksLikeYamlHex(string value)
    {
        var span = value.AsSpan();
        if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
        {
            span = span[1..];
        }

        if (span.Length < 3 || span[0] != '0' || (span[1] != 'x' && span[1] != 'X'))
        {
            return false;
        }

        foreach (var c in span[2..])
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A bare value shaped like YAML 1.1's sexagesimal (base-60) integer form, e.g. "1:30"
    /// -- read back as 90 by PyYAML/SnakeYAML-style resolvers, not the string "1:30". The
    /// ": " structural check above only catches a colon immediately followed by a space,
    /// so a colon-separated number like this slips past it and needs its own shape check.
    /// </summary>
    private static bool LooksLikeYamlSexagesimal(string value)
    {
        var parts = value.Split(':');
        if (parts.Length < 2)
        {
            return false;
        }

        var first = parts[0].AsSpan();
        if (first.Length > 0 && (first[0] == '+' || first[0] == '-'))
        {
            first = first[1..];
        }

        if (first.IsEmpty || !IsAsciiDigitsOrUnderscores(first))
        {
            return false;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 2 ||
                !IsAsciiDigits(part.AsSpan()) ||
                int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture) > 59)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiDigitsOrUnderscores(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

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
