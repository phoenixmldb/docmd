namespace Docmd.Cli;

using Docmd.Word;
using Ooxml.Md.Core.Markdown;

internal enum CommandKind { Convert, Audit, Register, License, Help, Version }

internal sealed record ParseResult(
    CommandKind Command,
    string? Input,
    ConversionOptions? Options,
    string? Error);

/// <summary>
/// Hand-rolled argument parsing.
/// </summary>
/// <remarks>
/// System.CommandLine's API has moved repeatedly across previews, and a churning
/// dependency is a liability in a commercial product. Fifteen flags do not justify it.
/// </remarks>
internal static class CommandLine
{
    internal static ParseResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return new ParseResult(CommandKind.Help, null, null, null);
        }

        switch (args[0])
        {
            case "--version" or "-V":
                return new ParseResult(CommandKind.Version, null, null, null);
            case "--help" or "-h":
                return new ParseResult(CommandKind.Help, null, null, null);
            case "audit":
                return new ParseResult(CommandKind.Audit, args.ElementAtOrDefault(1), null, null);
            case "register":
                return new ParseResult(CommandKind.Register, null, null, null);
            case "license":
                return new ParseResult(CommandKind.License, null, null, null);
            default:
                break;
        }

        string? input = null;
        var output = ".";
        Uri? assetBaseUrl = null;
        var includeImages = true;
        var includeFrontmatter = true;
        var flavour = MarkdownFlavour.Gfm;
        var imageDirectory = "img";

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith('-'))
            {
                // A second positional argument is a mistake, not a second job: "docmd a.docx
                // b.docx" silently converted a.docx and said nothing about b.docx, which in a
                // shell loop or a glob means an operator believes a corpus was converted when
                // most of it was not. Batch conversion is a later plan; until it exists, say so.
                if (input is not null)
                {
                    return Fail($"Unexpected argument '{argument}'. Pass a single input file; " +
                                 "converting several at once is not yet supported.");
                }

                input = argument;
                continue;
            }

            switch (argument)
            {
                case "--no-images":
                    includeImages = false;
                    break;
                case "--review":
                    // The review companion (spec §7.2) is a later plan. Accepting the flag and
                    // silently producing no .review.html is the worst of the three options:
                    // every other unimplemented flag here fails cleanly, and this is the flag
                    // most likely to be tried, being the headline feature.
                    return Fail($"{argument} (the HTML review companion) is not yet supported.");
                case "-r" or "--recursive":
                    // Directory traversal is deferred to a later plan (the corpus audit
                    // needs it too, and the two should share one implementation). Fail
                    // cleanly rather than silently converting nothing or crashing.
                    return Fail($"{argument} (recursive directory conversion) is not yet supported. " +
                                 "Pass a single file.");
                case "-o" or "--output":
                    if (!TryTake(args, ref i, out output!))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    break;
                case "--asset-base-url":
                    if (!TryTake(args, ref i, out var assetBaseUrlText))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    // Ruling 19 follow-through: validate here, at the argument boundary, so a
                    // malformed value fails one flag with a clear diagnostic (exit code 2)
                    // instead of surviving to FileSystemAssetSink and emitting a run's worth
                    // of Markdown with broken asset links before anyone notices.
                    if (!Uri.TryCreate(assetBaseUrlText, UriKind.Absolute, out var parsedAssetBaseUrl))
                    {
                        return Fail(
                            $"--asset-base-url must be an absolute URL, e.g. https://cdn.example.com/docs. " +
                            $"Got '{assetBaseUrlText}'.");
                    }

                    assetBaseUrl = parsedAssetBaseUrl;
                    break;
                case "--img-dir":
                    if (!TryTake(args, ref i, out imageDirectory!))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    break;
                case "--front-matter":
                    if (!TryTake(args, ref i, out var frontMatter))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    switch (frontMatter)
                    {
                        case "yaml":
                            includeFrontmatter = true;
                            break;
                        case "none":
                            includeFrontmatter = false;
                            break;
                        default:
                            // A bad argument is a diagnostic, never an exception: the CLI
                            // must exit 2 with a message, not a stack trace.
                            return Fail($"Unknown front-matter mode '{frontMatter}'. Use yaml or none.");
                    }

                    break;
                case "--flavour":
                    if (!TryTake(args, ref i, out var flavourText))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    if (!Enum.TryParse(flavourText, ignoreCase: true, out flavour))
                    {
                        return Fail($"Unknown flavour '{flavourText}'. Use gfm or commonmark.");
                    }

                    break;
                default:
                    return Fail($"Unknown option '{argument}'.");
            }
        }

        var options = new ConversionOptions
        {
            OutputDirectory = output,
            AssetBaseUrl = assetBaseUrl,
            IncludeImages = includeImages,
            IncludeFrontmatter = includeFrontmatter,
            Flavour = flavour,
            ImageDirectoryName = imageDirectory,
        };

        return new ParseResult(CommandKind.Convert, input, options, null);
    }

    private static bool TryTake(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            value = "";
            return false;
        }

        value = args[++index];
        return true;
    }

    private static ParseResult Fail(string message)
        => new(CommandKind.Help, null, null, message);
}
