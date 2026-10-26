using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed partial class SqliteUserSyncStore
{
    private static bool IsSimpleStateKey(string key) =>
        key.StartsWith("progress:", StringComparison.Ordinal) ||
        key.StartsWith("reader:", StringComparison.Ordinal) ||
        key.StartsWith("favorite:", StringComparison.Ordinal) ||
        key.StartsWith("favitem:", StringComparison.Ordinal);

    private SqliteCommand CreateProfileCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$profile", database.ActiveProfileId.ToString("D"));
        return command;
    }

    // The catalog and list identities have not changed, so positions,
    // preferences and favorites can be written individually. Keep the same
    // connection and operation gate: no second catalog read or state reset.
    // Catalog changes and list reconciliation retain the full restore path.
    private async ValueTask<UserSyncRestoreResult?> TryRestoreStateChangesAsync(
        SqliteConnection connection,
        UserSyncDocument local,
        UserSyncDocument remote,
        CancellationToken cancellationToken)
    {
        var merged = UserSyncDocumentMerger.Merge(
            local, remote, local.Catalog, local.DeviceId, local.DeviceName);
        var changed = UserSyncDocumentDelta.ComputeChangedKeys(local, merged);
        if (!changed.All(IsSimpleStateKey))
        {
            return null;
        }
        if (changed.Count == 0)
        {
            return new UserSyncRestoreResult(0, 0, local.Progress.Count, local.ReaderPreferences.Count,
                local.FavoriteStates.Count(state => state.IsFavorite), local.Lists.Count(list => !list.IsDeleted));
        }

        var itemIds = await ReadItemIdsAsync(connection, cancellationToken);
        var unitIds = merged.Progress.Any(entry => entry.Content.UnitKind is not null)
            ? await ReadUnitIdsAsync(connection, cancellationToken)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var allProgress = ResolveProgress(merged.Progress, itemIds, unitIds);
        var allPreferences = ResolveReaderPreferences(merged.ReaderPreferences, itemIds);
        var progress = allProgress.Where(entry => changed.Contains("progress:" + entry.SyncKey)).ToArray();
        var preferences = allPreferences.Where(entry => changed.Contains("reader:" + entry.SyncKey)).ToArray();
        var favorites = merged.FavoriteStates.Where(state => changed.Contains("favorite:" + state.ContributorKey.Trim())).ToArray();
        var favoriteItems = ResolveFavoriteItems(merged.FavoriteItemStates, itemIds)
            .Where(entry => changed.Contains("favitem:" + entry.SyncKey)).ToArray();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteStateKeysAsync(connection, transaction,
            "DELETE FROM playback_progress WHERE profile_id = $profile AND content_unit_id = $key;",
            progress.Where(entry => entry.IsUnit).Select(entry => entry.DatabaseId), cancellationToken);
        await ExecuteStateKeysAsync(connection, transaction,
            "DELETE FROM playback_progress WHERE profile_id = $profile AND catalog_item_id = $key AND content_unit_id IS NULL;",
            progress.Where(entry => !entry.IsUnit).Select(entry => entry.DatabaseId), cancellationToken);
        await ExecuteStateKeysAsync(connection, transaction,
            "DELETE FROM reader_preferences WHERE profile_id = $profile AND catalog_item_id = $key;",
            preferences.Select(entry => entry.DatabaseId), cancellationToken);
        await ExecuteStateKeysAsync(connection, transaction,
            "DELETE FROM favorite_contributors WHERE profile_id = $profile AND contributor_key = $key;",
            favorites.Select(state => state.ContributorKey.Trim()), cancellationToken);
        await ExecuteStateKeysAsync(connection, transaction,
            "UPDATE library_entries SET is_favorite = 0 WHERE profile_id = $profile AND catalog_item_id = $key AND is_favorite <> 0;",
            favoriteItems.Where(entry => !entry.State.IsFavorite && itemIds.ContainsKey(entry.SyncKey))
                .Select(entry => itemIds[entry.SyncKey]), cancellationToken);
        await InsertProgressAsync(connection, transaction, progress, cancellationToken);
        await InsertReaderPreferencesAsync(connection, transaction, preferences, cancellationToken);
        await InsertFavoriteStatesAsync(connection, transaction, favorites, cancellationToken);
        await InsertFavoriteItemStatesAsync(connection, transaction, favoriteItems, cancellationToken);
        await SqliteUserSyncMetadata.ObserveClockAsync(connection, transaction, GetMaximumClock(merged), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new UserSyncRestoreResult(0, 0, allProgress.Count, allPreferences.Count,
            merged.FavoriteStates.Count(state => state.IsFavorite), merged.Lists.Count(list => !list.IsDeleted));
    }

    private async ValueTask ExecuteStateKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IEnumerable<string> keys,
        CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.Parameters.Add("$key", SqliteType.Text);
        foreach (var key in keys)
        {
            parameter.Value = key;
            // Reusing the command reuses its prepared statement as well.
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
