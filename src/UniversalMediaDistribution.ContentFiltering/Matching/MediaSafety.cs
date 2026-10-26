namespace UniversalMediaDistribution.ContentFiltering.Matching;

/// <summary>
/// The one rule no filter list may overturn: a request that is part of playing
/// the video is never blocked.
///
/// This exists because the cost of the two mistakes is not symmetric. A missed
/// advertisement is an annoyance; a blocked manifest or segment is a video
/// that does not play, and the viewer has no way to tell why. It is also the
/// fast path — during playback almost every request is a segment, and those
/// resolve here without touching the rule index at all.
/// </summary>
internal static class MediaSafety
{
    private static readonly string[] Extensions =
    [
        ".aac",
        ".flac",
        ".key",
        ".m3u",
        ".m3u8",
        ".m4a",
        ".m4s",
        ".mkv",
        ".mp3",
        ".mp4",
        ".mpd",
        ".ogg",
        ".opus",
        ".srt",
        ".ts",
        ".vtt",
        ".webm"
    ];

    private static readonly string[] EndpointNames =
        ["dash", "hls", "manifest", "playlist", "segment", "segments", "subtitle", "subtitles"];

    /// <summary>
    /// True when the target looks like media, a manifest, a segment, a key, or
    /// a subtitle track. Query parameters and fragments cannot grant an
    /// allowance. A known non-media resource type cannot disguise itself by
    /// borrowing a media filename either.
    /// </summary>
    public static bool IsMediaTarget(ReadOnlySpan<char> pathAndQuery, ResourceType type)
    {
        if (type == ResourceType.Media)
        {
            return true;
        }

        if (type is not (ResourceType.Other or ResourceType.XmlHttpRequest))
        {
            return false;
        }

        var delimiter = pathAndQuery.IndexOfAny('?', '#');
        var path = delimiter >= 0 ? pathAndQuery[..delimiter] : pathAndQuery;
        foreach (var extension in Extensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Extensionless streaming endpoints are common. An unrelated file
        // such as /hls/advert.js or /playlist.png is still filtered.
        var filename = path[(path.LastIndexOf('/') + 1)..];
        if (filename.Contains('.'))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            foreach (var name in EndpointNames)
            {
                if (path[segment].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
