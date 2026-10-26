using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Backup;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteDatabaseBackupService : IFullDatabaseBackupService
{
    private readonly SqliteCatalogDatabase _database;

    public SqliteDatabaseBackupService(SqliteCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<string> CreateBackupAsync(
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var destination = NormalizeExternalPath(destinationPath, nameof(destinationPath));
        EnsureDifferentDatabase(destination);

        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(destination) && !overwrite)
            {
                throw new IOException($"Backup already exists at '{destination}'.");
            }

            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(destination)}.{Guid.CreateVersion7():N}.tmp");

            try
            {
                await CopyDatabaseAsync(
                    _database.DatabasePath,
                    temporaryPath,
                    destinationExists: false,
                    cancellationToken);
                await ValidateDatabaseAsync(temporaryPath, cancellationToken);

                File.Move(temporaryPath, destination, overwrite);
                return destination;
            }
            finally
            {
                DeleteOwnedTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream is not writable.", nameof(destination));
        }

        var temporaryPath = BuildTransferTemporaryPath("export");
        try
        {
            await CreateBackupAsync(temporaryPath, overwrite: false, cancellationToken);
            await using var source = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            DeleteOwnedTemporaryFile(temporaryPath);
        }
    }

    public async ValueTask<DatabaseRestoreResult> RestoreBackupAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizeExternalPath(sourcePath, nameof(sourcePath));
        EnsureDifferentDatabase(source);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The database backup was not found.", source);
        }

        await ValidateDatabaseAsync(source, cancellationToken);
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            var safetyBackupPath = BuildSafetyBackupPath();
            await CopyDatabaseAsync(
                _database.DatabasePath,
                safetyBackupPath,
                destinationExists: false,
                cancellationToken);
            await ValidateDatabaseAsync(safetyBackupPath, cancellationToken);

            try
            {
                await CopyDatabaseAsync(
                    source,
                    _database.DatabasePath,
                    destinationExists: true,
                    cancellationToken);
                await MigrateAndValidateCurrentDatabaseAsync(cancellationToken);
            }
            catch (Exception restoreException)
            {
                try
                {
                    await CopyDatabaseAsync(
                        safetyBackupPath,
                        _database.DatabasePath,
                        destinationExists: true,
                        CancellationToken.None);
                    await MigrateAndValidateCurrentDatabaseAsync(CancellationToken.None);
                }
                catch (Exception recoveryException)
                {
                    throw new AggregateException(
                        "Database restore failed, and the automatic recovery also failed. " +
                        $"The pre-restore copy remains at '{safetyBackupPath}'.",
                        restoreException,
                        recoveryException);
                }

                throw;
            }

            return new DatabaseRestoreResult(source, safetyBackupPath);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask<FullDatabaseRestoreResult> RestoreAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream is not readable.", nameof(source));
        }

        await _database.InitializeAsync(cancellationToken);
        var temporaryPath = BuildTransferTemporaryPath("restore");
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            var result = await RestoreBackupAsync(temporaryPath, cancellationToken);
            return new FullDatabaseRestoreResult(result.SafetyBackupPath);
        }
        finally
        {
            DeleteOwnedTemporaryFile(temporaryPath);
        }
    }

    private async ValueTask MigrateAndValidateCurrentDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await SqliteCatalogDatabase.ConfigureConnectionAsync(connection, cancellationToken);
        await SqliteCatalogMigrator.MigrateAsync(connection, cancellationToken);
        await ValidateOpenConnectionAsync(connection, cancellationToken);
    }

    private static async ValueTask CopyDatabaseAsync(
        string sourcePath,
        string destinationPath,
        bool destinationExists,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var source = CreatePathConnection(sourcePath, SqliteOpenMode.ReadOnly);
        await using var destination = CreatePathConnection(
            destinationPath,
            destinationExists ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate);

        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async ValueTask ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = CreatePathConnection(databasePath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken);
            await ValidateOpenConnectionAsync(connection, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new InvalidDataException(
                $"'{databasePath}' is not a valid Universal Media Distribution database backup.",
                exception);
        }
    }

    private static async ValueTask ValidateOpenConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var integrityCommand = connection.CreateCommand())
        {
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            var integrityResult = Convert.ToString(
                await integrityCommand.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Database integrity check failed: {integrityResult ?? "no result"}.");
            }
        }

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
            if (version is < 1 or > SqliteCatalogMigrator.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported database schema version {version}.");
            }
        }

        await using (var foreignKeyCommand = connection.CreateCommand())
        {
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeyCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.IsDBNull(0) ? "unknown table" : reader.GetString(0);
                var rowId = reader.IsDBNull(1) ? "unknown row" : Convert.ToString(reader.GetValue(1));
                throw new InvalidDataException(
                    $"Database relationship check failed in '{table}' at row {rowId}.");
            }
        }

        await using (var applicationCommand = connection.CreateCommand())
        {
            applicationCommand.CommandText = "SELECT value FROM schema_info WHERE key = 'application_id';";
            var applicationId = Convert.ToString(
                await applicationCommand.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(
                    applicationId,
                    SqliteCatalogMigrator.ApplicationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The database does not belong to Universal Media Distribution.");
            }
        }
    }

    private static SqliteConnection CreatePathConnection(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());

    private string BuildSafetyBackupPath()
    {
        var directory = Path.GetDirectoryName(_database.DatabasePath)!;
        var fileName = Path.GetFileNameWithoutExtension(_database.DatabasePath);
        var extension = Path.GetExtension(_database.DatabasePath);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        return Path.Combine(
            directory,
            $"{fileName}.before-restore-{timestamp}-{Guid.CreateVersion7():N}{extension}.backup");
    }

    private string BuildTransferTemporaryPath(string operation)
    {
        var directory = Path.GetDirectoryName(_database.DatabasePath)!;
        return Path.Combine(
            directory,
            $".{Path.GetFileName(_database.DatabasePath)}.{operation}-{Guid.CreateVersion7():N}.tmp");
    }

    private static string NormalizeExternalPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.GetFullPath(path);
    }

    private void EnsureDifferentDatabase(string externalPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // The sidecars carry live database state, so writing a backup over one
        // of them corrupts the database just as surely as writing over the
        // main file. This is the same set QuarantineDatabaseFiles moves.
        string[] reservedPaths =
        [
            _database.DatabasePath,
            _database.DatabasePath + "-wal",
            _database.DatabasePath + "-shm",
            _database.DatabasePath + "-journal"
        ];
        if (reservedPaths.Any(reserved =>
                string.Equals(externalPath, reserved, comparison)))
        {
            throw new ArgumentException("The backup path must be different from the active database path.");
        }
    }

    private static void DeleteOwnedTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The primary operation already produced its useful result or exception.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the uniquely named temporary file for manual cleanup.
        }
    }
}
