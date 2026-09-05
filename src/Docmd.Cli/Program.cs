namespace Docmd.Cli;

using System.Diagnostics;
using Docmd.Word;
using Ooxml.Md.Core.Opc;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitInternalError = 1;
    private const int ExitBadInput = 2;

    // Spec §12 reserves 4 for a degradation report under --strict, which is later work.
    // Defined so the exit-code contract is complete and the number is reserved; deliberately
    // unreferenced rather than wired to a fake path that would return it for the wrong reason.
    // Codes 3 (not registered) and 5 (paid feature) were retired when docmd became open core:
    // there is nothing to register for and nothing here to gate.
#pragma warning disable CA1823 // reserved exit code -- see comment above; no reachable path yet by design
    private const int ExitDegradedUnderStrict = 4;
#pragma warning restore CA1823

    internal static Task<int> Main(string[] args) => Run(args);

    /// <summary>
    /// The whole program, kept separate from <see cref="Main"/> so the exit-code contract can
    /// be exercised from a test rather than only from a shell.
    /// </summary>
    internal static async Task<int> Run(string[] args)
    {
        var parsed = CommandLine.Parse(args);

        if (parsed.Error is not null)
        {
            await Console.Error.WriteLineAsync($"docmd: {parsed.Error}").ConfigureAwait(false);
            return ExitBadInput;
        }

        switch (parsed.Command)
        {
            case CommandKind.Help:
                await Console.Out.WriteLineAsync(UsageText).ConfigureAwait(false);
                return ExitSuccess;
            case CommandKind.PrintStylesheet:
                await Console.Out.WriteLineAsync(StylesheetLoader.Read("markdown.xslt")).ConfigureAwait(false);
                return ExitSuccess;
            case CommandKind.Version:
                var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                await Console.Out.WriteLineAsync(version).ConfigureAwait(false);
                return ExitSuccess;
            case CommandKind.Audit or CommandKind.Register or CommandKind.License:
                var name = parsed.Command switch
                {
                    CommandKind.Audit => "audit",
                    CommandKind.Register => "register",
                    CommandKind.License => "license",
                    _ => throw new UnreachableException(),
                };
                await Console.Error.WriteLineAsync($"docmd: '{name}' is not available yet.").ConfigureAwait(false);

                // Spec §12 reserves 1 for an unexpected internal error. A deliberately
                // unimplemented subcommand is neither unexpected nor internal, and
                // returning 1 for it leaves a CI script unable to tell a bug from a
                // command it should not have run.
                return ExitBadInput;
            default:
                break;
        }

        // IsNullOrWhiteSpace, not `is null`: `docmd ""` reached DocumentConverter, whose
        // ArgumentException landed in the catch-all below and exited 1 -- an internal-error
        // code for what is plainly a usage error, and a stack-trace-shaped message for
        // exactly the case the CLI promises never to produce one for.
        if (string.IsNullOrWhiteSpace(parsed.Input))
        {
            await Console.Error.WriteLineAsync("docmd: no input file given.").ConfigureAwait(false);
            return ExitBadInput;
        }

        // -r/--recursive already fails during parsing (CommandLine.Parse), but a bare
        // directory path with no flag reaches here: fail cleanly for the same reason --
        // directory traversal is not implemented in this plan -- instead of letting
        // DocumentConverter fault on a path it cannot open as a file.
        if (Directory.Exists(parsed.Input))
        {
            await Console.Error.WriteLineAsync(
                $"docmd: '{parsed.Input}' is a directory. Recursive directory conversion is not yet " +
                "supported; pass a single file.").ConfigureAwait(false);
            return ExitBadInput;
        }

        try
        {
            var result = await DocumentConverter.WriteAsync(parsed.Input, parsed.Options!, CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var issue in result.AssetIssues)
            {
                await Console.Error.WriteLineAsync($"! {issue.PartName}: {issue.Reason}").ConfigureAwait(false);
            }

            return ExitSuccess;
        }
        catch (OpcFormatException ex)
        {
            await Console.Error.WriteLineAsync($"docmd: {ex.Message}").ConfigureAwait(false);
            return ExitBadInput;
        }
        catch (IOException ex)
        {
            await Console.Error.WriteLineAsync($"docmd: {ex.Message}").ConfigureAwait(false);
            return ExitBadInput;
        }
        catch (UnauthorizedAccessException ex)
        {
            await Console.Error.WriteLineAsync($"docmd: {ex.Message}").ConfigureAwait(false);
            return ExitBadInput;
        }
        // Final backstop, not a substitute for the specific catches above: without it, any
        // exception this pipeline does not anticipate (e.g. ArgumentException from an empty
        // input path) surfaces as a raw stack trace, which contradicts the rule that bad
        // input is never a stack trace, and leaves exit code 1 unreachable despite being
        // specified. Prints a one-line message and keeps the exception detail off stdout.
#pragma warning disable CA1031 // deliberate top-level catch-all -- see comment above
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"docmd: unexpected error: {ex.Message}").ConfigureAwait(false);
            return ExitInternalError;
        }
#pragma warning restore CA1031
    }

    private const string UsageText = """
        docmd - convert Microsoft Word documents to Markdown

        Usage:
          docmd <input.docx> [options]
          docmd audit <path> [-r]
          docmd register --email <address> | --key <key>
          docmd license

        Options:
          -o, --output <path>        output directory (default: .)
              --asset-base-url <url> emit remote URLs for local assets
              --stylesheet <file>    run your own stylesheet instead of the built-in one
              --img-dir <name>       image folder name (default: img)
              --no-images            omit images entirely
              --flavour <name>       gfm | commonmark (default: gfm)
              --front-matter <mode>  yaml | none (default: yaml)
              --print-stylesheet     write the built-in stylesheet to stdout and exit
          -h, --help                 show this help
          -V, --version              show the version

        Only .docx, .docm, .dotx and .dotm are supported. Word 97-2003 (.doc) is a
        different, binary format - re-save it as .docx first.

        Recursive directory conversion (-r / --recursive, and "docmd audit"), the HTML
        review companion (--review), and "docmd register" / "docmd license" are not yet
        implemented. Each fails with a message rather than doing nothing quietly.
        """;
}
