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
}
