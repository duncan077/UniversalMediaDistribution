using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteCatalogRepository :
    ICatalogTransferStore,
    ICatalogItemTransferStore,
    ICatalogStatisticsRepository,
    ICatalogUnitSummaryRepository,
    ICatalogUnitDeletionRepository,
    ICatalogRecoveryStatus
{
    private readonly SqliteCatalogDatabase _database;

    public SqliteCatalogRepository(SqliteCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public CatalogRecoveryNotice? LastRecovery => _database.LastRecovery;

    public async ValueTask<CatalogCounts> GetCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            return await SqliteCatalogReader.GetCountsAsync(
                connection,
                cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyDictionary<Guid, CatalogLatestUnit>>
        GetLatestUnitsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            return await SqliteCatalogReader.GetLatestUnitsAsync(
                connection,
                cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<CatalogItem>> ListAsync(
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await SqliteCatalogReader.ListAsync(connection, mediaType, cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
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

        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await SqliteCatalogReader.GetDetailsAsync(connection, catalogItemId, cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask<bool> DeleteUnitAsync(
        CatalogContentIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.UnitKind is not (ContentUnitKind.Chapter or ContentUnitKind.Episode))
            throw new ArgumentException("Only chapters and episodes can be deleted individually.", nameof(identity));

        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // The parent and season are essential: different seasons and titles
            // routinely contain an episode with the same display number.
            // Foreign keys cascade only this unit's mappings and local progress.
            command.CommandText = """
                DELETE FROM content_units WHERE id IN (
                    SELECT cu.id FROM content_units cu
                    LEFT JOIN seasons s ON s.id = cu.season_id
                    WHERE cu.catalog_item_id = $item_id
                      AND cu.unit_kind = $kind
                      AND cu.display_number_key = $number
                      AND (($season IS NULL AND cu.season_id IS NULL) OR s.season_number = $season)
                );
                """;
            command.Parameters.AddWithValue("$item_id", identity.CatalogItemId.ToString("D"));
            command.Parameters.AddWithValue("$kind", identity.UnitKind == ContentUnitKind.Chapter ? "chapter" : "episode");
            command.Parameters.AddWithValue("$number", NameNormalizer.Normalize(identity.DisplayNumber!));
            command.Parameters.AddWithValue("$season", (object?)identity.SeasonNumber ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _database.OperationGate.Release();
        }
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

        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            var exported = await SqliteCatalogReader.ExportItemsAsync(
                connection,
                [catalogItemId.ToString("D")],
                cancellationToken);
            if (exported.Items.Count == 0)
            {
                return false;
            }

            var item = exported.Items[0];
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            if (item.SyncVersion is { } currentVersion)
            {
                await SqliteUserSyncMetadata.ObserveClockAsync(
                    connection,
                    transaction,
                    currentVersion.Clock,
                    cancellationToken);
            }

            var deletionVersion =
                await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                    connection,
                    transaction,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            await SqliteCatalogImporter.MergeAsync(
                connection,
                transaction,
                new CatalogTransferDocument
                {
                    SourceLabel = "Catalog item deletion",
                    Items =
                    [
                        item with
                        {
                            Deleted = true,
                            SyncVersion = deletionVersion
                        }
                    ]
                },
                "Catalog item deletion",
                documentHash: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask<CatalogItemDetails> UpdateMetadataAsync(
        CatalogMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await EnsureReadyAsync(cancellationToken);

        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await SqliteCatalogMetadataUpdater.UpdateAsync(
                connection,
                transaction,
                update,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return await SqliteCatalogReader.GetDetailsAsync(
                    connection,
                    update.CatalogItemId,
                    cancellationToken) ??
                throw new KeyNotFoundException(
                    "The selected catalog item no longer exists.");
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask<CatalogTransferDocument> ExportCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await SqliteCatalogReader.ExportAsync(connection, cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask<CatalogTransferDocument> ExportCatalogItemAsync(
        Guid catalogItemId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await SqliteCatalogReader.ExportItemsAsync(
                connection, [catalogItemId.ToString("D")], cancellationToken);
        }
        finally { _database.OperationGate.Release(); }
    }

    public async ValueTask<CatalogTransferDocument> ExportMatchingCatalogItemsAsync(
        IReadOnlyList<CatalogTransferItem> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        await EnsureReadyAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await SqliteCatalogReader.ExportMatchingItemsAsync(connection, candidates, cancellationToken);
        }
        finally { _database.OperationGate.Release(); }
    }

    public ValueTask<CatalogImportResult> MergeCatalogAsync(
        CatalogTransferDocument document,
        string? sourceName = null,
        string? documentHash = null,
        CancellationToken cancellationToken = default) =>
        MergeCatalogCoreAsync(document, sourceName, documentHash, restoreSelectedItem: false, cancellationToken);

    public ValueTask<CatalogImportResult> ImportSelectedItemAsync(
        CatalogTransferItem item,
        string? sourceName = null,
        CancellationToken cancellationToken = default) =>
        MergeCatalogCoreAsync(
            new CatalogTransferDocument { Items = [item with { Deleted = false }] },
            sourceName, documentHash: null, restoreSelectedItem: true, cancellationToken);

    private async ValueTask<CatalogImportResult> MergeCatalogCoreAsync(
        CatalogTransferDocument document,
        string? sourceName,
        string? documentHash,
        bool restoreSelectedItem,
        CancellationToken cancellationToken)
    {
        CatalogTransferValidator.Validate(document);
        await EnsureReadyAsync(cancellationToken);

        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var existingCatalog = await SqliteCatalogReader.ExportIdentitiesAsync(
                connection,
                cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (restoreSelectedItem)
            {
                var selected = document.Items[0];
                var existing = existingCatalog.Items.FirstOrDefault(candidate =>
                    selected.Id is not null && candidate.Id == selected.Id) ??
                    CatalogTransferIdentity.FindBestMatch(existingCatalog.Items, selected);
                if (existing is { Deleted: true, SyncVersion: not null })
                {
                    // Observe the deletion even if the other device's clock
                    // is ahead. Restoration must outrank it on every device.
                    await SqliteUserSyncMetadata.ObserveClockAsync(
                        connection, transaction,
                        Math.Max(existing.SyncVersion.Clock, selected.SyncVersion?.Clock ?? 0),
                        cancellationToken);
                    var version = await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                        connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
                    document = document with
                    {
                        Items = [selected with
                        {
                            Id = existing.Id, Deleted = false, SyncVersion = version
                        }]
                    };
                }
            }
            document = document with { Items = document.Items.Select(CatalogStableId.NormalizeLegacyIdentity).ToArray() };
            var (normalizedDocument, folds) = ConvergeCatalogIdentities(
                existingCatalog.Items,
                document);
            var result = await SqliteCatalogImporter.MergeAsync(
                connection,
                transaction,
                normalizedDocument,
                sourceName,
                documentHash,
                cancellationToken);
            await SqliteCatalogRedirects.UpsertAllAsync(
                connection,
                transaction,
                folds,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    private static (
            CatalogTransferDocument Document,
            IReadOnlyList<UserSyncCatalogRedirect> Redirects)
        ConvergeCatalogIdentities(
            IReadOnlyList<CatalogTransferItem> existingItems,
            CatalogTransferDocument document)
    {
        var knownItems = existingItems.ToList();
        var knownBySyncId = new Dictionary<Guid, int>();
        for (var index = 0; index < knownItems.Count; index++)
        {
            if (knownItems[index].Id is { } knownId)
            {
                knownBySyncId.TryAdd(knownId, index);
            }
        }

        var redirectsByLoser =
            new Dictionary<Guid, UserSyncCatalogRedirect>();

        // Exact name matches are the overwhelming majority of a real import,
        // and resolving them by scanning every known item meant normalising
        // both names again for every pair. The key is computed once per item
        // instead, which takes the common path off the quadratic term — this
        // runs with OperationGate held, so every catalog read waits on it.
        var exactKeys = new List<string>(knownItems.Count);
        var byExactKey = new Dictionary<string, int>(
            knownItems.Count,
            StringComparer.Ordinal);
        for (var index = 0; index < knownItems.Count; index++)
        {
            var key = ExactKey(knownItems[index]);
            exactKeys.Add(key);
            byExactKey.TryAdd(key, index);
        }

        var normalizedItems = new List<CatalogTransferItem>(
            document.Items.Count);

        // Converging renames a candidate onto the identity it matched, so two
        // incoming titles can leave this loop as one. What is emitted is keyed
        // by identity for that reason: listing the same title twice would hand
        // the importer a document that contradicts itself, and a document
        // built from several devices reaches this with two spellings of one
        // title as a matter of course.
        var emittedByKey = new Dictionary<string, int>(
            document.Items.Count,
            StringComparer.Ordinal);
        foreach (var documentItem in document.Items)
        {
            var item = documentItem;

            // A tombstone is an instruction, not a title: converging one
            // would rename or fold the deletion itself. It passes through and
            // the importer applies it.
            if (item.Deleted)
            {
                normalizedItems.Add(item);
                continue;
            }

            var itemKey = ExactKey(item);

            // Identity outranks spelling: a title renamed elsewhere arrives
            // with a new name and the same ID, and matching by ID lets the
            // rename land instead of being folded back onto the old name.
            var matchIndex = -1;
            CatalogTransferItem? identityMatch = null;
            if (item.Id is { } incomingId &&
                knownBySyncId.TryGetValue(incomingId, out var bySyncId) &&
                CatalogTransferIdentity.AreCompatible(knownItems[bySyncId], item))
            {
                matchIndex = bySyncId;
                identityMatch = knownItems[matchIndex];
            }

            if (identityMatch is null)
            {
                matchIndex = byExactKey.TryGetValue(itemKey, out var exactIndex)
                    ? exactIndex
                    : -1;
                identityMatch = matchIndex >= 0
                    ? knownItems[matchIndex]
                    : CatalogTransferIdentity.FindBestMatch(knownItems, item);
                if (matchIndex < 0 && identityMatch is not null)
                {
                    item = CatalogTransferIdentity.ConvergeIdentity(
                        identityMatch,
                        item);

                    // Reference identity, not the record's structural
                    // equality: two known items can compare equal and IndexOf
                    // would then rewrite whichever it reached first.
                    matchIndex = IndexOfReference(knownItems, identityMatch);
                }

                item = FoldIdentity(
                    identityMatch,
                    item,
                    redirectsByLoser);
            }

            // Combined rather than discarded: the two entries are the same
            // title, and whichever arrived second still carries units the
            // first does not. The merge keeps the name already emitted, which
            // is the identity everything downstream is keyed by.
            var emittedKey = ExactKey(item);
            if (emittedByKey.TryGetValue(emittedKey, out var emittedIndex))
            {
                item = FoldIdentity(
                    normalizedItems[emittedIndex],
                    item,
                    redirectsByLoser);
                item = CatalogTransferIdentity.CombineConverged(
                    normalizedItems[emittedIndex],
                    item) with
                {
                    Id = item.Id ?? normalizedItems[emittedIndex].Id
                };
                normalizedItems[emittedIndex] = item;
            }
            else
            {
                emittedByKey[emittedKey] = normalizedItems.Count;
                normalizedItems.Add(item);
            }

            if (identityMatch is null || matchIndex < 0)
            {
                var key = ExactKey(item);
                byExactKey.TryAdd(key, knownItems.Count);
                if (item.Id is { } addedId)
                {
                    knownBySyncId.TryAdd(addedId, knownItems.Count);
                }

                exactKeys.Add(key);
                knownItems.Add(item);
            }
            else
            {
                ReplaceKnownItem(
                    knownItems,
                    exactKeys,
                    byExactKey,
                    matchIndex,
                    item);
            }
        }

        return (
            document with { Items = normalizedItems },
            redirectsByLoser.Values
                .OrderBy(static redirect => redirect.FromId)
                .ToArray());

        // Two identities meeting on one title: the smaller GUID survives — a
        // rule with no sides, so every device folds the pair the same way —
        // and the loser leaves a forwarding note for records still under it.
        static CatalogTransferItem FoldIdentity(
            CatalogTransferItem? existing,
            CatalogTransferItem item,
            Dictionary<Guid, UserSyncCatalogRedirect> redirectsByLoser)
        {
            if (existing?.Id is not { } existingId ||
                item.Id is not { } incomingId ||
                existingId == incomingId)
            {
                return item with { Id = item.Id ?? existing?.Id };
            }

            var winner = existingId.CompareTo(incomingId) <= 0
                ? existingId
                : incomingId;
            var loser = winner == existingId ? incomingId : existingId;
            redirectsByLoser.TryAdd(
                loser,
                new UserSyncCatalogRedirect
                {
                    FromId = loser,
                    ToId = winner,
                    SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                        "00000000000000000000000000000000",
                        DateTimeOffset.UtcNow,
                        $"redirect:{loser:N}")
                });
            return item with { Id = winner };
        }

        static string ExactKey(CatalogTransferItem item) =>
            CatalogTransferIdentity.GetKey(item);

        static int IndexOfReference(
            List<CatalogTransferItem> items,
            CatalogTransferItem sought)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (ReferenceEquals(items[index], sought))
                {
                    return index;
                }
            }

            return -1;
        }

        static void ReplaceKnownItem(
            List<CatalogTransferItem> items,
            List<string> keys,
            Dictionary<string, int> byKey,
            int index,
            CatalogTransferItem item)
        {
            var previousKey = keys[index];
            var replacementKey = ExactKey(item);
            items[index] = item;
            keys[index] = replacementKey;
            if (string.Equals(previousKey, replacementKey, StringComparison.Ordinal))
            {
                return;
            }

            // Only retire the old key if it still pointed at this slot; an
            // earlier duplicate keeps its own claim on it.
            if (byKey.TryGetValue(previousKey, out var owner) && owner == index)
            {
                byKey.Remove(previousKey);
            }

            byKey.TryAdd(replacementKey, index);
        }
    }
    private async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken);
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SqliteCatalogDatabase.ConfigureConnectionAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
