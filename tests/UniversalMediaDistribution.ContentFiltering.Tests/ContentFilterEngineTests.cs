namespace UniversalMediaDistribution.ContentFiltering.Tests;

public sealed class ContentFilterEngineTests
{
    private const string PlayerPage =
        "https://player.video-host.example/embed/123";

    [Fact]
    public void AnEmptyRulesetAllowsEverything()
    {
        var engine = new ContentFilterEngine();

        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://ads.example.com/banner.js"));
    }

    [Theory]
    [InlineData("https://doubleclick.net/tag.js")]
    [InlineData("https://securepubads.g.doubleclick.net/tag/js/gpt.js")]
    public void HostAnchorsMatchTheHostAndItsSubdomains(string url)
    {
        var engine = FilterEngineFactory.Build("||doubleclick.net^");

        Assert.Equal(FilterAction.Block, engine.Act(url, PlayerPage));
    }

    [Fact]
    public void HostAnchorsStopAtALabelBoundary()
    {
        // The separator "^" does not match a dot, which is what keeps a
        // look-alike host from inheriting a rule written for another one.
        var engine = FilterEngineFactory.Build("||doubleclick.net^");

        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://doubleclick.net.example.org/player.js"));
    }

    [Fact]
    public void ASeparatorAlsoMatchesTheEndOfTheUrl()
    {
        var engine = FilterEngineFactory.Build("||ads.example^");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://ads.example", PlayerPage));
    }

    [Fact]
    public void ALabelRuleMatchesAnyLabelOfTheHost()
    {
        var engine = FilterEngineFactory.Build("||tracker.", "||tracker^");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://tracker.metrics.example/collect", PlayerPage));
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://cdn.tracker.example/x.js", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://notatracker.example/x.js", PlayerPage));
    }

    [Fact]
    public void SubstringPatternsMatchAnywhereInTheUrl()
    {
        var engine = FilterEngineFactory.Build("/pagead/");

        Assert.Equal(
            FilterAction.Block,
            engine.Act(
                "https://cdn.example.org/pagead/js/adsbygoogle.js",
                PlayerPage));
    }

    [Fact]
    public void LeadingAndTrailingAnchorsPinTheWholeUrl()
    {
        var engine = FilterEngineFactory.Build(
            "|https://exact.example/ad|");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://exact.example/ad", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://exact.example/ad/more", PlayerPage));
    }

