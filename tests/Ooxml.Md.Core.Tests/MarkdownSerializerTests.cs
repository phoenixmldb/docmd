namespace Ooxml.Md.Core.Tests;

using System.Xml.Linq;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class MarkdownSerializerTests
{
    private static string Serialize(string inner)
        => MarkdownSerializer.Serialize(XDocument.Parse(
            $"""<md:document xmlns:md="https://phoenixml.dev/docmd/md">{inner}</md:document>"""));

    [Fact]
    public void Heading_UsesHashesForItsLevel()
        => Serialize("""<md:heading level="2"><md:text>Findings</md:text></md:heading>""")
            .Should().Be("## Findings\n");

    [Fact]
    public void Paragraph_IsEmittedPlainly()
        => Serialize("""<md:para><md:text>Body text.</md:text></md:para>""")
            .Should().Be("Body text.\n");

    [Fact]
    public void Blocks_AreSeparatedByExactlyOneBlankLine()
        => Serialize("""
            <md:heading level="1"><md:text>Title</md:text></md:heading>
            <md:para><md:text>One.</md:text></md:para>
            <md:para><md:text>Two.</md:text></md:para>
            """)
            .Should().Be("# Title\n\nOne.\n\nTwo.\n");

    [Fact]
    public void Output_EndsWithExactlyOneNewline()
    {
        var result = Serialize("""<md:para><md:text>Text.</md:text></md:para>""");

        result.Should().EndWith("\n");
        result.Should().NotEndWith("\n\n");
    }

    [Fact]
    public void Output_NeverUsesCarriageReturns()
    {
        // A byte-identical guarantee that changes with the host OS is not a guarantee.
        Serialize("""
            <md:para><md:text>A.</md:text></md:para>
            <md:para><md:text>B.</md:text></md:para>
            """).Should().NotContain("\r");
    }

    [Fact]
    public void Inline_EmitsStrongEmphasisAndCode()
        => Serialize("""
            <md:para><md:strong><md:text>bold</md:text></md:strong><md:text> and </md:text><md:em><md:text>italic</md:text></md:em><md:text> and </md:text><md:code><md:text>code</md:text></md:code></md:para>
            """)
            .Should().Be("**bold** and *italic* and `code`\n");

    [Fact]
    public void Link_EmitsInlineForm()
        => Serialize("""<md:para><md:link href="https://example.com/spec"><md:text>the spec</md:text></md:link></md:para>""")
            .Should().Be("[the spec](https://example.com/spec)\n");

    [Fact]
    public void Image_EmitsAltTextAndSource()
        => Serialize("""<md:para><md:image src="img/report/image1.png" alt="Figure 1"/></md:para>""")
            .Should().Be("![Figure 1](img/report/image1.png)\n");

    [Fact]
    public void Image_WithNoAltTextStillEmitsEmptyBrackets()
        => Serialize("""<md:para><md:image src="img/report/image2.png" alt=""/></md:para>""")
            .Should().Be("![](img/report/image2.png)\n");

    [Fact]
    public void HardBreak_ContinuationLineIsEscapedAtItsOwnStart()
    {
        // A hard break puts a literal '\n' inside one paragraph's assembled text. If
        // EscapeLineStart only ran once, over the whole string, a continuation line that
        // happens to start with '-' or '#' would read back as a list item or heading
        // interrupting the paragraph -- real content corruption, and people genuinely do
        // fake lists with manual line breaks in Word.
        Serialize("""<md:para><md:text>Steps:</md:text><md:br/><md:text>- item</md:text></md:para>""")
            .Should().Be("Steps:  \n\\- item\n");
    }

    [Fact]
    public void HardBreak_ContinuationLineStartingWithHashIsEscaped()
        => Serialize("""<md:para><md:text>Note:</md:text><md:br/><md:text># not a heading</md:text></md:para>""")
            .Should().Be("Note:  \n\\# not a heading\n");

    [Fact]
    public void Blockquote_PrefixesEveryLine()
        => Serialize("""<md:blockquote><md:para><md:text>Caution.</md:text></md:para></md:blockquote>""")
            .Should().Be("> Caution.\n");

    [Fact]
    public void CodeBlock_UsesFencesAndCarriesItsLanguage()
        => Serialize("""<md:code-block language="csharp"><md:text>var x = 1;</md:text></md:code-block>""")
            .Should().Be("```csharp\nvar x = 1;\n```\n");

    [Fact]
    public void ThematicBreak_IsEmitted()
        => Serialize("<md:hr/>").Should().Be("---\n");

    [Fact]
    public void EmptyDocument_ProducesEmptyString()
        => Serialize("").Should().BeEmpty();

    [Fact]
    public void OrderedList_UsesNumericMarkers()
        => Serialize("""
            <md:list ordered="true">
                <md:item><md:para><md:text>One</md:text></md:para></md:item>
                <md:item><md:para><md:text>Two</md:text></md:para></md:item>
            </md:list>
            """)
            .Should().Be("1. One\n2. Two\n");

    [Fact]
    public void UnorderedList_UsesHyphenMarkers()
        => Serialize("""
            <md:list ordered="false">
                <md:item><md:para><md:text>One</md:text></md:para></md:item>
                <md:item><md:para><md:text>Two</md:text></md:para></md:item>
            </md:list>
            """)
            .Should().Be("- One\n- Two\n");

    [Fact]
    public void ListItem_WithMultipleBlocks_LeavesBlankSeparatorLinesEmpty()
        // The middle line is a blank separator between the item's two paragraphs. It must
        // be genuinely empty -- two trailing spaces is Markdown's hard-line-break syntax,
        // and it would contradict the byte-identical output guarantee.
        => Serialize("""<md:list ordered="false"><md:item><md:para><md:text>A</md:text></md:para><md:para><md:text>B</md:text></md:para></md:item></md:list>""")
            .Should().Be("- A\n\n  B\n");

    [Fact]
    public void NestedList_IndentsUnderParentMarker()
        // Corrected by Task 8 (docmd task-8-report.md): a nested list continues its parent
        // item's own content -- indentation alone marks that, exactly as it does for every
        // other physical line of the item -- so no blank separator precedes it. The original
        // version of this test asserted a blank line here ("- Parent\n\n  - Child\n"), which
        // was untested against a real multi-level list; Task 8's ThreeLevelsNest showed that
        // rule turns a genuinely nested Word outline into a blank line per level.
        => Serialize("""
            <md:list ordered="false">
                <md:item>
                    <md:para><md:text>Parent</md:text></md:para>
                    <md:list ordered="false">
                        <md:item><md:para><md:text>Child</md:text></md:para></md:item>
                    </md:list>
                </md:item>
            </md:list>
            """)
            .Should().Be("- Parent\n  - Child\n");

    [Fact]
    public void Table_TwoByTwoWithHeaderRow_EmitsPipesAndDelimiterRow()
        => Serialize("""
            <md:table>
                <md:row header="true"><md:cell><md:text>A</md:text></md:cell><md:cell><md:text>B</md:text></md:cell></md:row>
                <md:row><md:cell><md:text>1</md:text></md:cell><md:cell><md:text>2</md:text></md:cell></md:row>
            </md:table>
            """)
            .Should().Be("| A | B |\n| --- | --- |\n| 1 | 2 |\n");

    [Fact]
    public void Table_SingleEmptyCell_YieldsPipeSpaceSpacePipe()
        => Serialize("""<md:table><md:row header="true"><md:cell/></md:row></md:table>""")
            .Should().Be("|  |\n| --- |\n");

    [Fact]
    public void Table_WithExplicitNonHeaderFirstRow_SynthesizesEmptyHeader()
        // md:row/@header is part of the vocabulary; GFM cannot express a headerless table,
        // so an explicit header="false" on the first row must not silently promote it --
        // instead a synthetic empty header row is emitted ahead of the delimiter.
        => Serialize("""
            <md:table>
                <md:row header="false"><md:cell><md:text>1</md:text></md:cell><md:cell><md:text>2</md:text></md:cell></md:row>
            </md:table>
            """)
            .Should().Be("|  |  |\n| --- | --- |\n| 1 | 2 |\n");
}
