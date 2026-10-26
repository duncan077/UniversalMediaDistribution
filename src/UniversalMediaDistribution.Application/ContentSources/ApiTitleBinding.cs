using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Application.ContentSources;

/// <summary>
/// A provider-owned route that is already known to locate one unit or the
/// season containing it. The catalog treats <see cref="Route"/> as opaque:
/// providers validate and interpret their own routes when a binding is reused.
/// </summary>
public sealed record ApiProviderUnitRoute
{
    public ApiProviderUnitRoute(
        ContentUnitKind unitKind,
        string? displayNumber,
        int? seasonNumber,
        string route)
    {
        if (!Enum.IsDefined(unitKind))
        {
            throw new ArgumentOutOfRangeException(nameof(unitKind));
        }

        if (seasonNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber));
        }

        var normalizedDisplayNumber = string.IsNullOrWhiteSpace(displayNumber)
            ? null
            : displayNumber.Trim();
        if (normalizedDisplayNumber is { Length: > 100 })
        {
            throw new ArgumentOutOfRangeException(nameof(displayNumber));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var normalizedRoute = NormalizeRoute(route);
        if (normalizedRoute.Length > 2_048 ||
            normalizedRoute.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(route));
        }

        if (normalizedDisplayNumber is null && seasonNumber is null)
        {
            throw new ArgumentException(
                "A provider route must identify a unit or a season.");
        }

        UnitKind = unitKind;
        DisplayNumber = normalizedDisplayNumber;
        SeasonNumber = seasonNumber;
        Route = normalizedRoute;
    }

    public ContentUnitKind UnitKind { get; }

    /// <summary>
    /// Null for a season route; otherwise the provider route is unit-specific.
    /// </summary>
    public string? DisplayNumber { get; }

    public int? SeasonNumber { get; }

    public string Route { get; }

    private static string NormalizeRoute(string route)
    {
        var normalized = route.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.StartsWith('\\'))
        {
            throw new ArgumentException(
                "A provider route cannot contain a network authority.",
                nameof(route));
        }

        if (normalized.StartsWith(
                "http://",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var absolute) ||
                absolute.Scheme is not ("http" or "https") ||
                absolute.UserInfo.Length > 0)
            {
                throw new ArgumentException(
                    "A provider route contains an invalid web address.",
                    nameof(route));
            }

            // Origins are deliberately not persisted. Provider domains move;
            // the current configured base URL owns a remembered route.
            normalized = absolute.PathAndQuery;
            return normalized.Length == 0 ? "/" : normalized;
        }

        if (normalized.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A provider route cannot contain a URL scheme.",
                nameof(route));
        }

        return normalized;
    }
}

/// <summary>
/// Remembers which title on a provider corresponds to a local catalog item.
/// Episode and chapter lookups search a provider by name, which is fuzzy and
/// can land on a remake or a similarly named show; once a title has answered
/// correctly for an item, that pairing is reused instead of searching again.
/// </summary>
public sealed record ApiTitleBinding
{
    public ApiTitleBinding(
        Guid catalogItemId,
        string providerId,
        string externalId,
        string title,
        IEnumerable<ApiProviderUnitRoute>? unitRoutes = null,
        DateTimeOffset? lastUsedAt = null)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A title binding requires a catalog item.",
                nameof(catalogItemId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        CatalogItemId = catalogItemId;
        ProviderId = providerId.Trim();
        ExternalId = externalId.Trim();
        Title = string.IsNullOrWhiteSpace(title) ? ExternalId : title.Trim();
        UnitRoutes = unitRoutes?
            .DistinctBy(
                static route => new
                {
                    route.UnitKind,
                    route.SeasonNumber,
                    DisplayNumber = route.DisplayNumber?.ToUpperInvariant()
                })
            .Take(256)
            .ToArray() ?? [];
        LastUsedAt = lastUsedAt;
    }

    public Guid CatalogItemId { get; }

    public string ProviderId { get; }

    /// <summary>The provider's own identifier for the title.</summary>
    public string ExternalId { get; }

    /// <summary>
    /// The provider's title, kept only so a reused binding can present a name
    /// before the item is loaded.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Routes learned while resolving units on this provider. A season route
    /// can serve every episode in that season; a unit route serves one exact
    /// episode or chapter.
    /// </summary>
    public IReadOnlyList<ApiProviderUnitRoute> UnitRoutes { get; }

    /// <summary>
    /// The last time this provider was selected successfully for the item.
    /// Older rows have no value and naturally sort behind a newly used source.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; }
}

public interface IApiTitleBindingStore
{
    /// <summary>
    /// The bindings for one catalog item, keyed by provider id.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, ApiTitleBinding>> LoadAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        ApiTitleBinding binding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets a binding that no longer resolves, so the next lookup searches
    /// by name again.
    /// </summary>
    ValueTask RemoveAsync(
        Guid catalogItemId,
        string providerId,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryApiTitleBindingStore : IApiTitleBindingStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Dictionary<string, ApiTitleBinding>>
        _bindings = [];

    public ValueTask<IReadOnlyDictionary<string, ApiTitleBinding>> LoadAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<
                IReadOnlyDictionary<string, ApiTitleBinding>>(
                _bindings.TryGetValue(catalogItemId, out var forItem)
                    ? new Dictionary<string, ApiTitleBinding>(
                        forItem,
                        StringComparer.Ordinal)
                    : new Dictionary<string, ApiTitleBinding>(
                        StringComparer.Ordinal));
        }
    }

    public ValueTask SaveAsync(
        ApiTitleBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_bindings.TryGetValue(binding.CatalogItemId, out var forItem))
            {
                forItem = new Dictionary<string, ApiTitleBinding>(
                    StringComparer.Ordinal);
                _bindings[binding.CatalogItemId] = forItem;
            }

            forItem[binding.ProviderId] = binding;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(
        Guid catalogItemId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_bindings.TryGetValue(catalogItemId, out var forItem) &&
                forItem.Remove(providerId.Trim()) &&
                forItem.Count == 0)
            {
                _bindings.Remove(catalogItemId);
            }
        }

        return ValueTask.CompletedTask;
    }
}
