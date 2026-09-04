namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class MarkdownEscaperTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("a*b", @"a\*b")]
    [InlineData("file_name_here", @"file\_name\_here")]
    [InlineData("a|b", @"a\|b")]
    [InlineData("[bracket]", @"\[bracket\]")]
    [InlineData("back`tick", @"back\`tick")]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("<tag>", @"\<tag\>")]
    public void EscapeInline_EscapesMarkupCharacters(string input, string expected)
        => MarkdownEscaper.EscapeInline(input).Should().Be(expected);

    [Theory]
    [InlineData("# not a heading", @"\# not a heading")]
    [InlineData("> not a quote", @"\> not a quote")]
    [InlineData("- not a list", @"\- not a list")]
    [InlineData("+ not a list", @"\+ not a list")]
    [InlineData("1. not a list", @"1\. not a list")]
    [InlineData("ordinary text", "ordinary text")]
    [InlineData("mid # hash is fine", "mid # hash is fine")]
    public void EscapeLineStart_EscapesOnlyLeadingBlockMarkers(string input, string expected)
        => MarkdownEscaper.EscapeLineStart(input).Should().Be(expected);

    [Fact]
    public void CodeSpan_LeavesContentUnescaped()
        => MarkdownEscaper.CodeSpan("a*b_c").Should().Be("`a*b_c`");

    [Fact]
    public void CodeSpan_WidensTheFenceWhenContentHasBackticks()
        // CommonMark: a span containing a backtick is delimited by a longer run, with
        // padding spaces so the inner backtick is not consumed by the fence.
        => MarkdownEscaper.CodeSpan("a`b").Should().Be("`` a`b ``");

    [Fact]
    public void EscapeUrl_EncodesSpacesAndParentheses()
        => MarkdownEscaper.EscapeUrl("img/my report/fig (1).png")
            .Should().Be("img/my%20report/fig%20%281%29.png");
}
