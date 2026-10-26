using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Application.Metadata;

public sealed record MetadataProviderSettings
{
    public string TmdbApiKey { get; init; } = string.Empty;

    public string MyAnimeListClientId { get; init; } = string.Empty;

    public string PreferredLanguage { get; init; } = "en-US";

    public MetadataProviderSettings Normalize()
    {
        var tmdbApiKey = NormalizeCredential(TmdbApiKey, nameof(TmdbApiKey));
        var myAnimeListClientId = NormalizeCredential(
            MyAnimeListClientId,
            nameof(MyAnimeListClientId));
        var preferredLanguage = NormalizeLanguage(PreferredLanguage);
        return this with
        {
            TmdbApiKey = tmdbApiKey,
            MyAnimeListClientId = myAnimeListClientId,
            PreferredLanguage = preferredLanguage
        };
    }

    private static string NormalizeCredential(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > 512 || normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Provider credentials cannot contain whitespace or exceed 512 characters.",
                parameterName);
        }

        return normalized;
    }

    private static string NormalizeLanguage(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "en-US" : value.Trim();
        _ = new CatalogAlternativeName("language-check", normalized);
        return normalized;
    }
}

public interface IMetadataProviderSettingsStore
{
    ValueTask<MetadataProviderSettings> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        MetadataProviderSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryMetadataProviderSettingsStore : IMetadataProviderSettingsStore
{
    private readonly Lock _gate = new();
    private MetadataProviderSettings _settings;

    public InMemoryMetadataProviderSettingsStore(
        MetadataProviderSettings? initialSettings = null)
    {
        _settings = (initialSettings ?? new MetadataProviderSettings()).Normalize();
    }

    public ValueTask<MetadataProviderSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask SaveAsync(
        MetadataProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _settings = settings.Normalize();
        }

        return ValueTask.CompletedTask;
    }
}

public sealed record CatalogMetadataSnapshot
{
    public CatalogMetadataSnapshot(
        string providerName,
        string title,
        string? description,
        string? thumbnailRef,
        IEnumerable<CatalogAlternativeName>? alternativeNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        ProviderName = providerName.Trim();
        Title = title.Trim();
        Description = description?.Trim();
        ThumbnailRef = string.IsNullOrWhiteSpace(thumbnailRef)
            ? null
            : thumbnailRef.Trim();
        AlternativeNames = alternativeNames?
            .Where(static alternativeName => alternativeName is not null)
            .ToArray() ?? [];
    }

    public string ProviderName { get; }

    public string Title { get; }

    public string? Description { get; }

    public string? ThumbnailRef { get; }

    public IReadOnlyList<CatalogAlternativeName> AlternativeNames { get; }
}

public interface ICatalogMetadataProvider
{
    string Name { get; }

    bool CanRefresh(CatalogItemDetails details);

    ValueTask<CatalogMetadataSnapshot> FetchAsync(
        CatalogItemDetails details,
        MetadataProviderSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed record CatalogMetadataRefreshResult(
    string ProviderName,
    CatalogItemDetails Details);

public sealed class CatalogMetadataRefreshService
{
    private readonly ICatalogRepository _repository;
    private readonly IMetadataProviderSettingsStore _settingsStore;
    private readonly IReadOnlyList<ICatalogMetadataProvider> _providers;

    public CatalogMetadataRefreshService(
        ICatalogRepository repository,
        IMetadataProviderSettingsStore settingsStore,
        IEnumerable<ICatalogMetadataProvider> providers)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
    }

    public IReadOnlyList<string> ProviderNames =>
        _providers.Select(static provider => provider.Name).ToArray();

    public string? GetProviderName(CatalogItemDetails details) =>
        FindProvider(details)?.Name;

    public async ValueTask<CatalogMetadataRefreshResult> RefreshAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A catalog item identifier is required.",
                nameof(catalogItemId));
        }

        var details = await _repository.GetDetailsAsync(catalogItemId, cancellationToken) ??
            throw new KeyNotFoundException("The selected catalog item no longer exists.");
        var provider = FindProvider(details) ??
            throw new NotSupportedException(
                "This catalog subtype has no metadata provider.");
        var settings = (await _settingsStore.LoadAsync(cancellationToken)).Normalize();
        var snapshot = await provider.FetchAsync(details, settings, cancellationToken);

        var primaryNameKey = NameNormalizer.Normalize(snapshot.Title);
        var alternativeNames = BuildPreviousTitle(details, primaryNameKey)
            .Concat(details.Item.AlternativeNames)
            .Concat(snapshot.AlternativeNames)
            .Where(alternativeName =>
                !string.Equals(
                    alternativeName.NameKey,
                    primaryNameKey,
                    StringComparison.Ordinal))
            .DistinctBy(
                static alternativeName =>
                    $"{alternativeName.NameKey}\u001f{alternativeName.Language}\u001f{alternativeName.Kind}",
                StringComparer.Ordinal)
            .Take(CatalogMetadataUpdate.MaximumAlternativeNameCount)
            .ToArray();

        var update = new CatalogMetadataUpdate(
            catalogItemId,
            snapshot.Title,
            string.IsNullOrWhiteSpace(snapshot.Description)
                ? details.Item.Description
                : snapshot.Description,
            snapshot.ThumbnailRef ?? details.Item.ThumbnailRef,
            alternativeNames);
        var updatedDetails = await _repository.UpdateMetadataAsync(update, cancellationToken);
        return new CatalogMetadataRefreshResult(snapshot.ProviderName, updatedDetails);
    }

    private ICatalogMetadataProvider? FindProvider(CatalogItemDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ICatalogMetadataProvider? selected = null;
        foreach (var provider in _providers)
        {
            if (!provider.CanRefresh(details))
            {
                continue;
            }

            if (selected is not null)
            {
                throw new InvalidOperationException(
                    $"Both '{selected.Name}' and '{provider.Name}' claim the same catalog subtype.");
            }

            selected = provider;
        }

        return selected;
    }

    private static IEnumerable<CatalogAlternativeName> BuildPreviousTitle(
        CatalogItemDetails details,
        string primaryNameKey)
    {
        if (string.Equals(
                details.Item.NameKey,
                primaryNameKey,
                StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return
            [
                new CatalogAlternativeName(
                    details.Item.Name,
                    details.OriginalLanguage,
                    "previous")
            ];
        }
        catch (ArgumentException)
        {
            return [new CatalogAlternativeName(details.Item.Name, kind: "previous")];
        }
    }
}
