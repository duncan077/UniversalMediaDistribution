namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Translates a single hoster's embed URL (the kind that today is shown in
/// the embedded WebView) into a direct media URL the native platform player
/// can play without a browser. Implementations are added to a
/// <see cref="VideoHosterResolver"/> which is then handed to the content
/// providers so that providers can publish DirectMedia sources instead of
/// EmbeddedWeb ones whenever a hoster is known.
/// </summary>
public interface IApiVideoHosterExtractor
{
    string Name { get; }

    bool Matches(Uri playbackUri);

    /// <summary>
    /// Whether this extractor recognises a page it was not offered by URL.
    /// <para>
    /// Hosters rotate the domain they publish embeds on far faster than a
    /// pinned list can be maintained, and a rotation makes every source
    /// unrecognisable even though the page behind it is unchanged. The page
    /// itself is the stable signal, so an extractor that can identify its own
    /// player from the markup says so here and is given the source anyway.
    /// </para>
    /// <para>
    /// Only consulted when no extractor claimed the URL, so a wrong answer
    /// costs a failed resolve rather than hijacking another hoster.
    /// </para>
    /// </summary>
    bool MatchesContent(string content) => false;

    ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken);
}
