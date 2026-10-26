namespace UniversalMediaDistribution.UI.Filtering;

/// <summary>
/// Maps a live stream URL onto the MIME type the platform player needs to pick
/// the right demuxer. Extension sniffing is all an M3U playlist gives us: the
/// entries carry no content type, and probing every channel before it is played
/// would cost a request per channel.
/// </summary>
public static class LiveTvStreamFormat
{
    public const string HlsMimeType = "application/vnd.apple.mpegurl";

    public const string DashMimeType = "application/dash+xml";

    /// <summary>
    /// The MIME type implied by the stream URL, or null to let the player work
    /// it out for itself.
    /// </summary>
    public static string? ResolveMimeType(Uri streamUri)
    {
        ArgumentNullException.ThrowIfNull(streamUri);

        // Relative URLs have no AbsolutePath to read, and a live channel that
        // is not absolute cannot be played anyway.
        var path = streamUri.IsAbsoluteUri
            ? streamUri.AbsolutePath
            : streamUri.OriginalString;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return HlsMimeType;
        }

        return path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
            ? DashMimeType
            : null;
    }
}
