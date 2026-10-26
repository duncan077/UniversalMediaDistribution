using System.Text.RegularExpressions;
using UniversalMediaDistribution.ContentFiltering.Matching;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

public sealed class RegexRuleCacheTests
{
    [Fact]
    public void EnginePreservesRegexBlockingExceptionsAndResourceScopes()
    {
        var engine = FilterEngineFactory.Build(
            "/ads[0-9]+\\.example/$script",
            "@@/ads123\\.example/$script");
        Assert.Equal(FilterAction.Block, engine.Act("https://ads456.example/tag", type: ResourceType.Script));
        Assert.Equal(FilterAction.Allow, engine.Act("https://ads123.example/tag", type: ResourceType.Script));
        Assert.Equal(FilterAction.Allow, engine.Act("https://ads456.example/tag", type: ResourceType.Image));
        Assert.Equal(FilterAction.Allow, engine.Act("https://player.example/tag", type: ResourceType.Script));
    }

    [Fact]
    public void UnusedAndNonmatchingRulesDoNotAllocateSymbolicMatchers()
    {
        var reads = 0;
        var cache = new RegexRuleCache(index => { reads++; return ($"tracker{index}[0-9]+", false); });
        Assert.Equal(0, reads);
        for (var index = 0; index < 200; index++)
            Assert.False(cache.IsMatch(index, "https://video.example/player"));
        Assert.Equal(200, reads);
        Assert.Equal(0, cache.MatcherCount);
        for (var index = 0; index < 200; index++)
            Assert.False(cache.IsMatch(index, "https://video.example/player"));
        Assert.Equal(200, reads);
    }

    [Fact]
    public void EvictionBoundsMemoryWithoutLosingMatches()
    {
        var cache = new RegexRuleCache(index => ($"tracker{index}[0-9]+", false));
        for (var cycle = 0; cycle < 3; cycle++)
            for (var index = 0; index < RegexRuleCache.Capacity * 3; index++)
            {
                Assert.True(cache.IsMatch(index, $"https://tracker{index}123.example/ad"));
                Assert.InRange(cache.MatcherCount, 1, RegexRuleCache.Capacity);
            }
    }

    [Theory]
    [InlineData("tracker[0-9]+", "TRACKER123", false)]
    [InlineData("tracker[0-9]+", "TRACKER123", true)]
    [InlineData("a|abc", "abc", true)]
    [InlineData("^a+$", "aaa\n", false)]
    [InlineData("(a+)+$", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!", true)]
    [InlineData("(?i:abc)", "ABC", true)]
    public void MatchesTheOriginalNonBacktrackingSemantics(string pattern, string input, bool matchCase)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (!matchCase) options |= RegexOptions.IgnoreCase;
        var expected = new Regex(pattern, options).IsMatch(input);
        var cache = new RegexRuleCache(_ => (pattern, matchCase));
        Assert.Equal(expected, cache.IsMatch(0, input));
        Assert.Equal(expected, cache.IsMatch(0, input));
    }

    [Fact]
    public void PathologicalPrefilterFallsBackToLinearMatcher()
    {
        var cache = new RegexRuleCache(_ => ("(a+)+$", true));
        Assert.False(cache.IsMatch(0, new string('a', 10000) + "!"));
        Assert.Equal(1, cache.MatcherCount);
        Assert.True(cache.IsMatch(0, new string('a', 10000)));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("(a)\\1")]
    [InlineData("(?=a)a")]
    public void InvalidOrUnsupportedPatternsRemainNonmatching(string pattern)
    {
        var cache = new RegexRuleCache(_ => (pattern, false));
        Assert.False(cache.IsMatch(0, "aa"));
        Assert.False(cache.IsMatch(0, "aa"));
    }

    [Fact]
    public void ConcurrentRequestsKeepCacheBoundedAndDecisionsConsistent()
    {
        var cache = new RegexRuleCache(index => ($"tracker{index}[0-9]+", false));
        Parallel.For(0, 256, index =>
        {
            var rule = index % 32;
            Assert.True(cache.IsMatch(rule, $"tracker{rule}123"));
            Assert.False(cache.IsMatch(rule, "player.example"));
        });
        Assert.InRange(cache.MatcherCount, 1, RegexRuleCache.Capacity);
    }
}
