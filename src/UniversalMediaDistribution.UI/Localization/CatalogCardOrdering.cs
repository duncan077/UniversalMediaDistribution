using System.Globalization;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Localization;

public static class CatalogCardOrdering
{
    public static IReadOnlyList<CatalogCard> FilterAndOrder(
        IEnumerable<CatalogCard> cards,
        string? distributorKey,
        CultureInfo? culture = null,
        IReadOnlySet<Guid>? favoriteCatalogItemIds = null,
        string? category = null,
        int? releaseYear = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        culture ??= UiTextCatalog.CurrentCulture;
        favoriteCatalogItemIds ??= new HashSet<Guid>();

        var normalizedFilter = string.IsNullOrWhiteSpace(distributorKey)
            ? null
            : distributorKey.Trim();
        var normalizedCategory = string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim();

        var candidates = cards
            .Where(card =>
                normalizedFilter is null ||
                normalizedFilter is ProviderFilterViewModel.LocalProviderKey ||
                normalizedFilter is not ProviderFilterViewModel.OnlineProviderKey &&
                card.Distributors.Any(distributor =>
                    string.Equals(
                        distributor.StableKey,
                        normalizedFilter,
                        StringComparison.Ordinal)))
            .Where(card =>
                normalizedCategory is null ||
                ContainsValue(card.Categories, normalizedCategory) ||
                ContainsValue(card.Genres, normalizedCategory))
            .Where(card =>
                releaseYear is null ||
                card.ReleaseYear == releaseYear)
            .Select(card => new DisplayCandidate(
                card,
                CatalogDisplayNameSelector.Select(
                    card.Name,
                    card.AlternativeNames,
                    culture),
                favoriteCatalogItemIds.Contains(card.Id)));

        return candidates
            .OrderByDescending(
                static candidate =>
                    candidate.Card.UpdatedAt ??
                    DateTimeOffset.MinValue)
            .ThenByDescending(
                static candidate => candidate.IsFavoriteTitle)
            .ThenBy(
                static candidate => candidate.LocalizedName,
                StringComparer.Create(culture, ignoreCase: true))
            .ThenBy(
                static candidate => candidate.Card.Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(static candidate => candidate.Card)
            .ToArray();
    }

    /// <summary>
    /// An indexed scan, so filtering a large catalog by category allocates no
    /// enumerators. This runs for every card on every refresh.
    /// </summary>
    private static bool ContainsValue(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(
                    values[index],
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record DisplayCandidate(
        CatalogCard Card,
        string LocalizedName,
        bool IsFavoriteTitle);
}
