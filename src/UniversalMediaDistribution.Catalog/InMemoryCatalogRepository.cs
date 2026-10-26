using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog;

public sealed class InMemoryCatalogRepository : ICatalogTransferStore, ICatalogUnitDeletionRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public InMemoryCatalogRepository()
        : this(new CatalogTransferDocument
        {
            SourceLabel = "Empty catalog"
        })
    {
    }

    public InMemoryCatalogRepository(CatalogTransferDocument initialDocument)
    {
        ArgumentNullException.ThrowIfNull(initialDocument);
        CatalogTransferValidator.Validate(initialDocument);

        foreach (var item in initialDocument.Items)
        {
            var entry = CreateEntry(
                item,
                initialDocument.ExportedAt);
            _entries.Add(BuildKey(item), entry);
        }
    }

    public async ValueTask<IReadOnlyList<CatalogItem>> ListAsync(
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _entries.Values
                .Select(static entry => CatalogTransferMapper.ToCatalogItem(
                    entry.Item,
                    entry.DatabaseId,
                    entry.UpdatedAt))
                .Where(item => mediaType is null || item.MediaType == mediaType)
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<CatalogItemDetails?> GetDetailsAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException("A catalog item identifier is required.", nameof(catalogItemId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entry = _entries.Values.FirstOrDefault(entry => entry.DatabaseId == catalogItemId);
            return entry is null
                ? null
                : CatalogDetailsMapper.ToDetails(
                    entry.Item,
                    entry.DatabaseId,
                    entry.StableItemKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DeleteUnitAsync(CatalogContentIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.UnitKind is not (ContentUnitKind.Chapter or ContentUnitKind.Episode))
            throw new ArgumentException("Only chapters and episodes can be deleted individually.", nameof(identity));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = _entries.FirstOrDefault(pair => pair.Value.DatabaseId == identity.CatalogItemId);
            if (existing.Value is null) return false;
            var item = existing.Value.Item;
            var removed = false;
            IReadOnlyList<CatalogTransferUnit> Filter(IReadOnlyList<CatalogTransferUnit> units) =>
                units.Where(unit =>
                {
                    var matches = CatalogTransferMapper.ParseUnitKind(unit.Kind) == identity.UnitKind &&
                        NameNormalizer.Normalize(unit.DisplayNumber) == NameNormalizer.Normalize(identity.DisplayNumber!);
                    removed |= matches;
                    return !matches;
                }).ToArray();
            var updated = item with
            {
                Units = identity.SeasonNumber is null ? Filter(item.Units) : item.Units,
                Seasons = item.Seasons.Select(season => season.Number == identity.SeasonNumber
                    ? season with { Units = Filter(season.Units) } : season).ToArray()
            };
            if (removed) _entries[existing.Key] = existing.Value with { Item = updated };
            return removed;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<bool> DeleteAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A catalog item identifier is required.",
                nameof(catalogItemId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = _entries.FirstOrDefault(
                pair => pair.Value.DatabaseId == catalogItemId);
            return existing.Value is not null && _entries.Remove(existing.Key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<CatalogItemDetails> UpdateMetadataAsync(
        CatalogMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existingPair = _entries.FirstOrDefault(
                pair => pair.Value.DatabaseId == update.CatalogItemId);
            if (existingPair.Value is null)
            {
                throw new KeyNotFoundException("The selected catalog item no longer exists.");
            }

            var updatedItem = existingPair.Value.Item with
            {
                Name = update.Name,
                Description = update.Description,
                ThumbnailRef = update.ThumbnailRef,
                AlternativeNames = update.AlternativeNames
                    .Select(static alternativeName => new CatalogTransferAlias
                    {
                        Name = alternativeName.Name,
                        Language = alternativeName.Language,
                        AliasType = alternativeName.Kind
                    })
                    .ToArray(),
                Aliases = null
            };
            var updatedKey = BuildKey(updatedItem);
            if (_entries.TryGetValue(updatedKey, out var conflicting) &&
                conflicting.DatabaseId != update.CatalogItemId)
            {
                throw new InvalidOperationException(
                    $"Another catalog item already uses the title '{update.Name}'.");
            }

            _entries.Remove(existingPair.Key);
            var updatedEntry = existingPair.Value with
            {
                Item = updatedItem,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _entries.Add(updatedKey, updatedEntry);
            return CatalogDetailsMapper.ToDetails(
                updatedItem,
                updatedEntry.DatabaseId,
                updatedEntry.StableItemKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<CatalogTransferDocument> ExportCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new CatalogTransferDocument
            {
                ExportedAt = DateTimeOffset.UtcNow,
                SourceLabel = "In-memory catalog",
                Items = _entries.Values
                    .Select(static entry =>
                        CatalogTransferMapper.ToCanonicalTransferItem(entry.Item))
                    .OrderBy(static item => item.MediaType, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<CatalogImportResult> MergeCatalogAsync(
        CatalogTransferDocument document,
        string? sourceName = null,
        string? documentHash = null,
        CancellationToken cancellationToken = default)
    {
        CatalogTransferValidator.Validate(document);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var created = 0;
            var updated = 0;
            var affectedNames = new List<string>(document.Items.Count);
            var updatedAt = DateTimeOffset.UtcNow;

            foreach (var documentItem in document.Items)
            {
                var item = documentItem;
                var key = BuildKey(item);
                if (!_entries.TryGetValue(key, out var existing))
                {
                    var identityMatch = CatalogTransferIdentity.FindBestMatch(
                        _entries.Values.Select(static entry => entry.Item),
                        item);
                    if (identityMatch is not null)
                    {
                        key = BuildKey(identityMatch);
                        existing = _entries[key];
                        item = CatalogTransferIdentity.ConvergeIdentity(
                            identityMatch,
                            item);
                    }
                }

                if (existing is not null)
                {
                    _entries[key] = existing with
                    {
                        Item = item,
                        UpdatedAt = updatedAt
                    };
                    updated++;
                }
                else
                {
                    _entries.Add(
                        key,
                        CreateEntry(item, updatedAt));
                    created++;
                }

                affectedNames.Add(item.Name);
            }

            return new CatalogImportResult(created, updated, 0, affectedNames);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildKey(CatalogTransferItem item)
    {
        var mediaType = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        return CatalogTransferIdentity.GetKey(item);
    }

    private static Entry CreateEntry(
        CatalogTransferItem item,
        DateTimeOffset updatedAt)
    {
        var databaseId = Guid.CreateVersion7();
        var mediaType = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        var stableItemKey =
            $"item:{CatalogTransferIdentity.GetKey(item)}";
        return new Entry(
            databaseId,
            stableItemKey,
            item,
            updatedAt);
    }

    private sealed record Entry(
        Guid DatabaseId,
        string StableItemKey,
        CatalogTransferItem Item,
        DateTimeOffset UpdatedAt);
}
