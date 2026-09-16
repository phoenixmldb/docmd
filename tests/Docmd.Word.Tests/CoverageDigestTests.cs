namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

/// <summary>
/// The digest exists so someone can report a conversion problem without sending us the
/// document. These tests are what make that claim checkable rather than asserted.
/// </summary>
public sealed class CoverageDigestTests
{
    private static XDocument Source(string bodyInner) => XDocument.Parse($"""
        <docmd:package xmlns:docmd="https://phoenixml.dev/docmd"
                       xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                       xmlns:acme="https://acme.example/schema/pricing">
          <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
        </docmd:package>
        """, LoadOptions.PreserveWhitespace);

    [Fact]
    public void Render_ContainsNoWordFromTheDocument()
    {
        // The privacy property, checked rather than documented. Every distinctive word in this
        // fixture is one a real report must never carry, and the assertion is over all of them
        // rather than over the ones a reviewer thought to name.
        var words = new[]
        {
            "Confidential", "Acme", "Hollingsworth", "Severance", "12500", "Whitfield",
        };
        var body = string.Join("", words.Select(w =>
            $"<w:p><w:r><w:pict><w:txbxContent><w:p><w:r><w:t>{w}</w:t></w:r></w:p></w:txbxContent></w:pict></w:r></w:p>"));

        var coverage = TextCoverageReport.Measure(Source(body), "\n");
        coverage.LostWords.Should().NotBeEmpty("the fixture must actually lose words, or this proves nothing");

        var digest = CoverageDigest.Render([CoverageFacts.From(coverage)]);

        foreach (var word in words)
        {
            digest.Should().NotContain(word,
                "a digest a user pastes into a public issue must carry no word from their document");
        }
    }

    [Fact]
    public void CoverageFacts_CannotCarryDocumentText()
    {
        // Redaction by construction: the renderer is not given the text and then trusted to
        // drop it. CoverageFacts has no field capable of holding it, so a future edit to the
        // renderer cannot reintroduce the leak -- there is nothing to reintroduce.
        var coverage = TextCoverageReport.Measure(
            Source("""<w:p><w:r><w:t>Hollingsworth Severance Agreement</w:t></w:r></w:p>"""),
            "\n");
        var facts = CoverageFacts.From(coverage);

        typeof(CoverageFacts).GetProperties()
            .Select(p => p.PropertyType)
            .Should().NotContain(typeof(string),
                "no property of CoverageFacts may hold free text");

        facts.LostWords.Should().Be(coverage.LostWords.Count);
        facts.SourceWords.Should().Be(coverage.SourceWords);
    }

    [Fact]
    public void Causes_ReportAForeignNamespaceWithoutNamingIt()
    {
        // A template author's custom XML can call its elements anything -- the name itself is
        // their content. Reporting "foreign" still says an unrecognised wrapper cost someone a
        // word, which is the actionable half, without printing what they called it.
        var coverage = TextCoverageReport.Measure(
            Source("""
                <w:p><w:r><w:pict><w:txbxContent>
                  <acme:ContractValue><w:p><w:r><w:t>boxed</w:t></w:r></w:p></acme:ContractValue>
                </w:txbxContent></w:pict></w:r></w:p>
                """),
            "\n");

        var named = coverage.Causes.Select(c => c.Construct).ToArray();

        named.Should().Contain("txbxContent", "a published-schema name is reported as itself");
        named.Should().NotContain("ContractValue", "a customer's element name must never be printed");
        named.Should().Contain("foreign", "but the unrecognised wrapper must still be reported");
    }

    [Fact]
    public void Render_AggregatesAcrossDocumentsAndIdentifiesThemByOrdinal()
    {
        var one = new CoverageFacts(100, 2, [new LossCause("drawing", 2)]);
        var two = new CoverageFacts(200, 0, []);
        var three = new CoverageFacts(300, 5, [new LossCause("drawing", 1), new LossCause("foreign", 4)]);

        var digest = CoverageDigest.Render([one, two, three]);

        digest.Should().Contain("3 document(s), 600 words");
        digest.Should().Contain("1 intact · 2 with losses · 7 lost");
        digest.Should().Contain("drawing");
        digest.Should().Contain("#1").And.Contain("#3");
        digest.Should().NotContain("#2", "a document that lost nothing needs no line of its own");
    }

    [Fact]
    public void Render_SaysNothingOfTheFilesystem()
    {
        var digest = CoverageDigest.Render([new CoverageFacts(10, 1, [new LossCause("drawing", 1)])]);

        digest.Should().NotContain(".docx", "a filename typically carries a client and a project");

        // Only the trailer may carry a URL, and it is ours. Everything above it must be free
        // of anything path-shaped.
        var body = digest[..digest.IndexOf("  No document text", StringComparison.Ordinal)];
        body.Should().NotContain("/").And.NotContain("\\",
            "a path says more about the customer than a filename does");
    }
}
