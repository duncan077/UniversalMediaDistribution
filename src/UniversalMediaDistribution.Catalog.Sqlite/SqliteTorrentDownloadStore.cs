using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.Torrenting.Ingestion;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteTorrentDownloadStore : ITorrentDownloadStore
{
    private readonly SqliteCatalogDatabase _database;

    public SqliteTorrentDownloadStore(SqliteCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<IReadOnlyList<TorrentDownloadRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            var builders = await LoadRecordsAsync(connection, cancellationToken);
            await LoadFilesAsync(connection, builders, cancellationToken);
            return builders.Values
                .Select(static builder => builder.Build())
                .OrderByDescending(static record => record.UpdatedAt)
                .ToArray();
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask UpsertAsync(
        TorrentDownloadRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO managed_downloads (
                        id, activity_id, session_id, title, detail, state,
                        downloaded_bytes, total_bytes, wanted_downloaded_bytes,
                        wanted_bytes, progress, error_code, storage_key,
                        created_at, updated_at, completed_at)
                    VALUES (
                        $id, $activity_id, $session_id, $title, $detail, $state,
                        $downloaded_bytes, $total_bytes, $wanted_downloaded_bytes,
                        $wanted_bytes, $progress, $error_code, $storage_key,
                        $created_at, $updated_at, $completed_at)
                    ON CONFLICT (id)
                    DO UPDATE SET
                        activity_id = excluded.activity_id,
                        session_id = excluded.session_id,
                        title = excluded.title,
                        detail = excluded.detail,
                        state = excluded.state,
                        downloaded_bytes = excluded.downloaded_bytes,
                        total_bytes = excluded.total_bytes,
                        wanted_downloaded_bytes = excluded.wanted_downloaded_bytes,
                        wanted_bytes = excluded.wanted_bytes,
                        progress = excluded.progress,
                        error_code = excluded.error_code,
                        storage_key = excluded.storage_key,
                        created_at = excluded.created_at,
                        updated_at = excluded.updated_at,
                        completed_at = excluded.completed_at;
                    """;
                command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
                command.Parameters.AddWithValue(
                    "$activity_id",
                    DbValue(record.ActivityId?.ToString()));
                command.Parameters.AddWithValue(
                    "$session_id",
                    DbValue(record.SessionId?.ToString()));
                command.Parameters.AddWithValue("$title", record.Title.Trim());
                command.Parameters.AddWithValue("$detail", DbValue(record.Detail));
                command.Parameters.AddWithValue(
                    "$state",
                    SerializeState(record.State));
                command.Parameters.AddWithValue(
                    "$downloaded_bytes",
                    Math.Max(0, record.DownloadedBytes));
                command.Parameters.AddWithValue(
                    "$total_bytes",
                    DbValue(NormalizeBytes(record.TotalBytes)));
                command.Parameters.AddWithValue(
                    "$wanted_downloaded_bytes",
                    DbValue(NormalizeBytes(record.WantedDownloadedBytes)));
                command.Parameters.AddWithValue(
                    "$wanted_bytes",
                    DbValue(NormalizeBytes(record.WantedBytes)));
                command.Parameters.AddWithValue(
                    "$progress",
                    DbValue(NormalizeProgress(record.Progress)));
                command.Parameters.AddWithValue(
                    "$error_code",
                    DbValue(record.ErrorCode));
                command.Parameters.AddWithValue(
                    "$storage_key",
                    DbValue(record.StorageKey));
                command.Parameters.AddWithValue(
                    "$created_at",
                    record.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue(
                    "$updated_at",
                    record.UpdatedAt.ToString("O"));
                command.Parameters.AddWithValue(
                    "$completed_at",
                    DbValue(record.CompletedAt?.ToString("O")));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteFiles = connection.CreateCommand())
            {
                deleteFiles.Transaction = (SqliteTransaction)transaction;
                deleteFiles.CommandText =
                    "DELETE FROM managed_download_files WHERE download_id = $id;";
                deleteFiles.Parameters.AddWithValue(
                    "$id",
                    record.Id.ToString("D"));
                await deleteFiles.ExecuteNonQueryAsync(cancellationToken);
            }

            if (record.Storage is { } storage)
            {
                for (var index = 0; index < storage.Files.Count; index++)
                {
                    var file = storage.Files[index];
                    await using var insertFile = connection.CreateCommand();
                    insertFile.Transaction = (SqliteTransaction)transaction;
                    insertFile.CommandText = """
                        INSERT INTO managed_download_files (
                            download_id, ordinal, complete_path,
                            incomplete_path, length)
                        VALUES (
                            $download_id, $ordinal, $complete_path,
                            $incomplete_path, $length);
                        """;
                    insertFile.Parameters.AddWithValue(
                        "$download_id",
                        record.Id.ToString("D"));
                    insertFile.Parameters.AddWithValue("$ordinal", index);
                    insertFile.Parameters.AddWithValue(
                        "$complete_path",
                        file.CompletePath);
                    insertFile.Parameters.AddWithValue(
                        "$incomplete_path",
                        file.IncompletePath);
                    insertFile.Parameters.AddWithValue(
                        "$length",
                        Math.Max(0, file.Length));
                    await insertFile.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask DeleteAsync(
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return;
        }

        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            foreach (var recordId in recordIds.Distinct())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    "DELETE FROM managed_downloads WHERE id = $id;";
                command.Parameters.AddWithValue(
                    "$id",
                    recordId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    private static async ValueTask<Dictionary<Guid, RecordBuilder>>
        LoadRecordsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        var records = new Dictionary<Guid, RecordBuilder>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, activity_id, session_id, title, detail, state,
                   downloaded_bytes, total_bytes, wanted_downloaded_bytes,
                   wanted_bytes, progress, error_code, storage_key,
                   created_at, updated_at, completed_at
            FROM managed_downloads
            ORDER BY updated_at DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id))
            {
                continue;
            }

            records[id] = new RecordBuilder
            {
                Id = id,
                ActivityId = ParseActivityId(GetNullableString(reader, 1)),
                SessionId = ParseSessionId(GetNullableString(reader, 2)),
                Title = reader.GetString(3),
                Detail = GetNullableString(reader, 4),
                State = DeserializeState(reader.GetString(5)),
                DownloadedBytes = reader.GetInt64(6),
                TotalBytes = GetNullableInt64(reader, 7),
                WantedDownloadedBytes = GetNullableInt64(reader, 8),
                WantedBytes = GetNullableInt64(reader, 9),
                Progress = GetNullableDouble(reader, 10),
                ErrorCode = GetNullableString(reader, 11),
                StorageKey = GetNullableString(reader, 12),
                CreatedAt = ParseTimestamp(reader.GetString(13)),
                UpdatedAt = ParseTimestamp(reader.GetString(14)),
                CompletedAt = ParseNullableTimestamp(
                    GetNullableString(reader, 15))
            };
        }

        return records;
    }

    private static async ValueTask LoadFilesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<Guid, RecordBuilder> records,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT download_id, complete_path, incomplete_path, length
            FROM managed_download_files
            ORDER BY download_id, ordinal;
            """;
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var id) &&
                records.TryGetValue(id, out var record))
            {
                record.Files.Add(new TorrentStoredFile(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));
            }
        }
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = _database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SqliteCatalogDatabase.ConfigureConnectionAsync(
                connection,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static long? NormalizeBytes(long? value) =>
        value is >= 0 ? value : null;

    private static double? NormalizeProgress(double? value) =>
        value is >= 0 and <= 1 && double.IsFinite(value.Value)
            ? value
            : null;

    private static string SerializeState(TorrentDownloadState state) =>
        state switch
        {
            TorrentDownloadState.Adding => "adding",
            TorrentDownloadState.FetchingMetadata => "metadata",
            TorrentDownloadState.CheckingFiles => "checking",
            TorrentDownloadState.FindingPeers => "peers",
            TorrentDownloadState.Downloading => "downloading",
            TorrentDownloadState.Complete => "complete",
            TorrentDownloadState.Seeding => "seeding",
            TorrentDownloadState.Paused => "paused",
            TorrentDownloadState.Failed => "failed",
            TorrentDownloadState.Stopped => "stopped",
            TorrentDownloadState.Removing => "removing",
            _ => "stopped"
        };

    private static TorrentDownloadState DeserializeState(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "adding" => TorrentDownloadState.Adding,
            "metadata" => TorrentDownloadState.FetchingMetadata,
            "checking" => TorrentDownloadState.CheckingFiles,
            "peers" => TorrentDownloadState.FindingPeers,
            "downloading" => TorrentDownloadState.Downloading,
            "complete" => TorrentDownloadState.Complete,
            "seeding" => TorrentDownloadState.Seeding,
            "paused" => TorrentDownloadState.Paused,
            "failed" => TorrentDownloadState.Failed,
            "removing" => TorrentDownloadState.Removing,
            _ => TorrentDownloadState.Stopped
        };

    private static TorrentActivityId? ParseActivityId(string? value) =>
        Guid.TryParse(value, out var id) ? new TorrentActivityId(id) : null;

    private static TorrentSessionId? ParseSessionId(string? value) =>
        Guid.TryParse(value, out var id) ? new TorrentSessionId(id) : null;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullableTimestamp(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseTimestamp(value);

    private static string? GetNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? GetNullableDouble(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private sealed class RecordBuilder
    {
        public Guid Id { get; init; }

        public TorrentActivityId? ActivityId { get; init; }

        public TorrentSessionId? SessionId { get; init; }

        public required string Title { get; init; }

        public string? Detail { get; init; }

        public TorrentDownloadState State { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; init; }

        public DateTimeOffset? CompletedAt { get; init; }

        public long DownloadedBytes { get; init; }

        public long? TotalBytes { get; init; }

        public long? WantedDownloadedBytes { get; init; }

        public long? WantedBytes { get; init; }

        public double? Progress { get; init; }

        public string? ErrorCode { get; init; }

        public string? StorageKey { get; init; }

        public List<TorrentStoredFile> Files { get; } = [];

        public TorrentDownloadRecord Build() =>
            new(
                Id,
                ActivityId,
                SessionId,
                Title,
                Detail,
                State,
                CreatedAt,
                UpdatedAt,
                CompletedAt,
                DownloadedBytes,
                TotalBytes,
                WantedDownloadedBytes,
                WantedBytes,
                Progress,
                ErrorCode,
                StorageKey,
                Files.Count == 0 ? null : new TorrentSessionStorage(Files));
    }
}
