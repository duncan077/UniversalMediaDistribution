namespace UniversalMediaDistribution.ContentFiltering.Subscriptions;

/// <summary>
/// One subscribed filter list.
/// </summary>
/// <param name="Id">Stable cache key. Never derived from the URL, so a list
/// can move without invalidating what is already downloaded.</param>
/// <param name="MinimumMode">The lowest filtering mode that includes this
/// list.</param>
/// <param name="OnConstrainedDevices">Whether a low-memory device subscribes
/// to this list. The compiled ruleset is dominated by network rules, so the
/// only lever that meaningfully moves its size is subscribing to fewer of
/// them.</param>
public sealed record FilterListSubscription(
    string Id,
    string Title,
    Uri Url,
    FilteringMode MinimumMode,
    bool OnConstrainedDevices = true);

/// <summary>
/// The lists the application subscribes to by default.
///
/// None of this data ships in the binary — it is fetched at runtime into
/// application storage. That keeps the licensing of the lists (GPL and
/// CC BY-SA, depending on the list) a matter between the viewer and their
/// publishers, and it is why only the small built-in seed is compiled in.
/// </summary>
public static class FilterListCatalog
{
    public static IReadOnlyList<FilterListSubscription> Default { get; } =
    [
        new(
            "ubo-filters",
            "uBlock Origin filters",
            new Uri("https://ublockorigin.github.io/uAssets/filters/filters.txt"),
            FilteringMode.Standard),
        new(
            "ubo-badware",
            "uBlock Origin badware risks",
            new Uri("https://ublockorigin.github.io/uAssets/filters/badware.txt"),
            FilteringMode.Standard),
        new(
            "ubo-privacy",
            "uBlock Origin privacy",
            new Uri("https://ublockorigin.github.io/uAssets/filters/privacy.txt"),
            FilteringMode.Standard),
        new(
            "ubo-quick-fixes",
            "uBlock Origin quick fixes",
            new Uri("https://ublockorigin.github.io/uAssets/filters/quick-fixes.txt"),
            FilteringMode.Standard),
        new(
            "ubo-unbreak",
            "uBlock Origin unbreak",
            new Uri("https://ublockorigin.github.io/uAssets/filters/unbreak.txt"),
            FilteringMode.Standard),
        new(
            "easylist",
            "EasyList",
            new Uri("https://easylist.to/easylist/easylist.txt"),
            FilteringMode.Standard),
        // Around fifty-five thousand rules on its own. A low-memory device
        // gives this up first: it is the largest single contributor to the
        // ruleset, and the advertisement and popunder blocking that protects
        // playback lives in the lists above it.
        new(
            "easyprivacy",
            "EasyPrivacy",
            new Uri("https://easylist.to/easylist/easyprivacy.txt"),
            FilteringMode.Standard,
            OnConstrainedDevices: false),
        new(
            "peter-lowe",
            "Peter Lowe's ad and tracking servers",
            new Uri(
                "https://pgl.yoyo.org/adservers/serverlist.php" +
                "?hostformat=adblockplus&showintro=0&mimetype=plaintext"),
            FilteringMode.Standard),
        new(
            "easylist-annoyances",
            "EasyList annoyances",
            new Uri("https://easylist.to/easylist/fanboy-annoyance.txt"),
            FilteringMode.Complete,
            OnConstrainedDevices: false)
    ];

    /// <summary>
    /// The subset a given mode subscribes to. <see cref="FilteringMode.Basic"/>
    /// and <see cref="FilteringMode.Off"/> subscribe to nothing, which is what
    /// makes them work with no network at all.
    /// </summary>
    public static IReadOnlyList<FilterListSubscription> For(
        FilteringMode mode,
        DeviceTier tier = DeviceTier.Standard,
        IReadOnlyList<FilterListSubscription>? catalog = null)
    {
        if (mode <= FilteringMode.Basic)
        {
            return [];
        }

        var source = catalog ?? Default;
        var result = new List<FilterListSubscription>(source.Count);
        foreach (var subscription in source)
        {
            if (subscription.MinimumMode <= mode &&
                (tier != DeviceTier.Constrained ||
                    subscription.OnConstrainedDevices))
            {
                result.Add(subscription);
            }
        }

        return result;
    }
}
