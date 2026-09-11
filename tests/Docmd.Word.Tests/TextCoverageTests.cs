namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Ooxml.Md.Core.StyleMapping;
using Xunit;

/// <summary>
/// End-to-end correctness: every word a reader sees in the .docx must still appear in the
/// Markdown, and docmd must say so when one does not.
/// </summary>
/// <remarks>
/// Before this existed, docx-to-Markdown correctness rested on one fixture with hand-written
/// expectations plus reading corpus output by eye. A construct could be dropped wholesale and
/// every test would stay green, because the suite asserted what the fixtures contained rather
/// than what a document contained.
/// </remarks>
public sealed class TextCoverageTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private string StageDocx(string fixture, string name)
    {
        var path = Path.Combine(_workspace, name);
        using var zip = FixtureZip.FromDirectory(FixtureZip.FixtureRoot(fixture));
        using var file = File.Create(path);
        zip.CopyTo(file);
        return path;
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static XDocument Source(string bodyInner) => XDocument.Parse($"""
        <docmd:package xmlns:docmd="https://phoenixml.dev/docmd"
                       xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
        </docmd:package>
        """, LoadOptions.PreserveWhitespace);

    private static string Para(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    // ---- the measurement, proven able to fail ----

    [Fact]
    public void Measure_ReportsAWordTheMarkdownDropped()
        // An oracle that has never reported a loss is not evidence.
        => TextCoverageReport.Measure(Source(Para("alpha beta gamma")), "alpha gamma\n")
            .LostWords.Should().ContainSingle().Which.Word.Should().Be("beta");

    [Fact]
    public void Measure_ReportsWordsWeldedTogether()
        // The defect that once turned "Hello world" into "Helloworld" and shipped, because every
        // test helper reproduced the join that caused it.
        => TextCoverageReport.Measure(
                Source("""<w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:t xml:space="preserve"> </w:t></w:r><w:r><w:t>world</w:t></w:r></w:p>"""),
                "Helloworld\n")
            .LostWords.Should().HaveCount(2, "welding two words into one destroys both");

    [Fact]
    public void Measure_AllowsTheOutputToAddWords()
        // Markdown legitimately adds list markers, alt text and link destinations.
        => TextCoverageReport.Measure(Source(Para("beta")), "1. alpha beta gamma\n")
            .LostWords.Should().BeEmpty();

    [Fact]
    public void Measure_IgnoresTrackedDeletions()
        => TextCoverageReport.Measure(
                Source("""<w:p><w:del><w:r><w:t>removed</w:t></w:r></w:del><w:r><w:t>kept</w:t></w:r></w:p>"""),
                "kept\n")
            .LostWords.Should().BeEmpty("a reader does not see deleted text");

    [Fact]
    public void Measure_SeesThroughOurOwnEscaping()
        // Read back through a real parser these are the source's words again. String matching
        // would have called this a loss.
        => TextCoverageReport.Measure(
                Source(Para("file_name.txt and 100% of 5 &lt; 10")),
                @"file\_name.txt and 100% of 5 &lt; 10" + "\n")
            .LostWords.Should().BeEmpty();

    [Fact]
    public void Measure_CountsNestedParagraphTextOnce()
    {
        // A paragraph inside a text box is a descendant of an outer paragraph. Walking each w:p
        // and collecting its descendants counted that text twice, and the duplicate could never
        // match, so the surplus was reported as loss. On a real document it turned a genuine
        // loss of 8 words into a claim of 3,307.
        var composite = Source("""
            <w:p><w:r><w:pict><w:txbxContent>
              <w:p><w:r><w:t>boxed words here</w:t></w:r></w:p>
            </w:txbxContent></w:pict></w:r></w:p>
            """);

        TextCoverageReport.SourceWords(composite).Should().Equal("boxed", "words", "here");
    }

    [Fact]
    public void Measure_DoesNotCascadeWhenAWordRepeats()
    {
        // Sequence alignment let a repeated common word pair with the wrong occurrence, advance
        // past everything between, and report the remainder as missing. Counting occurrences
        // cannot do that: only the genuinely absent word is reported.
        var composite = Source(Para("Statement of Work Alpha Statement of Confidentiality Beta"));

        TextCoverageReport.Measure(composite, "Statement of Confidentiality Beta Statement of Work\n")
            .LostWords.Should().ContainSingle().Which.Word.Should().Be("Alpha");
    }

    [Fact]
    public void Measure_NamesTheStructureTheLostWordsAreIn()
    {
        // "Words are missing" is not actionable; "they are inside <txbxContent>" is. Read from
        // the document rather than a fixed list of constructs we know we skip, because that list
        // went stale the moment the transform learned to read text boxes and began reporting
        // "which docmd does not read" about text boxes it had read correctly.
        var coverage = TextCoverageReport.Measure(
            Source("""
                <w:p><w:r><w:pict><w:txbxContent><w:p><w:r><w:t>boxed</w:t></w:r></w:p></w:txbxContent></w:pict></w:r></w:p>
                """),
            "\n");

        coverage.Causes.Select(c => c.Construct).Should().Contain(["txbxContent", "pict"]);
    }

    [Fact]
    public void Measure_DoesNotBlameTheCompositesOwnWrappers()
        // docmd:package and docmd:body are ancestors of every node in the composite, so naming
        // them would attach a meaningless cause to every loss ever reported.
        => TextCoverageReport.Measure(Source(Para("alpha beta")), "alpha\n")
            .Causes.Select(c => c.Construct).Should().NotContain(["package", "body"]);

    [Fact]
    public void Describe_SaysNothingWhenTheDocumentSurvivedIntact()
        // A warning users see on healthy documents is a warning they learn to ignore.
        => TextCoverageReport.Describe(
                TextCoverageReport.Measure(Source(Para("all present")), "all present\n"))
            .Should().BeEmpty();

    // ---- the real pipeline ----

    private async Task AssertNoTextLost(string input)
    {
        var result = await DocumentConverter.ConvertAsync(
            input,
            new ConversionOptions { OutputDirectory = _workspace, IncludeFrontmatter = false },
            TestContext.Current.CancellationToken);

        result.Coverage.LostWords.Should().BeEmpty(
            $"every word in {Path.GetFileName(input)} should survive conversion");
    }

    [Theory]
    [InlineData("composite-basic", "report.docx")]
    [InlineData("with-image", "with-image.docx")]
    [InlineData("word-noise", "noise.docx")]
    public async Task Fixture_LosesNoText(string fixture, string name)
        => await AssertNoTextLost(StageDocx(fixture, name));

    [Fact]
    public async Task ARealWordDocument_LosesNoText()
        // A genuine LibreOffice-produced file, carrying the run splitting and style indirection
        // that hand-written XML never quite reproduces.
        => await AssertNoTextLost(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "sample.docx"));

    /// <summary>Audits a folder of real documents. Opt in with DOCMD_CORPUS.</summary>
    /// <remarks>
    /// Reports rather than gates. On the 49-document sample it was built against, 43 lose
    /// nothing and total loss is twelve words, under 0.01% of all text; the causes are recorded
    /// in docs/limitations.md. Asserting zero here would be a test that cannot pass, which decays
    /// into one nobody runs. Its one hard assertion is that no document throws, because an
    /// exception ends a batch run.
    /// </remarks>
    [Fact]
    public async Task Corpus_Audit()
    {
        var corpus = Environment.GetEnvironmentVariable("DOCMD_CORPUS");
        Assert.SkipWhen(
            string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus),
            "Set DOCMD_CORPUS to a folder of .docx files to audit text coverage.");

        var documents = Directory.GetFiles(corpus!, "*.docx", SearchOption.AllDirectories)
                                 .Order(StringComparer.Ordinal).ToArray();
        documents.Should().NotBeEmpty("DOCMD_CORPUS is set but holds no .docx files");

        var report = new List<string>();
        var threw = new List<string>();
        int clean = 0, lostWords = 0, sourceWords = 0;

        foreach (var document in documents)
        {
            try
            {
                var result = await DocumentConverter.ConvertAsync(
                    document,
                    new ConversionOptions
                    {
                        OutputDirectory = _workspace,
                        IncludeFrontmatter = false,
                        IncludeImages = false,
                    },
                    TestContext.Current.CancellationToken);

                sourceWords += result.Coverage.SourceWords;
                lostWords += result.Coverage.LostWords.Count;
                if (result.Coverage.IsComplete)
                {
                    clean++;
                    continue;
                }

                report.Add($"{Path.GetFileName(document)}: {result.Coverage.LostWords.Count} of "
                           + $"{result.Coverage.SourceWords} words");
                report.AddRange(TextCoverageReport.Describe(result.Coverage).Skip(1).Take(4));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                threw.Add($"{Path.GetFileName(document)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var path = Path.Combine(Path.GetTempPath(), "docmd-corpus-audit.txt");
        await File.WriteAllLinesAsync(
            path,
            [
                $"{clean}/{documents.Length} documents lost no text",
                $"{lostWords} of {sourceWords} words lost overall",
                "", .. report, "", "Threw:", .. threw,
            ],
            TestContext.Current.CancellationToken);

        threw.Should().BeEmpty($"every document must convert without throwing; audit at {path}");
    }
}
