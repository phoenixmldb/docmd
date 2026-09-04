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

        // Two adjacent same-formatting spans that failed to merge (see MergeAdjacentMarkup)
        // serialise as four consecutive asterisks, e.g. "**conver****sion**" -- that is the
        // real shape the bug takes, not the word-boundary regex an earlier draft of this
        // test used, which could never match it (see Task 14 review). This document has no
        // known split run, so this is a general safety net; the real, targeted coverage for
        // the split-and-rejoin behaviour itself is
        // RealDocument_RejoinsWordsSplitByProofingBoundaries, against a fixture built to
        // carry an actual split.
        markdown.Should().NotContain("****", "adjacent same-formatting spans must merge, not butt asterisks together");
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
        // identical bold formatting. Unmerged, MergeAdjacentMarkup's absence would produce
        // "**conver****sion**" -- four consecutive asterisks where the two spans meet, not
        // the earlier draft's word-boundary regex, which could never match that shape (see
        // Task 14 review) and was dropped rather than kept as a second, weaker check.
        markdown.Should().Contain("**conversion**", "a bold word split by proofing boundaries must rejoin");
        markdown.Should().NotContain("****", "adjacent same-formatting spans must merge, not butt asterisks together");

        // "assembly" is split "assem" | "bly" the same way, but across two PLAIN runs, with
        // proofErr/bookmarkStart/bookmarkEnd sitting directly between them. This is not a
        // second test of MergeAdjacentMarkup -- that function only ever touches md:strong/
        // md:em, and two adjacent md:text nodes were always concatenated correctly by
        // WriteInline, merge or no merge. What this actually proves is that proofErr and
        // the bookmark elements between the two runs contribute no md-XML node at all (the
        // stylesheet's inline dispatch never selects them), so nothing they carry -- not
        // even a stray space -- leaks in between "assem" and "bly".
        markdown.Should().Contain("assembly", "proofErr/bookmark noise between two plain runs must not leak text or whitespace between them");
        markdown.Should().NotContain("assem bly");

        // None of Word's own noise should leak into the visible text.
        markdown.Should().NotContain("proofErr");
        markdown.Should().NotContain("bookmarkStart");
        markdown.Should().NotContain("rsid");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
