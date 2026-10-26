using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Converts resolved API servers into direct-media variants for platform
/// players whose own surface replaces the Avalonia player controls.
/// </summary>
internal static class ApiVideoServerVariantPlanner
{
    public static IReadOnlyList<PlatformMediaVariant> CreateAlternates(
        ApiContentVideoSet videoSet,
        ApiContentVideoSource selectedSource,
        ApiContentVideoSource initialPlaybackSource)
    {
        ArgumentNullException.ThrowIfNull(videoSet);
        ArgumentNullException.ThrowIfNull(selectedSource);
        ArgumentNullException.ThrowIfNull(initialPlaybackSource);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            initialPlaybackSource.PlaybackUri.AbsoluteUri
        };
        var variants = new List<PlatformMediaVariant>();
        foreach (var source in videoSet.Sources)
        {
            if (source.IsEmbeddedWeb ||
                IsSameSource(source, selectedSource) ||
                !seen.Add(source.PlaybackUri.AbsoluteUri))
            {
                continue;
            }

            variants.Add(CreateVariant(source));
        }

        return variants;
    }

    public static PlatformMediaVariant CreateVariant(
        ApiContentVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PlatformMediaVariant(
            new PlatformMediaSource(
                source.PlaybackUri,
                openReadAsync: null,
                source.Referrer,
                userAgent: source.UserAgent,
                decryptionKeys: source.DecryptionKeys
                    .Select(static key =>
                        new PlatformMediaDecryptionKey(
                            key.KeyId,
                            key.Key))
                    .ToArray(),
                httpOrigin: source.HttpOrigin),
            source.Label,
            IsAudio: false,
            source.MimeType,
            CreateSubtitles(source));
    }

    public static IReadOnlyList<PlatformSubtitleTrack> CreateSubtitles(ApiContentVideoSource source) =>
        source.Subtitles.Select(track => new PlatformSubtitleTrack(track.Source, track.Label, track.Language, track.IsDefault)).ToArray();

    private static bool IsSameSource(
        ApiContentVideoSource left,
        ApiContentVideoSource right) =>
        left.PlaybackUri == right.PlaybackUri &&
        left.Kind == right.Kind &&
        string.Equals(
            left.Label,
            right.Label,
            StringComparison.OrdinalIgnoreCase);
}
