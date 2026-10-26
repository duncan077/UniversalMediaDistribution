using System.Buffers.Binary;
using UniversalMediaDistribution.ContentFiltering.Compilation;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

public sealed class CompiledRulesetTests
{
    private static readonly string[] Lines =
    [
        "||ads.example.com^",
        "||tracker.example.org^$third-party",
        "@@||ads.example.com/needed.js",
        "/analytics.js$redirect=noopjs",
        "example.com##.ad-banner",
        "##.generic-ad"
    ];

    [Fact]
    public void ARulesetSurvivesARoundTrip()
    {
        var blob = FilterEngineFactory.Compile(out var report, null, Lines);

        Assert.True(CompiledRuleset.TryLoad(blob, out var ruleset));
        Assert.Equal(report.NetworkRules, ruleset.NetworkRuleCount);
        Assert.Equal(report.CosmeticRules, ruleset.CosmeticRuleCount);
        Assert.Equal(blob.Length, ruleset.ByteLength);

        var engine = new ContentFilterEngine(ruleset);
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://ads.example.com/x.js", "https://page.example/"));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act(
                "https://ads.example.com/needed.js",
                "https://page.example/"));
    }

    [Fact]
    public void AnUnknownFormatVersionIsRefusedRatherThanMisread()
    {
        var blob = FilterEngineFactory.Compile(out _, null, Lines);
        BinaryPrimitives.WriteInt32LittleEndian(
            blob.AsSpan(4),
            CompiledRuleset.FormatVersion + 1);

        Assert.False(CompiledRuleset.TryLoad(blob, out _));
    }

    [Fact]
    public void ATruncatedBlobIsRefused()
    {
        var blob = FilterEngineFactory.Compile(out _, null, Lines);

        Assert.False(CompiledRuleset.TryLoad(blob.AsMemory(0, 8), out _));
        Assert.False(
            CompiledRuleset.TryLoad(
                blob.AsMemory(0, blob.Length / 2),
                out _));
    }

    [Fact]
    public void AnEmptyRulesetLoads()
    {
        Assert.Equal(0, CompiledRuleset.Empty.NetworkRuleCount);
        Assert.Equal(0, CompiledRuleset.Empty.CosmeticRuleCount);
    }

    [Fact]
    public void IdenticalRulesAcrossListsAreStoredOnce()
    {
        FilterEngineFactory.Compile(
            out var report,
            null,
            ["||ads.example.com^", "||ads.example.com^"]);

        Assert.Equal(1, report.NetworkRules);
        Assert.Equal(1, report.DuplicateRules);
    }

    [Fact]
    public void TheConstrainedProfileDropsGenericAndProceduralCosmetics()
    {
        FilterEngineFactory.Compile(
            out var report,
            RulesetCompilerOptions.Constrained,
            [
                "##.generic-ad",
                "example.com##.host-ad",
                "example.com#?#div:has-text(Ad)"
            ]);

        // Only the host-scoped plain selector survives: generic hiding costs a
        // style recalculation against every element, and procedural selectors
        // cost DOM work on every mutation.
        Assert.Equal(1, report.CosmeticRules);
    }

    [Fact]
    public void NonAsciiPatternsAreDroppedRatherThanMiscompared()
    {
        FilterEngineFactory.Compile(
            out var report,
            null,
            ["||рекл.example^", "||ads.example^"]);

        Assert.Equal(1, report.NetworkRules);
        Assert.Equal(1, report.DroppedForCapacity);
    }
}
