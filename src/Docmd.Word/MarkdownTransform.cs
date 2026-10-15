namespace Docmd.Word;

using System.Xml.Linq;
using PhoenixmlDb.XQuery.Security;
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
    /// <summary>
    /// How long any one regular-expression operation in a stylesheet may run before the engine
    /// abandons it.
    /// </summary>
    /// <remarks>
    /// The engine's own default is no limit, and a cancellation token is not a substitute: a
    /// transform whose whole running time sits inside one <c>matches()</c> call never returns
    /// to a template, so nothing looks at the token. A catastrophically backtracking pattern
    /// ran for 90,886 ms after cancellation was requested at 1,000 ms (issue 48). Ten seconds
    /// is far longer than any single match a stylesheet performs legitimately -- the built-in
    /// one evaluates no regular expressions at all -- and far shorter than that. It is a
    /// default rather than a constant because a large document can make a legitimate match
    /// slow; <c>--regex-timeout</c> moves it, and null removes it.
    /// </remarks>
    public static TimeSpan DefaultRegexMatchTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Transforms an annotated composite into md-XML using the built-in stylesheet.</summary>
    public static Task<XDocument> RunAsync(XDocument composite, CancellationToken ct)
        => RunAsync(composite, stylesheetPath: null, DefaultRegexMatchTimeout, ct);

    /// <summary>
    /// Transforms an annotated composite into md-XML, optionally with a user stylesheet.
    /// </summary>
    /// <param name="composite">The annotated composite document to transform.</param>
    /// <param name="stylesheetPath">
    /// A stylesheet to run instead of the built-in one, or null for the built-in. Its own
    /// directory becomes the base URI, so relative <c>xsl:import</c> and <c>xsl:include</c>
    /// in a user stylesheet resolve against where that file lives rather than the process's
    /// working directory.
    /// </param>
    /// <param name="ct">Cancels the transform.</param>
    public static Task<XDocument> RunAsync(XDocument composite, string? stylesheetPath, CancellationToken ct)
        => RunAsync(composite, stylesheetPath, DefaultRegexMatchTimeout, ct);

    /// <summary>
    /// Transforms an annotated composite into md-XML, optionally with a user stylesheet, under
    /// an explicit regular-expression time limit.
    /// </summary>
    /// <param name="composite">The annotated composite document to transform.</param>
    /// <param name="stylesheetPath">
    /// A stylesheet to run instead of the built-in one, or null for the built-in. Its own
    /// directory becomes the base URI, so relative <c>xsl:import</c> and <c>xsl:include</c>
    /// in a user stylesheet resolve against where that file lives rather than the process's
    /// working directory.
    /// </param>
    /// <param name="regexMatchTimeout">
    /// Longest any one regular-expression operation may run, or null for no limit. See
    /// <see cref="DefaultRegexMatchTimeout"/> for why the default is not null.
    /// </param>
    /// <param name="ct">Cancels the transform.</param>
    public static async Task<XDocument> RunAsync(
        XDocument composite, string? stylesheetPath, TimeSpan? regexMatchTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(composite);
        if (regexMatchTimeout is { } limit && limit <= TimeSpan.Zero)
        {
            // Null is how "no limit" is said. A zero or negative TimeSpan reaching the engine
            // would be a limit nothing can satisfy, which is a different failure wearing the
            // same clothes.
            throw new ArgumentOutOfRangeException(
                nameof(regexMatchTimeout), limit, "The regex match timeout must be positive, or null for no limit.");
        }

        string stylesheet;
        Uri? baseUri = null;
        if (stylesheetPath is null)
        {
            stylesheet = StylesheetLoader.Read("markdown.xslt");
        }
        else
        {
            if (!File.Exists(stylesheetPath))
            {
                throw new FileNotFoundException($"Stylesheet not found: {stylesheetPath}", stylesheetPath);
            }

            stylesheet = await File.ReadAllTextAsync(stylesheetPath, ct).ConfigureAwait(false);
            baseUri = new Uri(Path.GetFullPath(stylesheetPath));
        }

        var transformer = new XsltTransformer
        {
            RegexMatchTimeout = regexMatchTimeout,
            ResourcePolicy = PolicyFor(stylesheetPath),
        };
        await transformer.LoadStylesheetAsync(stylesheet, baseUri).ConfigureAwait(false);

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

    /// <summary>
    /// What a stylesheet may reach while it runs. The engine's default is no policy at all,
    /// which means every scheme and every path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing for the built-in stylesheet: it has no <c>xsl:import</c>, reads no documents and
    /// evaluates no regular expressions, so a policy that admits nothing is exactly its needs.
    /// It is set anyway rather than left null, so that adding a read to the built-in stylesheet
    /// fails in the test suite instead of quietly acquiring a capability.
    /// </para>
    /// <para>
    /// For a stylesheet the caller passed, reads and imports are separated on purpose. Imports
    /// are allowed anywhere on the local file system: a house stylesheet commonly sits beside a
    /// shared module directory rather than above one, and refusing that would break the layout
    /// <c>--stylesheet</c> exists to serve. Reads -- <c>document()</c>, <c>unparsed-text()</c>,
    /// collections -- are confined to the stylesheet's own directory, which covers the lookup
    /// table shipped next to the stylesheet that uses it and not the rest of the disk. An
    /// imported module is still judged by the same read rules, so admitting imports broadly does
    /// not widen what any of them may read.
    /// </para>
    /// <para>
    /// No scheme is allowed outright, so <c>http</c> and <c>https</c> are refused for both: a
    /// stylesheet that fetches while converting an internal document is the exposure, and
    /// nothing docmd does needs the network. Writes are refused for the same reason -- docmd
    /// writes the Markdown, not the stylesheet. <c>xsl:evaluate</c> stays allowed because it
    /// only computes, and whatever it computes is judged by these same rules.
    /// </para>
    /// </remarks>
    private static ResourcePolicy PolicyFor(string? stylesheetPath)
    {
        var policy = ResourcePolicy.CreateBuilder()
            .AllowDtdProcessing(false)
            .AllowXslEvaluate();

        if (stylesheetPath is not null)
        {
            // The engine canonicalises both sides of a file: prefix comparison (links resolved),
            // so the plain full path is what belongs here.
            var directory = Path.GetDirectoryName(Path.GetFullPath(stylesheetPath));
            policy = policy.AllowImportFrom("file");
            if (!string.IsNullOrEmpty(directory))
            {
                policy = policy.AllowReadFrom("file", host: null, pathPrefix: directory);
            }
        }

        return policy.Build();
    }
}
