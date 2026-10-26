using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class MobilePlaybackSourcePolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5555/media")]
    [InlineData("http://localhost:5555/media")]
    [InlineData("http://[::1]:5555/media")]
    public void RequirePlaybackUri_AcceptsLocalMonoTorrentStreams(
        string uriText)
    {
        var request = CreateRequest(new Uri(uriText));

        var result = MobilePlaybackSourcePolicy.RequirePlaybackUri(request);

        Assert.Equal(uriText, result.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://streams.example/live/channel.m3u8")]
    [InlineData("http://1.1.1.1/live/channel.m3u8")]
    public void RequirePlaybackUri_AcceptsPublicLiveStreams(string uriText)
    {
        var request = CreateRequest(new Uri(uriText));

        var result = MobilePlaybackSourcePolicy.RequirePlaybackUri(request);

        Assert.Equal(uriText, result.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://127.0.0.1:5555/media")]
    [InlineData("http://10.0.0.5/media")]
    [InlineData("https://192.168.1.20/media")]
    [InlineData("file:///private/media.mp4")]
    [InlineData("http://secret@127.0.0.1:5555/media")]
    public void RequirePlaybackUri_RejectsUnsafeOrCredentialedSources(
        string uriText)
    {
        var request = CreateRequest(new Uri(uriText));

        var exception = Assert.Throws<NotSupportedException>(
            () => MobilePlaybackSourcePolicy.RequirePlaybackUri(request));

        Assert.Contains(
            "public",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequirePlaybackUri_RejectsStreamOnlySources()
    {
        var source = new PlatformMediaSource(
            playbackUri: null,
            _ => ValueTask.FromException<IMediaReadHandle>(
                new NotSupportedException()));
        var request = new PlatformMediaRequest(
            new PlatformMediaVariant(source, "Original", false, "video/mp4"),
            "Test",
            0,
            [],
            []);

        Assert.Throws<NotSupportedException>(
            () => MobilePlaybackSourcePolicy.RequirePlaybackUri(request));
    }

    [Fact]
    public void PlatformMediaSource_CarriesAValidatedHttpOrigin()
    {
        var origin = new Uri("https://nupload.top/");

        var source = new PlatformMediaSource(
            new Uri("https://sv2.ibra.lat/video/master.m3u8"),
            openReadAsync: null,
            httpOrigin: origin);

        Assert.Equal(origin, source.HttpOrigin);
        Assert.Throws<ArgumentException>(() =>
            new PlatformMediaSource(
                new Uri("https://sv2.ibra.lat/video/master.m3u8"),
                openReadAsync: null,
                httpOrigin: new Uri("https://nupload.top/watch/video")));
    }

    private static PlatformMediaRequest CreateRequest(Uri uri) =>
        new(
            new PlatformMediaVariant(
                new PlatformMediaSource(uri, openReadAsync: null),
                "Original",
                false,
                "video/mp4"),
            "Test",
            0,
            [],
            []);
}
