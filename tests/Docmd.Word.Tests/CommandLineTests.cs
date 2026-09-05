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

    [Fact]
    public void Parse_RejectsAnUnknownFrontMatterMode()
        => CommandLine.Parse(["report.docx", "--front-matter", "toml"]).Error.Should().Contain("toml");

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

    // --review was accepted, advertised in --help, and did nothing: the run exited 0 having
    // produced no .review.html. Every other unimplemented flag here fails cleanly, and this
    // is the flag most likely to be tried, being spec §7.2's headline feature.
    [Fact]
    public void Parse_RejectsReviewAsNotYetSupported()
    {
        var result = CommandLine.Parse(["report.docx", "--review"]);

        result.Error.Should().Contain("--review");
    }

    [Fact]
    public void Parse_RejectsASecondPositionalArgument()
    {
        // "docmd a.docx b.docx" converted a.docx and said nothing about b.docx. In a shell
        // loop or behind a glob that means an operator believes a corpus was converted when
        // most of it was not -- the failure that is worst precisely because it is quiet.
        var result = CommandLine.Parse(["a.docx", "b.docx"]);

        result.Error.Should().Contain("b.docx");
    }

    [Fact]
    public void Parse_DoesNotMistakeAFlagValueForASecondPositionalArgument()
        // The guard above must not fire on "out/" or "img", which are consumed as flag
        // values rather than seen as positionals.
        => CommandLine.Parse(["a.docx", "-o", "out/", "--img-dir", "media", "--flavour", "gfm"])
            .Error.Should().BeNull();
}
