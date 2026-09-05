namespace Docmd.Word.Tests;

using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using FluentAssertions;
using Xunit;

/// <summary>
/// A performance gate, because this repository twice shipped a costly change that every other
/// test was blind to.
/// </summary>
/// <remarks>
/// <para>
/// The style map work went in with fifteen new tests, a clean build and a full review, carrying
/// a 50% transform regression nobody measured. Separately, one match pattern with two chained
/// predicates made the whole transform quadratic, costing 124 seconds on a 1,000-paragraph
/// document until it was found by accident while investigating something else. No correctness
/// test can see either: the output was byte-identical both times.
/// </para>
/// <para>
/// The scaling assertion is the load-bearing one, and it is a ratio rather than a stopwatch
/// bound so it means the same thing on a slow CI box as on a fast laptop. Quadratic growth
/// shows up as 16x for a 4x input; linear as 4x. The threshold sits at 10x, which no linear
/// implementation approaches and no quadratic one survives.
/// </para>
/// <para>
/// Every transform runs under a wall-clock budget, and that is not belt-and-braces: when this
/// gate was verified by reintroducing the quadratic pattern on purpose, it did fail, but only
/// after more than ten minutes, because measuring a pathological transform means waiting for
/// it. A gate that slow gets deleted rather than fixed. The budget converts that into a
/// failure in seconds, and the large case is measured first so the catastrophic case reports
/// without doing the rest of the work.
/// </para>
/// </remarks>
public sealed class TransformScalingTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const int Budget = 25_000;

    private static string Composite(int paragraphs)
    {
        var sb = new StringBuilder();
        sb.Append($"""<docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}"><docmd:body><w:body>""");
        for (var i = 0; i < paragraphs; i++)
        {
            sb.Append($"""<w:p><w:r><w:t>Paragraph number {i} with some ordinary body text.</w:t></w:r></w:p>""");
        }

        sb.Append("""</w:body></docmd:body><docmd:styles><w:styles/></docmd:styles>""");
        sb.Append("""<docmd:numbering><w:numbering/></docmd:numbering><docmd:relationships/><docmd:properties/>""");
        sb.Append("""</docmd:package>""");
        return sb.ToString();
    }

    /// <summary>
    /// Runs one transform, failing rather than waiting if it overruns <see cref="Budget"/>.
    /// The overrunning transform is abandoned, not awaited: the engine may not check
    /// cancellation mid-transform, and a gate that depends on it would hang exactly when the
    /// thing it guards has broken.
    /// </summary>
    private static async Task<long> TimeOneAsync(XDocument composite, int paragraphs)
    {
        var sw = Stopwatch.StartNew();

        // Task.Run, not a bare call. The engine's TransformAsync does its work synchronously
        // and hands back an already-completed task, so invoking it directly blocks this thread
        // until the transform finishes -- and Task.WhenAny below would then have nothing left
        // to race. Verified the hard way: without this the budget never fired and the gate took
        // over five minutes to fail.
        var work = Task.Run(
            () => MarkdownTransform.RunAsync(composite, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        if (await Task.WhenAny(work, Task.Delay(Budget)).ConfigureAwait(false) != work)
        {
            Assert.Fail(
                $"Transforming {paragraphs} paragraphs of plain text exceeded {Budget / 1000}s. "
                + "It took about 3 seconds for 1000 when this gate was written, so this is a "
                + "catastrophic regression rather than drift. The known cause is chained "
                + "predicates in a match pattern: see "
                + "docs/engine-defects/2026-09-05-xslt-chained-predicates-in-match-patterns.md.");
        }

        await work.ConfigureAwait(false);
        return Math.Max(sw.ElapsedMilliseconds, 1);
    }

    private static async Task<long> MillisecondsForAsync(int paragraphs)
    {
        var composite = XDocument.Parse(Composite(paragraphs), LoadOptions.PreserveWhitespace);
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            best = Math.Min(best, await TimeOneAsync(composite, paragraphs).ConfigureAwait(false));
        }

        return best;
    }

    [Fact]
    public async Task TransformCost_GrowsNoWorseThanLinearly()
    {
        // Warm up small: the first transform in a process pays JIT, which at these sizes is
        // larger than the thing being measured.
        await MillisecondsForAsync(50);

        // Large first, so the catastrophic case fails without measuring anything else.
        var large = await MillisecondsForAsync(1000);
        var small = await MillisecondsForAsync(250);

        ((double)large / small).Should().BeLessThan(
            10.0,
            "a 4x larger document should cost about 4x more, and quadratic growth costs 16x. "
            + $"Measured {small} ms at 250 paragraphs and {large} ms at 1000. Chained predicates "
            + "in a match pattern are the known way to cause this: see "
            + "docs/engine-defects/2026-09-05-xslt-chained-predicates-in-match-patterns.md.");
    }
}
