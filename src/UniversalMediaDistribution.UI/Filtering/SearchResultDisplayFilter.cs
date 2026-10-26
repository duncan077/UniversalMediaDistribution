using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Filtering;

/// <summary>
/// Decides which already-returned search results stay on screen.
/// <para>
/// This is narrowing, not searching. The search criteria — text, genre, year —
/// were applied at the source and are deliberately not re-checked here: a
/// provider that filtered by genre natively can return a preview card that
/// never repeats the genre, and re-checking would throw those away.
/// </para>
/// </summary>
public sealed class SearchResultDisplayFilter
{
    private readonly Func<ContentSourceProviderConfiguration, bool>
        _isProviderAvailable;

    /// <param name="isProviderAvailable">
    /// Whether an online result's provider still answers for the shelf being
    /// browsed. A provider disabled mid-search leaves results behind that
    /// should stop showing.
    /// </param>
    public SearchResultDisplayFilter(
        string? providerContributorKey = null,
        string? category = null,
        Func<ContentSourceProviderConfiguration, bool>? isProviderAvailable =
            null)
    {
        ProviderContributorKey = providerContributorKey;
        Category = category;
        _isProviderAvailable = isProviderAvailable ?? (static _ => true);
    }

    public string? ProviderContributorKey { get; }

    public string? Category { get; }


    public bool Matches(ContentSourceCatalogSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsOnline &&
            result.Provider is { } resultProvider &&
            !_isProviderAvailable(resultProvider))
        {
            return false;
        }

        if (!MatchesProviderChoice(result))
        {
            return false;
        }

        if (Category is not null &&
            !result.Card.Categories
                .Concat(result.Card.Genres)
                .Contains(Category, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static bool MatchesNamedProvider(
        ContentSourceCatalogSearchResult result,
        string providerKey)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Card.Distributors.Any(distributor =>
                string.Equals(
                    distributor.StableKey,
                    providerKey,
                    StringComparison.Ordinal)) ||
            result.Provider is { } provider &&
            string.Equals(
                ProviderFilterViewModel.CreateNamedProviderKey(
                    provider.DisplayName),
                providerKey,
                StringComparison.Ordinal);
    }

    private bool MatchesProviderChoice(
        ContentSourceCatalogSearchResult result) =>
        ProviderContributorKey switch
        {
            null => true,
            ProviderFilterViewModel.LocalProviderKey => !result.IsOnline,
            ProviderFilterViewModel.OnlineProviderKey => result.IsOnline,
            var key => MatchesNamedProvider(result, key)
        };
}
