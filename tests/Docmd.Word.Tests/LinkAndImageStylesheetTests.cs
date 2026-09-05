namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class LinkAndImageStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string Relationships = """
        <docmd:relationship id="rId8" type="hyperlink" target="https://example.com/spec" external="true"/>
        <docmd:relationship id="rId7" type="image" target="word/media/image1.png" external="false"/>
        """;

    /// <summary>Runs the real stylesheet over a hand-built composite and returns md-XML.</summary>
    private static async Task<XDocument> TransformAsync(string bodyInner)
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}"
                xmlns:wp="{Wp}" xmlns:a="{A}" xmlns:pic="{Pic}" xmlns:r="{R}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles/></docmd:styles>
              <docmd:numbering><w:numbering/></docmd:numbering>
              <docmd:relationships>
                {Relationships}
              </docmd:relationships>
              <docmd:properties/>
            </docmd:package>
            """, LoadOptions.PreserveWhitespace);

        HeadingAnnotator.Annotate(composite);
        return await MarkdownTransform.RunAsync(composite, TestContext.Current.CancellationToken);
    }

    /// <summary>Transforms, then serialises -- the two stages callers actually compose.</summary>
    private static async Task<string> ToMarkdownAsync(string bodyInner)
        => MarkdownSerializer.Serialize(await TransformAsync(bodyInner));

    [Fact]
    public async Task Hyperlink_BecomesALink()
        => (await ToMarkdownAsync("""
            <w:p><w:hyperlink r:id="rId8"><w:r><w:t>the spec</w:t></w:r></w:hyperlink></w:p>
            """))
            .Should().Be("[the spec](https://example.com/spec)\n");

    [Fact]
    public async Task DanglingHyperlink_DegradesToPlainText()
        => (await ToMarkdownAsync("""
            <w:p><w:hyperlink r:id="rId99"><w:r><w:t>orphan</w:t></w:r></w:hyperlink></w:p>
            """))
            .Should().Be("orphan\n");

    [Fact]
    public async Task Drawing_BecomesAnImageCarryingThePartName()
    {
        // src is the part name here; AssetRewriter replaces it with the sink's URI.
        var mdXml = await TransformAsync("""
            <w:p><w:r><w:drawing><wp:inline>
              <wp:docPr id="1" name="Picture 1" descr="Vent assembly"/>
              <a:graphic><a:graphicData><pic:pic><pic:blipFill>
                <a:blip r:embed="rId7"/></pic:blipFill></pic:pic></a:graphicData></a:graphic>
            </wp:inline></w:drawing></w:r></w:p>
            """);

        var image = mdXml.Descendants(MdNames.Image).Single();
        image.Attribute("src")!.Value.Should().Be("word/media/image1.png");
        image.Attribute("alt")!.Value.Should().Be("Vent assembly");
    }

    [Fact]
    public async Task Drawing_BetweenTwoTextRunsInOneRun_StaysInPosition()
    {
        // Ruling 17: the plan's given fix (apply-templates for w:drawing appended after
        // the w:t/w:br loop) is the same reordering bug Task 7 already found for w:br --
        // an inline image sitting between two text runs would jump to the end of its run.
        // w:drawing must join the "w:t | w:br" document-order union instead, so it emits
        // in its true position.
        var mdXml = await TransformAsync("""
            <w:p><w:r>
              <w:t>before</w:t>
              <w:drawing><wp:inline>
                <wp:docPr id="1" name="Picture 1" descr="Vent assembly"/>
                <a:graphic><a:graphicData><pic:pic><pic:blipFill>
                  <a:blip r:embed="rId7"/></pic:blipFill></pic:pic></a:graphicData></a:graphic>
              </wp:inline></w:drawing>
              <w:t>after</w:t>
            </w:r></w:p>
            """);

        var para = mdXml.Descendants(MdNames.Para).Single();
        para.Elements().Select(e => e.Name).Should().Equal(MdNames.Text, MdNames.Image, MdNames.Text);
        para.Elements(MdNames.Text).Select(e => e.Value).Should().Equal("before", "after");
    }
}
