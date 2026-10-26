using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ApiVideoServerVariantPlannerTests
{
    [Fact]
    public void CapturedBrowserUserAgentReachesNativePlayer()
    {
        var source = new ApiContentVideoSource("Browser capture", new Uri("https://cdn.example/movie.mp4"), userAgent: "Browser/1.0");
        Assert.Equal("Browser/1.0", ApiVideoServerVariantPlanner.CreateVariant(source).Source.UserAgent);
    }

    [Fact]
    public void CreatesDistinctDirectAlternatesWithRequestHeaders()
    {
        var selected = new ApiContentVideoSource(
            "Primary",
            new Uri("https://primary.example/video.m3u8"),
            "application/vnd.apple.mpegurl");
        var key = new ApiContentVideoDecryptionKey(
            "00112233445566778899aabbccddeeff",
            "ffeeddccbbaa99887766554433221100");
        var alternate = new ApiContentVideoSource(
            "Backup",
            new Uri("https://backup.example/video.mpd"),
            "application/dash+xml",
            new Uri("https://provider.example/watch/4"),
            decryptionKeys: [key],
            httpOrigin: new Uri("https://provider.example/"));
        var duplicateUri = new ApiContentVideoSource(
            "Duplicate backup",
            alternate.PlaybackUri);
        var embedded = new ApiContentVideoSource(
            "Web fallback",
            new Uri("https://embed.example/watch/4"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);
        var videoSet = new ApiContentVideoSet(
            ContentUnitKind.Episode,
            "4",
            seasonNumber: null,
            [selected, alternate, duplicateUri, embedded]);

        var variants = ApiVideoServerVariantPlanner.CreateAlternates(
            videoSet,
            selected,
            selected);

        var variant = Assert.Single(variants);
        Assert.Equal("Backup", variant.Label);
        Assert.Equal(alternate.PlaybackUri, variant.Source.PlaybackUri);
        Assert.Equal(alternate.Referrer, variant.Source.Referrer);
        Assert.Equal(alternate.HttpOrigin, variant.Source.HttpOrigin);
        Assert.Equal("application/dash+xml", variant.MimeType);
        var mappedKey = Assert.Single(variant.Source.DecryptionKeys);
        Assert.Equal(key.KeyId, mappedKey.KeyId);
        Assert.Equal(key.Key, mappedKey.Key);
    }
}
