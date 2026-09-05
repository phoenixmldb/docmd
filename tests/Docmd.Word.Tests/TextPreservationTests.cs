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
/// End-to-end correctness: every word a reader sees in the .docx must still appear, in order,
/// in the Markdown.
/// </summary>
/// <remarks>
/// This is the oracle the suite was missing. Before it, docx-to-Markdown correctness rested on
/// one fixture with hand-written expectations plus reading corpus output by eye, so a construct
/// could be dropped wholesale and every test would stay green -- the suite asserted what the
/// fixtures contained, never what a document contained.
/// </remarks>
public sealed class TextPreservationTests : IDisposable
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

    // ---- the mechanism itself, proven able to fail ----

    private static XDocument Source(string bodyInner) => XDocument.Parse($"""
        <docmd:package xmlns:docmd="https://phoenixml.dev/docmd"
                       xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
        </docmd:package>
        """, LoadOptions.PreserveWhitespace);

    private static string Para(string text) =>
        $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    [Fact]
    public void Oracle_ReportsAWordTheMarkdownDropped()
        // Proving the check can fail. An oracle that has never reported a loss is not evidence.
        => TextPreservationOracle.Check(Source(Para("alpha beta gamma")), "alpha gamma\n")
            .Should().ContainSingle().Which.Missing.Should().Be("beta");

    [Fact]
    public void Oracle_ReportsWordsWeldedTogether()
    {
        // The exact defect that once turned "Hello world" into "Helloworld" and shipped, because
        // every test helper reproduced the join that caused it.
        var lost = TextPreservationOracle.Check(
            Source("""<w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:t xml:space="preserve"> </w:t></w:r><w:r><w:t>world</w:t></w:r></w:p>"""),
            "Helloworld\n");

        lost.Should().NotBeEmpty("welding two words into one destroys both tokens");
    }

    [Fact]
    public void Oracle_AllowsTheOutputToAddWords()
        // Markdown legitimately adds list markers, alt text and link destinations.
        => TextPreservationOracle.Check(Source(Para("beta")), "1. alpha beta gamma\n")
            .Should().BeEmpty();

    [Fact]
    public void Oracle_CatchesReordering()
        => TextPreservationOracle.Check(Source(Para("alpha beta")), "beta alpha\n")
            .Should().NotBeEmpty("order carries meaning; a subsequence check still enforces it");

    [Fact]
    public void Oracle_IgnoresTrackedDeletions()
        // A reader does not see deleted text, so the Markdown must not contain it and the
        // oracle must not demand it.
        => TextPreservationOracle.Check(
                Source("""<w:p><w:del><w:r><w:t>removed</w:t></w:r></w:del><w:r><w:t>kept</w:t></w:r></w:p>"""),
                "kept\n")
            .Should().BeEmpty();

    [Fact]
    public void Oracle_SeesThroughOurOwnEscaping()
        // The output escapes these; read back through a real parser they are the source's words
        // again. String matching would have called this a loss.
        => TextPreservationOracle.Check(
                Source(Para("file_name.txt and 100% of 5 &lt; 10")),
                @"file\_name.txt and 100% of 5 &lt; 10" + "\n")
            .Should().BeEmpty();

    // ---- the real pipeline ----

    private async Task AssertNoTextLost(string input)
    {
        var result = await DocumentConverter.ConvertAsync(
            input,
            new ConversionOptions { OutputDirectory = _workspace, IncludeFrontmatter = false },
            TestContext.Current.CancellationToken);

        using var package = OpcPackage.OpenFile(input);
        var composite = WordCompositeBuilder.Build(package, StyleMap.Empty);

        TextPreservationOracle.Check(composite, result.Markdown)
            .Should().BeEmpty($"every word in {Path.GetFileName(input)} should survive conversion");
    }

    [Theory]
    [InlineData("composite-basic", "report.docx")]
    [InlineData("with-image", "with-image.docx")]
    [InlineData("word-noise", "noise.docx")]
    public async Task Fixture_LosesNoText(string fixture, string name)
        => await AssertNoTextLost(StageDocx(fixture, name));

    [Fact]
    public async Task ARealWordDocument_LosesNoText()
        // fixtures/real/sample.docx is a genuine LibreOffice-produced file rather than a
        // hand-written fixture, so it carries the run splitting, rsid noise and style
        // indirection that real documents have and hand-written XML never quite reproduces.
        // It is the closest thing in the suite to the corpus, and the most valuable single
        // case this oracle runs against.
        => await AssertNoTextLost(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "sample.docx"));

    /// <summary>
    /// Audits a folder of real documents and writes a report. Opt in with DOCMD_CORPUS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This reports rather than gates, and the distinction is deliberate. On the 49-document
    /// sample it was built against, 38 convert without losing a word and 11 do not, for reasons
    /// that are real defects rather than oracle noise: text inside text boxes is never reached,
    /// and inline content controls and fields are not descended into (see docs/limitations.md).
    /// Asserting zero loss here would be a test that cannot pass, which decays into a test
    /// nobody runs.
    /// </para>
    /// <para>
    /// The gates are the fixture cases above, including a genuine LibreOffice document. This is
    /// the instrument for measuring how far that guarantee extends to documents we have not
    /// seen, and for telling whether a change moved the number.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Corpus_Audit()
    {
        var corpus = Environment.GetEnvironmentVariable("DOCMD_CORPUS");
        Assert.SkipWhen(
            string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus),
            "Set DOCMD_CORPUS to a folder of .docx files to audit text preservation.");

        var documents = Directory.GetFiles(corpus!, "*.docx", SearchOption.AllDirectories)
                                 .Order(StringComparer.Ordinal)
                                 .ToArray();
        documents.Should().NotBeEmpty("DOCMD_CORPUS is set but holds no .docx files");

        var report = new List<string>();
        var threw = new List<string>();
        var clean = 0;

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

                using var package = OpcPackage.OpenFile(document);
                var composite = WordCompositeBuilder.Build(package, StyleMap.Empty);
                var lost = TextPreservationOracle.Check(composite, result.Markdown);

                if (lost.Count == 0)
                {
                    clean++;
                    continue;
                }

                report.Add($"{Path.GetFileName(document)}: {lost.Count} word(s) lost");
                report.AddRange(lost.Take(3).Select(l => $"     '{l.Missing}' near \"{l.SourceContext}\""));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                threw.Add($"{Path.GetFileName(document)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var path = Path.Combine(Path.GetTempPath(), "docmd-corpus-audit.txt");
        await File.WriteAllLinesAsync(
            path,
            [$"{clean}/{documents.Length} documents lost no text", "", .. report, "", "Threw:", .. threw],
            TestContext.Current.CancellationToken);

        // The one hard assertion: a document may convert imperfectly, but it must convert.
        // An exception ends a batch run, which is a different and worse failure than losing a
        // word, and it is the promise docmd makes about corpora.
        threw.Should().BeEmpty($"every document must convert without throwing; audit at {path}");
    }
}
