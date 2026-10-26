namespace UniversalMediaDistribution.ContentFiltering.Tests;

/// <summary>
/// The invariant this whole engine is built around: filtering may cost an
/// advertisement, never the video. These cases all sit on hosts a rule blocks,
/// and every one of them still has to load.
/// </summary>
public sealed class MediaSafetyTests
{
    private const string PlayerPage =
        "https://player.video-host.example/embed/123";

    private static ContentFilterEngine BlocksEverythingAdjacent() =>
        FilterEngineFactory.Build(
            "||ads.example.org^",
            "||analytics.example.org^",
            "||tracker.example.org^",
            "||ads.video-cdn.example^",
            "/segment",
            "/playlist",
            "-ad-");

    [Theory]
    [InlineData("https://ads.video-cdn.example/hls/master.m3u8")]
    [InlineData("https://analytics.example.org/segment/00042.ts")]
    [InlineData("https://tracker.example.org/manifest.mpd")]
    [InlineData("https://ads.example.org/subtitles/es.vtt")]
    [InlineData("https://ads.example.org/playlist/video.mp4?token=abc")]
    [InlineData("https://ads.example.org/dash/init.m4s")]
    [InlineData("https://ads.example.org/audio/track.m4a")]
    [InlineData("https://ads.example.org/keys/licence.key")]
    public void MediaIsNeverBlocked(string url)
    {
        Assert.Equal(
            FilterAction.Allow,
            BlocksEverythingAdjacent().Act(url, PlayerPage));
    }

    [Fact]
    public void ThePagesOwnSiteIsNeverBlocked()
    {
        var engine = FilterEngineFactory.Build("||ads.video-host.example^");

        Assert.Equal(
            FilterAction.Allow,
            engine.Act(
                "https://ads.video-host.example/embed/123",
                "https://ads.video-host.example/embed/123"));
    }

    [Fact]
    public void SubresourcesOnThePagesSubdomainsAreFiltered()
    {
        var engine = FilterEngineFactory.Build("/chunk");

        Assert.Equal(
            FilterAction.Block,
            engine.Act(
                "https://cdn.player.video-host.example/chunk.js",
                PlayerPage));
    }

    [Theory]
    [InlineData("/tag.js?return=movie.mp4")]
    [InlineData("/tag.js?x=.ts")]
    [InlineData("/tag.js#movie.m3u8")]
    [InlineData("/movie.mp4.js")]
    [InlineData("/hls/advert.js")]
    [InlineData("/playlist-advert")]
    [InlineData("/manifestation")]
    public void MediaLookalikesDoNotBypassRules(string path)
    {
        Assert.Equal(FilterAction.Block,
            BlocksEverythingAdjacent().Act("https://ads.example.org" + path, PlayerPage));
    }

    [Theory]
    [InlineData(ResourceType.Script)]
    [InlineData(ResourceType.Image)]
    [InlineData(ResourceType.SubDocument)]
    [InlineData(ResourceType.Ping)]
    [InlineData(ResourceType.WebSocket)]
    public void AResourceKnownNotToBeMediaCannotBorrowAMediaFilename(ResourceType type)
    {
        var decision = BlocksEverythingAdjacent().Match(new FilterRequest(
            "https://ads.example.org/video.mp4", PlayerPage, type));
        Assert.True(decision.IsBlocking);
    }

    [Theory]
    [InlineData("/hls/master")]
    [InlineData("/manifest?token=abc")]
    [InlineData("/segments/00042")]
    [InlineData("/VIDEO.M3U8?token=abc")]
    public void StreamingRequestsKeepTheirMediaAllowance(string path)
    {
        var decision = BlocksEverythingAdjacent().Match(new FilterRequest(
            "https://ads.example.org" + path, PlayerPage, ResourceType.XmlHttpRequest));
        Assert.Equal(FilterReason.Media, decision.Reason);
    }

    [Fact]
    public void NativeMediaRequestsDoNotNeedAFileExtension()
    {
        var decision = BlocksEverythingAdjacent().Match(new FilterRequest(
            "https://ads.example.org/?signed=abc", PlayerPage, ResourceType.Media));
        Assert.Equal(FilterReason.Media, decision.Reason);
    }

    [Fact]
    public void FirstPartyRulesAndExceptionsAreHonored()
    {
        var engine = FilterEngineFactory.Build(
            "/advert.js$script,~third-party",
            "@@/needed-advert.js$script");
        Assert.True(engine.Match(new FilterRequest(
            "https://player.video-host.example/advert.js", PlayerPage, ResourceType.Script)).IsBlocking);
        Assert.Equal(FilterReason.Exception, engine.Match(new FilterRequest(
            "https://player.video-host.example/needed-advert.js", PlayerPage, ResourceType.Script)).Reason);
        Assert.False(engine.Match(new FilterRequest(
            "https://external.example/advert.js", PlayerPage, ResourceType.Script)).IsBlocking);
    }

    [Fact]
    public void AHostThatMerelyLooksLikeMediaIsStillFiltered()
    {
        // The allowance reads the path and query, never the host, so an ad
        // host cannot rename itself out of a rule.
        var engine = FilterEngineFactory.Build("||m3u8-ads.example^");

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://m3u8-ads.example/tag.js", PlayerPage));
    }
}
