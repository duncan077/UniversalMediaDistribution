using UniversalMediaDistribution.ContentFiltering.Parsing;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

public sealed class FilterListParserTests
{
    [Fact]
    public void ReadsAPlainBlockingRule()
    {
        var result = FilterListParser.Parse("||ads.example.com^");

        var rule = Assert.Single(result.NetworkRules);
        Assert.Equal("||ads.example.com^", rule.Pattern);
        Assert.Equal(ResourceType.Default, rule.Types);
        Assert.Equal(NetworkRuleFlags.None, rule.Flags);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void TopLevelDocumentsAreNotIncludedByDefault()
    {
        // A list that blocks a host means the resources it serves, not the
        // viewer's own navigation to it.
        var rule = Assert.Single(
            FilterListParser.Parse("||ads.example.com^").NetworkRules);

        Assert.Equal(ResourceType.None, rule.Types & ResourceType.Document);
    }

    [Fact]
    public void ReadsExceptionsAndOptions()
    {
        var result = FilterListParser.Parse(
            "@@||cdn.example.com/player.js$script,third-party,important");

        var rule = Assert.Single(result.NetworkRules);
        Assert.True(rule.IsException);
        Assert.Equal(ResourceType.Script, rule.Types);
        Assert.True(rule.Flags.HasFlag(NetworkRuleFlags.ThirdPartyOnly));
        Assert.True(rule.Flags.HasFlag(NetworkRuleFlags.Important));
    }

    [Theory]
    [InlineData("||example.com^$3p", NetworkRuleFlags.ThirdPartyOnly)]
    [InlineData("||example.com^$1p", NetworkRuleFlags.FirstPartyOnly)]
    [InlineData("||example.com^$~3p", NetworkRuleFlags.FirstPartyOnly)]
    public void PartyShorthandOptionsAreReadAsOptionsNotAsPattern(
        string line,
        NetworkRuleFlags expected)
    {
        // "$3p" and "$1p" begin with a digit. Treating that as part of the
        // pattern produces a rule that can never match, and uBlock Origin's
        // own lists use the shorthand.
        var rule = Assert.Single(FilterListParser.Parse(line).NetworkRules);

        Assert.Equal("||example.com^", rule.Pattern);
        Assert.True(rule.Flags.HasFlag(expected));
    }

    [Fact]
    public void NegatedTypesRemoveFromTheDefaultSet()
    {
        var rule = Assert.Single(
            FilterListParser.Parse("||example.com^$~script").NetworkRules);

        Assert.Equal(ResourceType.None, rule.Types & ResourceType.Script);
        Assert.NotEqual(ResourceType.None, rule.Types & ResourceType.Image);
    }

    [Fact]
    public void ReadsDomainScopesAndRedirects()
    {
        var rule = Assert.Single(
            FilterListParser
                .Parse("/ads.js$domain=a.com|~b.a.com,redirect=noopjs")
                .NetworkRules);

        Assert.Equal("a.com|~b.a.com", rule.DocumentDomains);
        Assert.Equal("noopjs", rule.RedirectResource);
        Assert.True(rule.Flags.HasFlag(NetworkRuleFlags.Redirect));
    }

    [Fact]
    public void ReadsRegularExpressionPatterns()
    {
        var rule = Assert.Single(
            FilterListParser.Parse("/banner\\d+\\.gif/").NetworkRules);

        Assert.True(rule.Flags.HasFlag(NetworkRuleFlags.Regex));
        Assert.Equal("banner\\d+\\.gif", rule.Pattern);
    }

    [Theory]
    [InlineData("example.com##.ad-banner", ".ad-banner", false, false)]
    [InlineData("example.com#@#.ad-banner", ".ad-banner", true, false)]
    [InlineData("##.generic-ad", ".generic-ad", false, false)]
    [InlineData("example.com#?#div:has-text(Ad)", "div:has-text(Ad)", false, true)]
    public void ReadsCosmeticRules(
        string line,
        string body,
        bool isException,
        bool isProcedural)
    {
        var rule = Assert.Single(
            FilterListParser.Parse(line).CosmeticRules);

        Assert.Equal(body, rule.Body);
        Assert.Equal(isException, rule.IsException);
        Assert.Equal(isProcedural, rule.IsProcedural);
        Assert.Equal(CosmeticRuleKind.Selector, rule.Kind);
    }

    [Fact]
    public void ReadsScriptletInvocations()
    {
        var rule = Assert.Single(
            FilterListParser
                .Parse("example.com##+js(set-constant, adsEnabled, false)")
                .CosmeticRules);

        Assert.Equal(CosmeticRuleKind.Scriptlet, rule.Kind);
        Assert.Equal("set-constant, adsEnabled, false", rule.Body);
        Assert.Equal("example.com", rule.Domains);
    }

    [Fact]
    public void APlainSelectorIsMarkedProceduralWhenItNeedsDomWork()
    {
        var rule = Assert.Single(
            FilterListParser.Parse("example.com##div:upward(2)").CosmeticRules);

        Assert.True(rule.IsProcedural);
    }

    [Fact]
    public void ANetworkRuleCarryingAFragmentIsNotACosmeticRule()
    {
        var result = FilterListParser.Parse("||example.com/page#section");

        Assert.Single(result.NetworkRules);
        Assert.Empty(result.CosmeticRules);
    }

    [Fact]
    public void UnsupportedOptionsDropTheRuleRatherThanHalfApplyIt()
    {
        var result = FilterListParser.Parse("||example.com^$removeparam=utm");

        Assert.Empty(result.NetworkRules);
        Assert.Equal(1, result.UnsupportedLines);
    }

    [Fact]
    public void APatternThatWouldMatchEverythingIsRefused()
    {
        var result = FilterListParser.Parse(
            string.Join('\n', ["*", "$third-party"]));

        Assert.Empty(result.NetworkRules);
        Assert.Equal(2, result.UnsupportedLines);
    }

    [Fact]
    public void CommentsAndBannersAreNotCountedAsSkipped()
    {
        var result = FilterListParser.Parse(
            string.Join(
                '\n',
                [
                    "[Adblock Plus 2.0]",
                    "! Title: Example list",
                    "! Expires: 4 days",
                    "",
                    "||ads.example.com^"
                ]));

        Assert.Single(result.NetworkRules);
        Assert.Equal(0, result.SkippedLines);
        Assert.Equal("Example list", result.Title);
        Assert.Equal(TimeSpan.FromDays(4), result.Expires);
    }

    [Fact]
    public void ReadsAnHourlyExpiryHint()
    {
        var result = FilterListParser.Parse("! Expires: 12 hours");

        Assert.Equal(TimeSpan.FromHours(12), result.Expires);
    }

    [Fact]
    public void ThePreprocessorSkipsBranchesThisHostDoesNotSatisfy()
    {
        var result = FilterListParser.Parse(
            string.Join(
                '\n',
                [
                    "!#if env_firefox",
                    "||firefox-only.example^",
                    "!#else",
                    "||everyone-else.example^",
                    "!#endif",
                    "||always.example^"
                ]));

        Assert.Equal(2, result.NetworkRules.Count);
        Assert.DoesNotContain(
            result.NetworkRules,
            rule => rule.Pattern.Contains("firefox-only"));
    }

    [Fact]
    public void AnUnknownConditionKeepsItsRulesRatherThanLosingThem()
    {
        var result = FilterListParser.Parse(
            string.Join(
                '\n',
                ["!#if cap_something_new", "||kept.example^", "!#endif"]));

        Assert.Single(result.NetworkRules);
    }

    [Fact]
    public void IncludeDirectivesAreReportedRatherThanApplied()
    {
        var result = FilterListParser.Parse("!#include easylist.part.txt");

        Assert.Equal("easylist.part.txt", Assert.Single(result.Includes));
    }

    [Fact]
    public void AMalformedLineDoesNotCostTheRestOfTheList()
    {
        var result = FilterListParser.Parse(
            string.Join(
                '\n',
                ["||first.example^", "example.com##", "||second.example^"]));

        Assert.Equal(2, result.NetworkRules.Count);
        Assert.Equal(1, result.MalformedLines);
    }
}