    [Fact]
    public void WildcardsSpanArbitraryText()
    {
        var engine = FilterEngineFactory.Build("||example.org/a*/banner.png");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://example.org/assets/deep/banner.png", PlayerPage));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveUnlessTheRuleAsksOtherwise()
    {
        var engine = FilterEngineFactory.Build("||ADS.Example.ORG^");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://ads.example.org/tag.js", PlayerPage));
    }

    [Fact]
    public void ATrailingDotOnTheHostDoesNotEvadeARule()
    {
        var engine = FilterEngineFactory.Build("||ads.example.org^");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://ads.example.org./tag.js", PlayerPage));
    }

    [Fact]
    public void ResourceTypesConstrainARule()
    {
        var engine = FilterEngineFactory.Build("||example.org/x$script");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://example.org/x", PlayerPage, ResourceType.Script));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://example.org/x", PlayerPage, ResourceType.Image));
    }

    [Fact]
    public void ThirdPartyRulesLeaveFirstPartyRequestsAlone()
    {
        var engine = FilterEngineFactory.Build("||shared.example^$third-party");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://shared.example/x.js", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act(
                "https://shared.example/x.js",
                "https://other.shared.example/page"));
    }

    [Fact]
    public void DocumentScopesConstrainARuleToTheirPages()
    {
        var engine = FilterEngineFactory.Build(
            "/count.js$domain=allowed.example|~inner.allowed.example");

        Assert.Equal(
            FilterAction.Block,
            engine.Act(
                "https://third.example/count.js",
                "https://allowed.example/page"));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act(
                "https://third.example/count.js",
                "https://inner.allowed.example/page"));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act(
                "https://third.example/count.js",
                "https://elsewhere.example/page"));
    }

    [Fact]
    public void DenyAllowStepsARuleAsideForNamedHosts()
    {
        var engine = FilterEngineFactory.Build(
            "/track.js$denyallow=safe.example");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://ads.example/track.js", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://safe.example/track.js", PlayerPage));
    }

    [Fact]
    public void AnExceptionOutranksABlock()
    {
        var engine = FilterEngineFactory.Build(
            "||example.org^",
            "@@||example.org/needed.js");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://example.org/ad.js", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://example.org/needed.js", PlayerPage));
    }

    [Fact]
    public void ImportantOutranksAnException()
    {
        var engine = FilterEngineFactory.Build(
            "||example.org/ad.js$important",
            "@@||example.org^");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://example.org/ad.js", PlayerPage));
    }

    [Fact]
    public void ABadFilterReplacementRuleStillTakesEffect()
    {
        // The shape uBlock Origin's unbreak list actually uses: retire a
        // blanket rule and put a narrower one in its place. Both halves have
        // to work, or the host ends up unfiltered.
        var engine = FilterEngineFactory.Build(
            "||ads.example^",
            "||ads.example^$badfilter",
            "||ads.example^$3p");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://a.ads.example/tag.js", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act(
                "https://a.ads.example/tag.js",
                "https://ads.example/page"));
    }

    [Fact]
    public void BadFilterRemovesTheRuleItNames()
    {
        var engine = FilterEngineFactory.Build(
            "||example.org/ad.js",
            "||example.org/ad.js$badfilter");

        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://example.org/ad.js", PlayerPage));
    }

    [Fact]
    public void RedirectRulesNameASurrogate()
    {
        var engine = FilterEngineFactory.Build(
            "||example.org/analytics.js$redirect=noopjs");

        Assert.Equal(
            FilterAction.Redirect,
            engine.Act("https://example.org/analytics.js", PlayerPage));
        Assert.Equal(
            "noopjs",
            engine.Redirect("https://example.org/analytics.js", PlayerPage));
    }

    [Fact]
    public void RegularExpressionRulesMatch()
    {
        var engine = FilterEngineFactory.Build("/banner\\d+\\.gif/");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://example.org/banner42.gif", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://example.org/banner.gif", PlayerPage));
    }

    [Fact]
    public void SchemesFilterListsDoNotDescribeAreLeftAlone()
    {
        var engine = FilterEngineFactory.Build("||example.org^", "/advert");

        Assert.Equal(
            FilterAction.Allow,
            engine.Act("data:text/plain,advertisement", PlayerPage));
        Assert.Equal(FilterAction.Allow, engine.Act("about:blank", PlayerPage));
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("blob:https://example.org/abc", PlayerPage));
    }

    [Fact]
    public void CosmeticsAreScopedToTheirHosts()
    {
        var engine = FilterEngineFactory.Build(
            "##.generic-ad",
            "video-host.example##.host-ad",
            "other.example##.other-ad",
            "video-host.example##+js(prevent-window-open)");

        var payload = engine.GetCosmetics("player.video-host.example");

        // Kept apart: a host's own rules are few and apply wherever its page
        // runs, while the generic set is large enough that it only goes into
        // the top document.
        Assert.Contains(".generic-ad", payload.GenericSelectors);
        Assert.Contains(".host-ad", payload.Selectors);
        Assert.DoesNotContain(".generic-ad", payload.Selectors);
        Assert.DoesNotContain(".other-ad", payload.Selectors);
        Assert.DoesNotContain(".other-ad", payload.GenericSelectors);
        Assert.Contains("prevent-window-open", payload.Scriptlets);
    }

    [Fact]
    public void ACosmeticExceptionRemovesTheSelectorItNames()
    {
        var engine = FilterEngineFactory.Build(
            "##.generic-ad",
            "video-host.example#@#.generic-ad");

        Assert.DoesNotContain(
            ".generic-ad",
            engine.GetCosmetics("video-host.example").GenericSelectors);
        Assert.Contains(
            ".generic-ad",
            engine.GetCosmetics("elsewhere.example").GenericSelectors);
    }
}
