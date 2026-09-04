namespace Docmd.Word.Tests;

using Docmd.Cli;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class CommandLineTests
{
    [Fact]
    public void Parse_DefaultsToConvertWithTheInputPath()
    {
        var result = CommandLine.Parse(["report.docx"]);

        result.Error.Should().BeNull();
        result.Command.Should().Be(CommandKind.Convert);
        result.Input.Should().Be("report.docx");
        result.Options!.IncludeImages.Should().BeTrue();
        result.Options.Flavour.Should().Be(MarkdownFlavour.Gfm);
    }

    [Fact]
    public void Parse_ReadsOutputAndAssetFlags()
    {
        var result = CommandLine.Parse(
            ["specs/", "-o", "out/", "--asset-base-url", "https://cdn.example.com/docs", "--no-images"]);

        result.Error.Should().BeNull();
        result.Options!.OutputDirectory.Should().Be("out/");
        // ConversionOptions.AssetBaseUrl is typed Uri? (Ruling 19), not string -- a bad value
        // must fail loudly at this boundary rather than surviving to FileSystemAssetSink.
        result.Options.AssetBaseUrl.Should().Be(new Uri("https://cdn.example.com/docs"));
        result.Options.IncludeImages.Should().BeFalse();
    }

    [Fact]
    public void Parse_RecognisesSubcommands()
    {
        CommandLine.Parse(["audit", "specs/"]).Command.Should().Be(CommandKind.Audit);
        CommandLine.Parse(["register", "--email", "a@b.com"]).Command.Should().Be(CommandKind.Register);
        CommandLine.Parse(["license"]).Command.Should().Be(CommandKind.License);
        CommandLine.Parse(["--version"]).Command.Should().Be(CommandKind.Version);
        CommandLine.Parse([]).Command.Should().Be(CommandKind.Help);
    }

    [Fact]
    public void Parse_RejectsAnUnknownFlag()
        => CommandLine.Parse(["report.docx", "--nope"]).Error.Should().Contain("--nope");

    [Fact]
    public void Parse_RejectsAFlagMissingItsValue()
        => CommandLine.Parse(["report.docx", "-o"]).Error.Should().Contain("-o");

    [Fact]
    public void Parse_RejectsAnUnknownFlavour()
        => CommandLine.Parse(["report.docx", "--flavour", "textile"]).Error.Should().Contain("textile");

    // Ruling 19 follow-through: the CLI must validate --asset-base-url with
    // Uri.TryCreate(raw, UriKind.Absolute, ...) so a malformed value fails here -- mapped by
    // Program to exit code 2 -- rather than surviving to FileSystemAssetSink deep in the
    // pipeline after thousands of files have already been written with broken links.
    [Fact]
    public void Parse_RejectsAMalformedAssetBaseUrl()
    {
        var result = CommandLine.Parse(["report.docx", "--asset-base-url", "not a url"]);

        result.Error.Should().Contain("--asset-base-url").And.Contain("not a url");
    }

    [Fact]
    public void Parse_RejectsARelativeAssetBaseUrl()
    {
        var result = CommandLine.Parse(["report.docx", "--asset-base-url", "cdn.example.com/docs"]);

        result.Error.Should().Contain("--asset-base-url");
    }

    // -r / --recursive is on the CLI surface but directory traversal is deferred to a later
    // plan. It must fail cleanly, never silently convert nothing or crash.
    [Fact]
    public void Parse_RejectsRecursiveAsNotYetSupported()
    {
        var result = CommandLine.Parse(["specs/", "-r"]);

        result.Error.Should().Contain("-r");
    }
}
