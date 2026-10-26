using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.ContentFiltering.Compilation;
using UniversalMediaDistribution.ContentFiltering.Parsing;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class EmbeddedWebFilterSessionTests
{
    private static readonly Uri PlayerPage =
        new("https://player.video-host.example/embed/123");

    private static EmbeddedWebFilterSession Session() =>
        new(EmbeddedWebAdBlocker.Engine);

    [Fact]
    public void ASessionWithNoOpenPageIsInactive()
    {
        var session = Session();

        Assert.False(session.IsActive);
        Assert.Null(session.DocumentUri);
    }

    [Fact]
    public void OpeningAPageMakesTheDocumentAvailableToTheHook()
    {
        var session = Session();

        session.Open(PlayerPage);

        Assert.True(session.IsActive);
        Assert.Equal(PlayerPage, session.DocumentUri);

        session.Close();

        Assert.False(session.IsActive);
    }

    [Fact]
    public void TheSessionAppliesTheSeedRulesToRequests()
    {
        var session = Session();
        session.Open(PlayerPage);

        Assert.Equal(
            FilterAction.Block,
            session.Match(
                "https://pagead2.googlesyndication.com/pagead/js/x.js",
                ResourceType.Script).Action);
    }

    [Fact]
    public void ThePagesOwnSiteStaysAllowedThroughTheSession()
    {
        var session = Session();
        session.Open(PlayerPage);

        Assert.Equal(
            FilterAction.Allow,
            session.Match(
                "https://cdn.player.video-host.example/chunk.js",
                ResourceType.Script).Action);
    }

    [Fact]
    public void MediaStaysAllowedThroughTheSession()
    {
        var session = Session();
        session.Open(PlayerPage);

        Assert.Equal(
            FilterAction.Allow,
            session.Match(
                "https://ads.example.org/hls/segment-00042.ts",
                ResourceType.Media).Action);
    }

    [Fact]
    public void PassthroughKeepsObservationWithoutChangingThePage()
    {
        var session = Session();

        session.OpenPassthrough(
            new Uri("https://nupload.top/watch/signed-session"));
        var decision = session.Match(
            "https://pagead2.googlesyndication.com/pagead/js/x.js",
            ResourceType.Script);

        Assert.True(session.IsActive);
        Assert.True(session.IsPassthrough);
        Assert.Equal(FilterAction.Allow, decision.Action);
        Assert.Equal(1, session.RequestsSeen);
        Assert.Equal(0, session.RequestsBlocked);
        Assert.Empty(session.UnmatchedHosts);
        Assert.Equal(";", session.BuildInjectionPayload("nupload.top"));
    }

    [Fact]
    public void ARegularOpenAfterPassthroughRestoresFiltering()
    {
        var session = Session();
        session.OpenPassthrough(
            new Uri("https://nupload.top/watch/signed-session"));

        session.Close();
        session.Open(PlayerPage);

        Assert.False(session.IsPassthrough);
        Assert.Equal(
            FilterAction.Block,
            session.Match(
                "https://pagead2.googlesyndication.com/pagead/js/x.js",
                ResourceType.Script).Action);
        Assert.NotEqual(
            ";",
            session.BuildInjectionPayload("video-host.example"));
    }

    [Fact]
    public void NuploadCompatibilityBlocksItsAdsWithoutChangingThePage()
    {
        var session = Session();
        session.OpenCompatibilityLimited(
            new Uri("https://nupload.top/watch/signed-session"),
            EmbeddedWebAdBlocker.NuploadCompatibilityEngine);

        Assert.True(session.IsActive);
        Assert.False(session.IsPassthrough);
        Assert.True(session.IsCompatibilityLimited);
        Assert.True(session.IsPageInjectionSuppressed);
        Assert.Equal(
            FilterAction.Block,
            session.Match(
                "https://acscdn.com/script/aclib.js",
                ResourceType.Script).Action);
        Assert.Equal(
            FilterAction.Block,
            session.Match(
                "https://js.wpadmngr.com/static/adManager.js",
                ResourceType.Script).Action);
        Assert.Equal(
            FilterAction.Allow,
            session.Match(
                "https://ssl.p.jwpcdn.com/player/v/8.38.3/jwplayer.js",
                ResourceType.Script).Action);
        Assert.Equal(
            FilterAction.Allow,
            session.Match(
                "https://sv2.ibra.lat/?s=signed-media",
                ResourceType.XmlHttpRequest).Action);
        Assert.Equal(";", session.BuildInjectionPayload("nupload.top"));
    }

    [Fact]
    public void CompatibilityFilteringRespectsTheOffPreference()
    {
        var session = new EmbeddedWebFilterSession(
            new ContentFilterEngine(CompiledRuleset.Empty));
        session.OpenCompatibilityLimited(
            new Uri("https://nupload.top/watch/signed-session"),
            EmbeddedWebAdBlocker.NuploadCompatibilityEngine);

        Assert.Equal(
            FilterAction.Allow,
            session.Match(
                "https://acscdn.com/script/aclib.js",
                ResourceType.Script).Action);
    }

    [Fact]
    public void ARegularOpenAfterCompatibilityRestoresFullInjection()
    {
        var session = Session();
        session.OpenCompatibilityLimited(
            new Uri("https://nupload.top/watch/signed-session"),
            EmbeddedWebAdBlocker.NuploadCompatibilityEngine);

        session.Close();
        session.Open(PlayerPage);

        Assert.False(session.IsCompatibilityLimited);
        Assert.False(session.IsPageInjectionSuppressed);
        Assert.NotEqual(
            ";",
            session.BuildInjectionPayload("video-host.example"));
    }

    [Fact]
    public void OpeningAPageTellsTheHooksWhichHostToBuildAPayloadFor()
    {
        // Cosmetic payloads are per host. Without this signal a hook would
        // keep serving the first server's rules to every server after it.
        var session = Session();
        var opened = new List<Uri>();
        session.DocumentOpened += opened.Add;

        session.Open(PlayerPage);
        session.Close();
        session.Open(new Uri("https://other-host.example/embed/9"));

        Assert.Equal(
            [PlayerPage, new Uri("https://other-host.example/embed/9")],
            opened);
    }

    [Fact]
    public void AcceptedTopLevelRedirectUsesTheServingHostAsTheDocument()
    {
        var session = Session();
        var servingPage =
            new Uri("https://rotating-player.example/e/123");
        session.Open(new Uri("https://stable-player.example/e/123"));
        _ = session.Match(
            "https://tracker.metrics.example/collect",
            ResourceType.Script);

        session.Navigate(servingPage);
        var ownPlayerScript = session.Match(
            "https://rotating-player.example/jwplayer/jwplayer.js",
            ResourceType.Script);

        Assert.Equal(servingPage, session.DocumentUri);
        Assert.Equal(FilterReason.SameSite, ownPlayerScript.Reason);
        Assert.Equal(FilterAction.Allow, ownPlayerScript.Action);
        Assert.Equal(2, session.RequestsSeen);
        Assert.Equal(1, session.RequestsBlocked);
    }

    [Fact]
    public void RedirectPreservesCompatibilityMode()
    {
        var session = Session();
        var servingPage =
            new Uri("https://nupload-cdn.example/watch/123");
        session.OpenCompatibilityLimited(
            new Uri("https://nupload.top/watch/123"),
            EmbeddedWebAdBlocker.NuploadCompatibilityEngine);

        session.Navigate(servingPage);

        Assert.Equal(servingPage, session.DocumentUri);
        Assert.True(session.IsCompatibilityLimited);
        Assert.True(session.IsPageInjectionSuppressed);
    }

    [Fact]
    public void RedirectRequiresAnOpenSession()
    {
        var session = Session();

        Assert.Throws<InvalidOperationException>(() =>
            session.Navigate(new Uri("https://player.example/e/123")));
    }

    [Fact]
    public void ThePayloadCarriesOnlyTheHostsOwnCosmetics()
    {
        var parsed = FilterListParser.Parse(string.Join(
            '\n',
            [
                "video-host.example##.host-ad",
                "other-host.example##.other-ad"
            ]));
        var session = new EmbeddedWebFilterSession(
            new ContentFilterEngine(CompiledRuleset.Load(
                RulesetCompiler.Compile(parsed, out _))));

        var payload = session.BuildInjectionPayload("video-host.example");

        Assert.Contains("\".host-ad\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("other-ad", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void HostsNoRuleMentionsAreNamed()
    {
        // The lists are large but not complete, and a small or regional
        // advertisement network simply is not in them. Naming the host is what
        // turns a surviving advertisement into a rule someone can write.
        var session = Session();
        session.Open(PlayerPage);

        session.Match(
            "https://obscure-ad-network.example/tag.js",
            ResourceType.Script);
        session.Match("https://cdn.jsdelivr.net/hls.js", ResourceType.Script);
        session.Match(
            "https://pagead2.googlesyndication.com/pagead/js/x.js",
            ResourceType.Script);

        var hosts = session.UnmatchedHosts;

        Assert.Contains("obscure-ad-network.example", hosts);
        Assert.Contains("cdn.jsdelivr.net", hosts);
        // The blocked one is not an unmatched host; it matched a rule.
        Assert.DoesNotContain("pagead2.googlesyndication.com", hosts);
    }

    [Fact]
    public void TheAllowanceBreakdownSeparatesItsCauses()
    {
        var session = Session();
        session.Open(PlayerPage);

        session.Match(
            "https://player.video-host.example/app.js",
            ResourceType.Script);
        session.Match(
            "https://cdn.example/hls/segment.ts",
            ResourceType.Media);
        session.Match(
            "https://pagead2.googlesyndication.com/pagead/js/x.js",
            ResourceType.Script);

        Assert.Equal(1, session.RequestsAllowedAsSameSite);
        Assert.Equal(1, session.RequestsAllowedAsMedia);
        Assert.Equal(1, session.RequestsBlocked);
        Assert.Equal(3, session.RequestsSeen);
    }

    [Fact]
    public void OpeningANewPageStartsTheDiagnosticsOver()
    {
        var session = Session();
        session.Open(PlayerPage);
        session.Match("https://unmatched.example/x.js", ResourceType.Script);

        session.Open(new Uri("https://other.example/embed"));

        Assert.Equal(0, session.RequestsSeen);
        Assert.Empty(session.UnmatchedHosts);
    }

    [Fact]
    public void TheInjectionPayloadCarriesTheRuntimeAndDefenses()
    {
        var payload = Session().BuildInjectionPayload("video-host.example");

        Assert.Contains("__umdFilterConfig", payload, StringComparison.Ordinal);
        Assert.Contains("__umdFilterRuntime", payload, StringComparison.Ordinal);
        Assert.Contains(
            "\"builtInDefenses\":true",
            payload,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFallbackInstallsSeedRequestWrappers()
    {
        var session = Session();
        session.Open(PlayerPage);
        Assert.DoesNotContain("__umdEmbeddedRequestBlockerV1",
            session.BuildInjectionPayload(PlayerPage.Host));
        var fallback = session.BuildFallbackInjectionPayload(PlayerPage.Host);
        Assert.StartsWith(EmbeddedWebAdBlocker.RequestBlockingScript, fallback);
        Assert.Contains("const requestOnly = true;", fallback);
        Assert.Contains("__umdFilterRuntime", fallback);
    }

    [Fact]
    public void TheFallbackDoesNotRestoreNetworkBlockingWhenOff()
    {
        var session = new EmbeddedWebFilterSession(new ContentFilterEngine());
        session.Open(PlayerPage);
        Assert.Equal(";", session.BuildFallbackInjectionPayload(PlayerPage.Host));
        Assert.Equal(";", session.BuildInjectionPayload(PlayerPage.Host));
    }

    [Fact]
    public void CompatibilityAndPassthroughAlsoSuppressFallbackInjection()
    {
        var session = Session();
        session.OpenPassthrough(PlayerPage);
        Assert.Equal(";", session.BuildFallbackInjectionPayload(PlayerPage.Host));
        session.OpenCompatibilityLimited(PlayerPage, EmbeddedWebAdBlocker.NuploadCompatibilityEngine);
        Assert.Equal(";", session.BuildFallbackInjectionPayload(PlayerPage.Host));
    }

    [Theory]
    [InlineData("noopjs", ";")]
    [InlineData("noop.txt", "")]
    [InlineData("noopjson", "{}")]
    public void KnownSurrogatesHaveABody(string resource, string expected)
    {
        Assert.Equal(expected, Session().Surrogate(resource));
    }

    [Fact]
    public void AnUnknownSurrogateFallsBackToRefusingTheRequest()
    {
        // Serving nothing is right when we have no stand-in; inventing one
        // could hand a page something it will try to execute.
        Assert.Null(Session().Surrogate("some-unknown-resource"));
    }

    [Fact]
    public void TheDefaultHookDeclinesSoTheInjectedPolicyStaysInCharge()
    {
        Assert.False(UnavailableWebViewFilterHook.Instance.TryAttach(
            null!,
            Session()));
    }
}
