namespace Ooxml.Md.Core.Tests;

using System.Xml.Linq;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class MarkdownSerializerTests
{
    // PreserveWhitespace, matching MarkdownTransform: the serialiser's real input keeps
    // whitespace-only text nodes, and a test helper that silently drops them cannot
    // express -- let alone pin -- what the serialiser does with a space.
    private static string Serialize(string inner)
        => MarkdownSerializer.Serialize(XDocument.Parse(
            $"""<md:document xmlns:md="https://phoenixml.dev/docmd/md">{inner}</md:document>""",
            LoadOptions.PreserveWhitespace));

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
    public void AdjacentDifferingFormatting_DoesNotMerge()
        // Task 14 review: MergeAdjacentMarkup (added to rejoin a word Word's own
        // spell-check split across two same-formatted runs) gates on the element name
        // matching. Bold directly abutting italic, with nothing between them, must stay
        // two separate spans -- merging across different formatting would silently change
        // meaning, not just tidy punctuation.
        => Serialize("""<md:para><md:strong><md:text>bold</md:text></md:strong><md:em><md:text>italic</md:text></md:em></md:para>""")
            .Should().Be("**bold***italic*\n");

    [Fact]
    public void HardBreak_AtEndOfParagraph_TrailingSpacesSurviveTrimming()
        // Task 14 review: TrimTrailingHorizontalWhitespace (added to drop the stray space
        // Word/LibreOffice leaves before a paragraph mark) must stop at the '\n' a hard
        // break emits rather than eating into it. TrimEnd(' ', '\t') halts at the first
        // non-matching character scanning from the end, and md:br's own output ends in
        // '\n' -- not a space -- so the two spaces immediately before it are never reached.
        => Serialize("""<md:para><md:text>Steps:</md:text><md:br/></md:para>""")
            .Should().Be("Steps:  \n");

    [Fact]
    public void CodeSpan_TrailingSpaceInsideBackticksSurvivesParagraphTrimming()
        // Task 14 review: a code span's content is never escaped or altered (that is its
        // whole purpose), and CodeSpan's own closing fence is a backtick, not a space, so
        // even when the code span is the last thing in a paragraph, the paragraph-level
        // trailing-whitespace trim must not reach past that backtick and eat a trailing
        // space that is meaningful *inside* the span.
        => Serialize("""<md:para><md:text>Run: </md:text><md:code><md:text>ls -la </md:text></md:code></md:para>""")
            .Should().Be("Run: `ls -la `\n");

    [Fact]
    public void Strong_WithATrailingSpace_PutsTheSpaceOutsideTheDelimiters()
        // CommonMark closes an emphasis run only when the closing delimiter is
        // right-flanking. "**bold **and more" is therefore not bold at all -- it renders
        // as literal asterisks. Selecting a word together with its trailing space before
        // pressing Ctrl+B is everyday Word behaviour, so this arrives constantly.
        => Serialize("""<md:para><md:strong><md:text>bold </md:text></md:strong><md:text>and more</md:text></md:para>""")
            .Should().Be("**bold** and more\n");

    [Fact]
    public void Em_WithALeadingSpace_PutsTheSpaceOutsideTheDelimiters()
        // The opening delimiter has the mirror-image requirement: it must be
        // left-flanking, so "see* note*" is not italic either.
        => Serialize("""<md:para><md:text>see</md:text><md:em><md:text> note</md:text></md:em></md:para>""")
            .Should().Be("see *note*\n");

    [Fact]
    public void Strong_WithSpacesOnBothSides_KeepsBothOutside()
        => Serialize("""<md:para><md:text>a</md:text><md:strong><md:text> bold </md:text></md:strong><md:text>b</md:text></md:para>""")
            .Should().Be("a **bold** b\n");

    [Fact]
    public void Strong_ContainingOnlyWhitespace_EmitsNoDelimitersAtAll()
        // "**" around nothing is four literal asterisks in the reader's face, and an
        // emphasis with no content has no meaning to lose by dropping it.
        => Serialize("""<md:para><md:text>a</md:text><md:strong><md:text> </md:text></md:strong><md:text>b</md:text></md:para>""")
            .Should().Be("a b\n");

    [Fact]
    public void Blockquote_PrefixesEveryLine()
        => Serialize("""<md:blockquote><md:para><md:text>Caution.</md:text></md:para></md:blockquote>""")
            .Should().Be("> Caution.\n");

    [Fact]
    public void Blockquote_BlankSeparatorLineCarriesABareMarker()
        // The separator between two paragraphs of one quote used to be "> " -- a trailing
        // space, invisible, stripped by any editor that trims on save (which makes a
        // re-conversion look like a diff against nothing) and one keystroke away from
        // being a hard break. It cannot simply be blank the way a list item's separator
        // is: an empty line TERMINATES a blockquote, splitting one quote into two.
        => Serialize("""<md:blockquote><md:para><md:text>A</md:text></md:para><md:para><md:text>B</md:text></md:para></md:blockquote>""")
            .Should().Be("> A\n>\n> B\n");

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
    public void EmptyListItem_KeepsItsMarkerButNotTheSpaceAfterIt()
        // The marker must stay -- dropping it would delete the item -- but "- " on an
        // otherwise blank line is trailing whitespace. "-" alone is a valid empty item.
        => Serialize("""<md:list ordered="false"><md:item/><md:item><md:para><md:text>Two</md:text></md:para></md:item></md:list>""")
            .Should().Be("-\n- Two\n");

    [Fact]
    public void EmptyOrderedListItem_KeepsItsNumberButNotTheSpaceAfterIt()
        => Serialize("""<md:list ordered="true"><md:item/><md:item><md:para><md:text>Two</md:text></md:para></md:item></md:list>""")
            .Should().Be("1.\n2. Two\n");

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

    [Fact]
    public void Emphasis_OnPunctuationAlone_IsDropped()
        // Word leaves a full stop italic when the sentence before it was italicised, which is
        // an artifact of selection rather than authorial intent. Markdown cannot express it:
        // "*.*" is fine alone but breaks the moment it touches a non-space character, and the
        // asterisks then survive as literal text. The words matter; the italic full stop does
        // not. Found by the text-preservation oracle on a real 2008 program guide, which lost
        // 3,440 of 4,664 words to this one pattern.
        => Serialize("""<md:para><md:em><md:text>.</md:text></md:em></md:para>""")
            .Should().Be(".\n");

    [Fact]
    public void Emphasis_AfterAnotherSpan_SurvivesWhenItHasWords()
        // The narrow rule matters: adjacent emphasis is NOT broken in general, so this must
        // keep its markup. Only content with no letter or digit is dropped.
        => Serialize("""
            <md:para><md:strong><md:text>bold</md:text></md:strong><md:em><md:text>italic</md:text></md:em></md:para>
            """)
            .Should().Be("**bold***italic*\n");

    [Fact]
    public void StrongFollowedByAnItalicFullStop_KeepsTheFullStop()
    {
        // The exact real-world shape. Emitting "**newsletter***.*" renders as
        // "newsletter*.*" -- the asterisks visible, the sentence corrupted.
        var markdown = Serialize("""
            <md:para><md:strong><md:text>newsletter</md:text></md:strong><md:em><md:text>.</md:text></md:em></md:para>
            """);

        markdown.Should().Be("**newsletter**.\n");
    }

    [Fact]
    public void Emphasis_OnASymbolAlone_IsDropped()
        => Serialize("""<md:para><md:strong><md:text>&#8594;</md:text></md:strong></md:para>""")
            .Should().Be("\u2192\n");

    [Fact]
    public void Emphasis_OnADigit_IsKept()
        // "no letters or digits", not "no letters": a bold figure is meaningful.
        => Serialize("""<md:para><md:strong><md:text>5</md:text></md:strong></md:para>""")
            .Should().Be("**5**\n");
}
