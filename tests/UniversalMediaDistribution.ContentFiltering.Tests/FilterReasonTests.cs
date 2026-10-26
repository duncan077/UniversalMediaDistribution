namespace UniversalMediaDistribution.ContentFiltering.Tests;

/// <summary>
/// Every allow carries the reason it was allowed. Without it, a page whose
/// advertisements come from its own origin is indistinguishable from a ruleset
/// that never downloaded — both look like "filtering is not working".
/// </summary>
public sealed class FilterReasonTests
{
    private const string PlayerPage = "https://player.host.example/embed/1";

    private static ContentFilterEngine Engine() =>
        FilterEngineFactory.Build("||ads.example^", "@@||ads.example/ok.js");

    private static FilterReason Reason(
        string url,
        string? document = PlayerPage,
        ResourceType type = ResourceType.Script) =>
        Engine().Match(new FilterRequest(
            url.AsSpan(),
            document.AsSpan(),
            type)).Reason;

    [Fact]
    public void ThePagesOwnSiteIsReportedAsSuch()
    {
        // The number that explains a host serving its own advertising.
        Assert.Equal(
            FilterReason.SameSite,
            Reason("https://player.host.example/promo.js"));
        Assert.Equal(
            FilterReason.SameSite,
            Reason("https://cdn.player.host.example/promo.js"));
    }

    [Fact]
    public void MediaIsReportedSeparatelyFromTheSameSiteAllowance()
    {
        Assert.Equal(
            FilterReason.Media,
            Reason(
                "https://ads.example/hls/segment-1.ts",
                type: ResourceType.Media));
    }

    [Fact]
    public void ABlockedRequestNamesTheRule()
    {
        Assert.Equal(FilterReason.Rule, Reason("https://ads.example/tag.js"));
    }

    [Fact]
    public void AnExceptionIsDistinguishedFromNoRuleAtAll()
    {
        Assert.Equal(
            FilterReason.Exception,
            Reason("https://ads.example/ok.js"));
        Assert.Equal(
            FilterReason.NoRule,
            Reason("https://unrelated.example/app.js"));
    }

    [Fact]
    public void AnUnfilterableSchemeIsNotCountedAsAnAllowance()
    {
        Assert.Equal(
            FilterReason.UnsupportedScheme,
            Reason("data:text/plain,advert"));
    }

    [Fact]
    public void TheReasonDoesNotChangeTheVerdict()
    {
        // Two allows are the same outcome however they were reached, so the
        // explanation must not leak into equality.
        Assert.Equal(
            FilterDecision.Allow,
            FilterDecision.Allowed(FilterReason.SameSite));
    }
}
