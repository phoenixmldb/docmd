namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core;
using Xunit;

public sealed class SluggerTests
{
    [Theory]
    [InlineData("Q3 Findings", "q3-findings")]
    [InlineData("  Leading and trailing  ", "leading-and-trailing")]
    [InlineData("Punctuation: removed!", "punctuation-removed")]
    [InlineData("Multiple   spaces", "multiple-spaces")]
    [InlineData("Hyphen-already", "hyphen-already")]
    public void Slug_NormalisesText(string input, string expected)
        => new Slugger().Slug(input).Should().Be(expected);

    [Fact]
    public void Slug_DeduplicatesRepeats()
    {
        var slugger = new Slugger();

        slugger.Slug("Overview").Should().Be("overview");
        slugger.Slug("Overview").Should().Be("overview-1");
        slugger.Slug("Overview").Should().Be("overview-2");
    }

    [Fact]
    public void Slug_FallsBackWhenNothingSurvivesNormalisation()
    {
        // A heading of only punctuation must still get a stable, unique anchor rather
        // than an empty string, or two such headings would collide silently.
        var slugger = new Slugger();

        slugger.Slug("***").Should().Be("section");
        slugger.Slug("###").Should().Be("section-1");
    }

    [Fact]
    public void Slug_IsCultureInvariant()
    {
        // Turkish lowercases 'I' to dotless 'i'. Without an invariant culture the same
        // document would slug differently on a Turkish machine, breaking determinism.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            new Slugger().Slug("INDEX").Should().Be("index");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
