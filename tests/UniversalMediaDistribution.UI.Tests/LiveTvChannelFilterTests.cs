using UniversalMediaDistribution.UI.Filtering;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class LiveTvStreamFormatTests
{
    [Theory]
    [InlineData("https://cdn.example/live/stream.m3u8")]
    [InlineData("https://cdn.example/live/STREAM.M3U8")]
    [InlineData("https://cdn.example/live/stream.m3u8?token=abc")]
    public void HlsPlaylistsResolveToTheHlsType(string url) =>
        Assert.Equal(
            LiveTvStreamFormat.HlsMimeType,
            LiveTvStreamFormat.ResolveMimeType(new Uri(url)));

    [Theory]
    [InlineData("https://cdn.example/live/manifest.mpd")]
    [InlineData("https://cdn.example/live/Manifest.MPD?s=1")]
    public void DashManifestsResolveToTheDashType(string url) =>
        Assert.Equal(
            LiveTvStreamFormat.DashMimeType,
            LiveTvStreamFormat.ResolveMimeType(new Uri(url)));

    [Theory]
    [InlineData("https://cdn.example/live/stream.ts")]
    [InlineData("https://cdn.example/live/stream")]
    [InlineData("https://cdn.example/m3u8/stream")]
    public void AnythingElseIsLeftToThePlayer(string url) =>
        Assert.Null(LiveTvStreamFormat.ResolveMimeType(new Uri(url)));

    [Fact]
    public void TheQueryStringIsNotMistakenForAnExtension() =>
        // AbsolutePath excludes the query, so a token that merely mentions
        // .mpd must not change the type.
        Assert.Null(LiveTvStreamFormat.ResolveMimeType(
            new Uri("https://cdn.example/live/stream?file=movie.mpd")));

    [Fact]
    public void ANullUriIsRejected() =>
        Assert.Throws<ArgumentNullException>(() =>
            LiveTvStreamFormat.ResolveMimeType(null!));
}

public sealed class LiveTvChannelFilterTests
{
    private static readonly FakeChannel[] Channels =
    [
        new("iptv-a", "uk-news-1", "Sky News", "IPTV Alpha", "News"),
        new("iptv-a", "uk-sport-1", "Sky Sports", "IPTV Alpha", "Sports"),
        new("iptv-b", "es-news-1", "Canal Noticias", "IPTV Beta", "Noticias"),
        new("iptv-b", null, "Documentales", "IPTV Beta", "Otros")
    ];

    [Fact]
    public void NoFiltersKeepsEverything() =>
        Assert.Equal(4, LiveTvChannelFilter.Apply(Channels).Count);

    [Fact]
    public void ProviderIsMatchedExactly()
    {
        var filtered = LiveTvChannelFilter.Apply(Channels, providerId: "iptv-b");

        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, channel =>
            Assert.Equal("iptv-b", channel.ProviderId));
        Assert.Empty(LiveTvChannelFilter.Apply(Channels, providerId: "IPTV-B"));
    }

    [Fact]
    public void GroupIsMatchedCaseInsensitively()
    {
        var filtered = LiveTvChannelFilter.Apply(Channels, group: "sports");

        Assert.Equal("Sky Sports", Assert.Single(filtered).Name);
    }

    [Fact]
    public void TextSearchesNameProviderGroupAndChannelId()
    {
        Assert.Single(LiveTvChannelFilter.Apply(Channels, query: "sky sports"));
        Assert.Equal(
            2,
            LiveTvChannelFilter.Apply(Channels, query: "Alpha").Count);
        Assert.Single(
            LiveTvChannelFilter.Apply(Channels, query: "Noticias"),
            static channel => channel.Name == "Canal Noticias");

        // The channel ID is searchable because a user reading an EPG often has
        // the ID and not the display name.
        Assert.Equal(
            "Sky News",
            Assert.Single(
                LiveTvChannelFilter.Apply(Channels, query: "uk-news-1")).Name);
    }

    [Fact]
    public void AWhitespaceQueryIsNotAFilter() =>
        Assert.Equal(4, LiveTvChannelFilter.Apply(Channels, query: "   ").Count);

    [Fact]
    public void AChannelWithoutAnIdIsNotMatchedByIdSearches() =>
        Assert.Empty(LiveTvChannelFilter.Apply(
            [Channels[3]],
            query: "es-news-1"));

    [Fact]
    public void FiltersCombineWithAnd()
    {
        Assert.Empty(LiveTvChannelFilter.Apply(
            Channels,
            providerId: "iptv-a",
            group: "Noticias"));
        Assert.Single(LiveTvChannelFilter.Apply(
            Channels,
            providerId: "iptv-a",
            group: "News",
            query: "sky"));
    }

    [Fact]
    public void ProviderOptionsAreDistinctAndOrderedByName()
    {
        var options = LiveTvChannelFilter.BuildProviderOptions(Channels);

        Assert.Equal(
            [("iptv-a", "IPTV Alpha"), ("iptv-b", "IPTV Beta")],
            options);
    }

    [Fact]
    public void GroupOptionsAreDistinctAndOrdered()
    {
        var options = LiveTvChannelFilter.BuildGroupOptions(Channels);

        Assert.Equal(["News", "Noticias", "Otros", "Sports"], options);
    }

    [Fact]
    public void GroupOptionsCollapseCaseVariants()
    {
        FakeChannel[] channels =
        [
            new("p", "1", "A", "P", "News"),
            new("p", "2", "B", "P", "NEWS")
        ];

        Assert.Single(LiveTvChannelFilter.BuildGroupOptions(channels));
    }

    private sealed record FakeChannel(
        string? ProviderId,
        string? ChannelId,
        string Name,
        string ProviderName,
        string GroupName) : ILiveTvChannelFilterTarget;
}
