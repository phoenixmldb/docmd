namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using Docmd.Word;
using FluentAssertions;
using Xunit;

/// <summary>
/// Word's own output is far messier than anything hand-written: w:proofErr through the
/// flow, runs split mid-word by spell-check, rsid attributes everywhere, bookmarks
/// between runs. Hand-written fixtures test intent; this tests reality.
/// </summary>
/// <remarks>
/// <c>fixtures/real/sample.docx</c> is a genuine LibreOffice-produced document (LibreOffice
/// is genuine third-party markup, which is the point -- see the fixture's commit message).
/// LibreOffice does not, however, reproduce Word's own proofing/rsid/bookmark noise, so
/// <see cref="RealDocument_RejoinsWordsSplitByProofingBoundaries"/> exercises a second,
/// hand-authored fixture that carries exactly that noise: <c>fixtures/word-noise</c>.
/// </remarks>
public sealed class RealDocumentTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private static string Sample => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "sample.docx");

    private string StageDocx(string fixture, string name)
    {
        var path = Path.Combine(_workspace, name);
        using var zip = FixtureZip.FromDirectory(FixtureZip.FixtureRoot(fixture));
        using var file = File.Create(path);
        zip.CopyTo(file);
        return path;
    }

    [Fact]
    public async Task RealDocument_ProducesTheExpectedStructure()
    {
        var result = await DocumentConverter.ConvertAsync(
            Sample, new ConversionOptions { OutputDirectory = _workspace }, TestContext.Current.CancellationToken);

        var markdown = result.Markdown;

        markdown.Should().Contain("\n# ", "the Heading 1 must survive");
        markdown.Should().Contain("\n## ", "the Heading 2 must survive");
        markdown.Should().Contain("\n### ", "the Heading 3 must survive");
        markdown.Should().Contain("\n- ", "the bullet list must survive");
        markdown.Should().Contain("\n1. ", "the numbered list must survive");
        markdown.Should().Contain("| --- |", "the table must survive with a delimiter row");
        markdown.Should().Contain("](", "the hyperlink and image must survive");
        markdown.Should().Contain("**", "bold text must survive");
        markdown.Should().Contain("*", "italic text must survive");

        // Literal Markdown metacharacters in the source prose must come through as text,
        // not be misread as table pipes, emphasis markers, or link syntax.
        markdown.Should().Contain(@"config\|prod\*staging\_v2.yaml");

        // Spell-check and rsid boundaries split runs mid-word. If they are not rejoined,
        // words come out fragmented -- the single most visible real-document failure.
        markdown.Should().NotMatchRegex(@"\*\*\w+\*\*\w", "runs split mid-word must be rejoined");
    }

    [Fact]
    public async Task RealDocument_ReportsNoUnexpectedAssetIssues()
    {
        var result = await DocumentConverter.ConvertAsync(
            Sample, new ConversionOptions { OutputDirectory = _workspace }, TestContext.Current.CancellationToken);

        result.AssetIssues.Should().BeEmpty("the sample uses a PNG, which renders everywhere");
    }

    /// <summary>
    /// LibreOffice never emits w:proofErr, w:rsid*, or w:bookmarkStart/End, and it never
    /// splits a word's characters across two adjacent w:r runs the way Word's spell-check
    /// does constantly. This fixture is hand-authored specifically to carry all four, so
    /// the converted output is checked against reality Word actually produces, not just
    /// intent a fixture author wrote down.
    /// </summary>
    [Fact]
    public async Task RealDocument_RejoinsWordsSplitByProofingBoundaries()
    {
        var path = StageDocx("word-noise", "noisy.docx");

        var result = await DocumentConverter.ConvertAsync(
            path, new ConversionOptions { OutputDirectory = _workspace }, TestContext.Current.CancellationToken);

        var markdown = result.Markdown;

        // "conversion" is split "conver" | "sion" across two w:r elements separated by
        // w:proofErr/w:bookmarkStart/w:bookmarkEnd, both runs carrying rsid attributes and
        // identical bold formatting. If the split is not rejoined, this comes out as
        // "**conver****sion**" -- visually similar when rendered, but fragmented in the
        // raw text a RAG index or a human diffing the file actually sees.
        markdown.Should().Contain("**conversion**", "a bold word split by proofing boundaries must rejoin");
        markdown.Should().NotContain("conver**sion", "the split must not leak into the plain-text half");
        markdown.Should().NotMatchRegex(@"\*\*\w+\*\*\w", "runs split mid-word must be rejoined, bold or not");

        // A second, unformatted word is split the same way to prove the rejoin is not an
        // artefact of the bold-merging path specifically.
        markdown.Should().Contain("assembly", "a plain word split by proofing boundaries must rejoin");
        markdown.Should().NotContain("assem bly");

        // None of Word's own noise should leak into the visible text.
        markdown.Should().NotContain("proofErr");
        markdown.Should().NotContain("bookmarkStart");
        markdown.Should().NotContain("rsid");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
