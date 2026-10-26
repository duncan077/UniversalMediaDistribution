namespace UniversalMediaDistribution.Domain.Catalog;

/// <summary>
/// The identity two installations agree on for the same title.
/// <para>
/// Catalog item Guids are minted locally the first time a title is imported,
/// so they never match between two people and cannot carry identity across a
/// device boundary. This is the key the catalog import already dedupes on and
/// the one the sync protocol itself carries, which is why it is also the key
/// friends match progress on.
/// </para>
/// <para>
/// Both sides of that comparison — the client asking about a title and the
/// server answering from a sync snapshot — have to produce the same string,
/// so the rule lives here rather than being written out at each end.
/// </para>
/// </summary>
public static class ContentItemKey
{
    public static string Create(string mediaType, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        return $"{mediaType.Trim().ToLowerInvariant()}:" +
            NameNormalizer.Normalize(name);
    }

    /// <summary>
    /// Builds the key for a known media type, matching the lowercase spelling
    /// the catalog stores.
    /// </summary>
    public static string Create(MediaType mediaType, string name) =>
        Create(mediaType.ToString(), name);

    public static string Create(MediaType mediaType, string name, string? subtypeCode)
    {
        // Book formats need their own namespace: an adaptation is another work.
        var prefix = mediaType == MediaType.Book &&
            !string.IsNullOrWhiteSpace(subtypeCode)
                ? subtypeCode.Trim().ToLowerInvariant()
                : mediaType.ToString().ToLowerInvariant();
        return Create(prefix, name);
    }
}
