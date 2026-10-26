using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class EmbeddedWebAdBlockerTests
{
    private static readonly Uri PlayerPage =
        new("https://player.video-host.example/embed/123");

    [Theory]
    [InlineData("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js")]
    [InlineData("https://securepubads.g.doubleclick.net/tag/js/gpt.js")]
    [InlineData("https://ads.video-cdn.example/banner.js")]
    [InlineData("https://tracker.metrics.example/collect")]
    [InlineData("https://a.exoclick.com/ads")]
    [InlineData("https://cdn.popads.net/pop.js")]
    [InlineData("https://s.trafficjunky.net/beacon")]
    [InlineData("https://cdn.pncloudfl.com/pn/09b/59a/66a/09b59a66a.jpg")]
    [InlineData("https://acscdn.com/script/aclib.js")]
    [InlineData("https://js.wpadmngr.com/static/adManager.js")]
    [InlineData("https://static.cloudflareinsights.com/beacon.min.js/v1")]
    [InlineData("https://amvntixh.in/sn/pr/2060453")]
    [InlineData("https://coosync.com/sn/c?zoneid=2060453")]
    [InlineData("https://demandingoverdriveunthread.com/in.js")]
    [InlineData("https://analytics.example.org/collect?v=2")]
    [InlineData("https://beacon.example.org/hit.png")]
    [InlineData("https://mixpanel.com/track")]
    public void BlocksAdAndTrackingHosts(string value)
    {
        Assert.True(EmbeddedWebAdBlocker.ShouldBlock(new Uri(value)));
    }

    [Theory]
    [InlineData("https://cdn.example.org/pagead/js/adsbygoogle.js")]
    [InlineData("https://static.example.org/adframe.html")]
    [InlineData("https://serve.example.org/banner.php?zoneid=42")]
    [InlineData("https://partner.example.org/click?aff_id=99")]
    [InlineData("https://rotating-domain.example/chicken.gif?z=2060453&pb=x")]
    public void BlocksAdvertisingPathsOnOtherwiseUnknownHosts(string value)
    {
        Assert.True(
            EmbeddedWebAdBlocker.ShouldBlock(new Uri(value), PlayerPage));
    }

    [Theory]
    [InlineData("https://cdn.video-cdn.example/player.js")]
    [InlineData("https://doubleclick.net.example.org/player.js")]
    [InlineData("https://notads.example.org/video.m3u8")]
    [InlineData("data:text/plain,advertisement")]
    [InlineData("https://downloads.example.org/player.js")]
    [InlineData("https://uploads.example.org/thumb.jpg")]
    public void AllowsNormalPlaybackResources(string value)
    {
        Assert.False(EmbeddedWebAdBlocker.ShouldBlock(new Uri(value)));
    }

    [Theory]
    [InlineData("https://ads.video-cdn.example/hls/master.m3u8")]
    [InlineData("https://analytics.example.org/segment/00042.ts")]
    [InlineData("https://tracker.example.org/manifest.mpd")]
    [InlineData("https://ads.example.org/subtitles/es.vtt")]
    [InlineData("https://ads.example.org/playlist/video.mp4?token=abc")]
    public void MediaAlwaysWinsOverABlockingRule(string value)
    {
        // A filter must never be the reason a video stops playing.
        Assert.False(
            EmbeddedWebAdBlocker.ShouldBlock(new Uri(value), PlayerPage));
    }

    [Fact]
    public void AllowsTheSelectedPlayerHostEvenWhenItsNameLooksLikeAnAdHost()
    {
        var player = new Uri("https://ads.video-host.example/embed/123");

        Assert.False(EmbeddedWebAdBlocker.ShouldBlock(player, player));
    }

    [Fact]
    public void AllowsSubdomainsOfThePlayerHost()
    {
        Assert.False(EmbeddedWebAdBlocker.ShouldBlock(
            new Uri("https://cdn.player.video-host.example/chunk.js"),
            PlayerPage));
    }

    [Fact]
    public void BlocksThirdPartyTrackersWhileThePlayerPageIsOpen()
    {
        Assert.True(EmbeddedWebAdBlocker.ShouldBlock(
            new Uri("https://www.google-analytics.com/g/collect"),
            PlayerPage));
    }

    [Theory]
    [InlineData("https://player.video-host.example/pagead/ad.js")]
    [InlineData("https://ads.player.video-host.example/tag.js")]
    [InlineData("https://doubleclick.net/tag.js?return=movie.mp4")]
    [InlineData("https://doubleclick.net/tag.js?x=.ts")]
    public void FirstPartyAndMediaLookalikeAdsAreBlocked(string value)
    {
        Assert.True(EmbeddedWebAdBlocker.ShouldBlock(new Uri(value), PlayerPage));
    }

    [Fact]
    public void IgnoresNonHttpSchemes()
    {
        Assert.False(EmbeddedWebAdBlocker.ShouldBlock(
            new Uri("blob:https://player.video-host.example/abc"),
            PlayerPage));
        Assert.False(EmbeddedWebAdBlocker.ShouldBlock(
            new Uri("about:blank"),
            PlayerPage));
    }

    [Fact]
    public void MatchesHostsRegardlessOfCaseOrTrailingDot()
    {
        Assert.True(EmbeddedWebAdBlocker.ShouldBlock(
            new Uri("https://anuncios.exoclick.com/tag"),
            PlayerPage));
        Assert.True(EmbeddedWebAdBlocker.ShouldBlock(
            new Uri("https://ADS.Example.ORG./tag.js"),
            PlayerPage));
    }

    [Theory]
    [InlineData("MutationObserver")]
    [InlineData("globalThis.fetch")]
    [InlineData("XMLHttpRequest")]
    [InlineData("sendBeacon")]
    [InlineData("EventSource")]
    [InlineData("WebSocket")]
    [InlineData("Worker")]
    [InlineData("window.open")]
    [InlineData("isClickTrap")]
    [InlineData("Element.prototype.setAttribute")]
    [InlineData("setAttributeNS")]
    [InlineData("HTMLIFrameElement")]
    [InlineData("HTMLScriptElement")]
    [InlineData("protectUrlProperty")]
    [InlineData("queueMicrotask")]
    public void InjectedPolicyCoversTheRequestAndPopupSurfaces(string value)
    {
        Assert.Contains(
            value,
            EmbeddedWebAdBlocker.InjectionScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedPolicyCarriesTheSameRuleData()
    {
        var script = EmbeddedWebAdBlocker.InjectionScript;

        Assert.Contains("\"doubleclick.net\"", script, StringComparison.Ordinal);
        Assert.Contains("\"tracker\"", script, StringComparison.Ordinal);
        Assert.Contains("\"zoneid=\"", script, StringComparison.Ordinal);
        // The media allowance has to reach the page too, or the injected copy
        // would block streams the C# side permits.
        Assert.Contains("\".m3u8\"", script, StringComparison.Ordinal);
        Assert.Contains("mediaPaths", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedPolicyIsIdempotent()
    {
        // Re-injection happens on every completed navigation, so the script
        // must detect its own marker instead of double-wrapping the page APIs.
        Assert.Contains(
            "if (!globalThis[marker])",
            EmbeddedWebAdBlocker.InjectionScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "__umdEmbeddedAdBlockerV3",
            EmbeddedWebAdBlocker.InjectionScript,
            StringComparison.Ordinal);
    }
}
