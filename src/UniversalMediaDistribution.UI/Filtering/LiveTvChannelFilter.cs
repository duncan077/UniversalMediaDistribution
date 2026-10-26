namespace UniversalMediaDistribution.UI.Filtering;

/// <summary>
/// The parts of a live channel the filter reads. Kept as an interface so the
/// filtering rules can be exercised without standing up a channel view model,
/// which needs a media session and a playback callback.
/// </summary>
public interface ILiveTvChannelFilterTarget
{
    string? ProviderId { get; }

    string? ChannelId { get; }

    string Name { get; }

    string ProviderName { get; }

    string GroupName { get; }
}

/// <summary>
/// Narrows the live channel list by provider, group, and free text. Text is
/// matched against everything the row displays plus the channel ID, because a
/// user reading an EPG often has the ID and not the display name.
/// </summary>
public static class LiveTvChannelFilter
{
    public static IReadOnlyList<T> Apply<T>(
        IEnumerable<T> channels,
        string? providerId = null,
        string? group = null,
        string? query = null)
        where T : ILiveTvChannelFilterTarget
    {
        ArgumentNullException.ThrowIfNull(channels);
        var trimmedQuery = query?.Trim() ?? string.Empty;
        return channels
            .Where(channel =>
                MatchesProvider(channel, providerId) &&
                MatchesGroup(channel, group) &&
                MatchesText(channel, trimmedQuery))
            .ToArray();
    }

    /// <summary>
    /// The distinct providers present, ordered for display. Built from the
    /// channels themselves because the list only makes sense for what actually
    /// loaded.
    /// </summary>
    public static IReadOnlyList<(string? ProviderId, string ProviderName)>
        BuildProviderOptions<T>(IEnumerable<T> channels)
        where T : ILiveTvChannelFilterTarget
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels
            .Select(static channel =>
                (channel.ProviderId, channel.ProviderName))
            .Distinct()
            .OrderBy(
                static provider => provider.ProviderName,
                StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> BuildGroupOptions<T>(
        IEnumerable<T> channels)
        where T : ILiveTvChannelFilterTarget
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels
            .Select(static channel => channel.GroupName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(
                static group => group,
                StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool MatchesProvider<T>(T channel, string? providerId)
        where T : ILiveTvChannelFilterTarget =>
        providerId is null ||
        string.Equals(
            channel.ProviderId,
            providerId,
            StringComparison.Ordinal);

    private static bool MatchesGroup<T>(T channel, string? group)
        where T : ILiveTvChannelFilterTarget =>
        group is null ||
        string.Equals(
            channel.GroupName,
            group,
            StringComparison.CurrentCultureIgnoreCase);

    private static bool MatchesText<T>(T channel, string query)
        where T : ILiveTvChannelFilterTarget =>
        query.Length == 0 ||
        Contains(channel.Name, query) ||
        Contains(channel.ProviderName, query) ||
        Contains(channel.GroupName, query) ||
        Contains(channel.ChannelId, query);

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) is
            true;
}
