using UniversalMediaDistribution.ContentFiltering.Compilation;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

/// <summary>
/// The token index is the one place where an optimization can silently cost
/// correctness: a rule filed under a token the URL never emits is a rule that
/// never fires, and nothing else in the system would notice. Every pattern
/// shape the compiler can produce is checked here against a URL it must match.
/// </summary>
public sealed class TokenIndexTests
{
    /// <summary>
    /// Pairs of (filter, a URL that filter has to match). Compiled together so
    /// each rule has to be found through a populated index rather than in
    /// isolation.
    /// </summary>
    public static TheoryData<string, string> Corpus =>
        new()
        {
            { "||googlesyndication.com^", "https://pagead2.googlesyndication.com/pagead/js/x.js" },
            { "||doubleclick.net^", "https://securepubads.g.doubleclick.net/tag/js/gpt.js" },
            { "||ads.example.org^", "https://ads.example.org/tag.js" },
            { "||exoclick.com^", "https://anuncios.exoclick.com/tag" },
            { "||a.example^", "https://a.example/x" },
            { "||tracker.", "https://tracker.metrics.example/collect" },
            { "||metrics^", "https://a.metrics/collect" },
            { "/pagead/", "https://cdn.example.org/pagead/js/adsbygoogle.js" },
            { "/adframe", "https://static.example.org/adframe.html" },
            { "zoneid=", "https://serve.example.org/banner.php?zoneid=42" },
            { "aff_id=", "https://partner.example.org/click?aff_id=99" },
            { "adsbygoogle", "https://cdn.example.org/adsbygoogle.js" },
            { "-advertisement-", "https://cdn.example.org/x-advertisement-y.png" },
            { "||example.org/a*/banner.png", "https://example.org/assets/deep/banner.png" },
            { "|https://exact.example/ad|", "https://exact.example/ad" },
            { "||example.org^$script", "https://example.org/x.js" },
            { "||verylongadvertisinghostname.example^", "https://verylongadvertisinghostname.example/x" },
            { "^popunder^", "https://cdn.example.org/a/popunder/b" },
            { "||example.org/%2Fads", "https://example.org/%2Fads/x" },
            { "||xn--80ak6aa92e.example^", "https://xn--80ak6aa92e.example/x" }
        };

    private const string Document = "https://player.unrelated.example/embed";

    private static string[] AllFilters()
    {
        var lines = new List<string>();
        foreach (var row in Corpus)
        {
            lines.Add((string)row[0]);
        }

        return [.. lines];
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryRuleIsReachableThroughItsToken(string filter, string url)
    {
        // On its own the rule can only be found through its own bucket, and
        // alongside every other rule it has to still be found through it.
        Assert.Equal(
            FilterAction.Block,
            FilterEngineFactory.Build(filter)
                .Act(url, Document, ResourceType.Script));

        Assert.Equal(
            FilterAction.Block,
            FilterEngineFactory.Build(AllFilters())
                .Act(url, Document, ResourceType.Script));
    }

    [Fact]
    public void MostRulesAreIndexedRatherThanEvaluatedForEveryRequest()
    {
        FilterEngineFactory.Compile(out var report, null, AllFilters());

        // A rule with no indexable token costs every request. A handful is
        // expected; a majority would mean the analyzer has stopped working.
        Assert.True(
            report.AlwaysEvaluatedRules * 4 < report.NetworkRules,
            $"{report.AlwaysEvaluatedRules} of {report.NetworkRules} rules " +
            "are evaluated for every request.");
    }

    [Fact]
    public void TheAlwaysEvaluatedBucketIsCapped()
    {
        var lines = new List<string>();
        for (var index = 0; index < 200; index++)
        {
            // Patterns whose only token run sits at an unanchored start have
            // nothing the index can key on.
            lines.Add($"adhost{index}*x");
        }

        FilterEngineFactory.Compile(
            out var report,
            RulesetCompilerOptions.Constrained with
            {
                MaxAlwaysEvaluatedRules = 16
            },
            [.. lines]);

        Assert.Equal(16, report.AlwaysEvaluatedRules);
        Assert.Equal(184, report.DroppedForCapacity);
    }

    [Fact]
    public void ExceptionsSurviveTheAlwaysEvaluatedCap()
    {
        var lines = new List<string> { "@@keepthisexception*x" };
        for (var index = 0; index < 50; index++)
        {
            lines.Add($"adhost{index}*x");
        }

        FilterEngineFactory.Compile(
            out var report,
            RulesetCompilerOptions.Standard with
            {
                MaxAlwaysEvaluatedRules = 1
            },
            [.. lines]);

        // Losing a blocking rule costs coverage; losing the exception that
        // keeps a page working costs the page.
        Assert.Equal(1, report.AlwaysEvaluatedRules);
        Assert.Equal(50, report.DroppedForCapacity);
    }
}
