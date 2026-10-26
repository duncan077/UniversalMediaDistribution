using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteCatalogDatabase : IActiveProfileAccessor
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteCatalogDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Whose state the per-person stores read and write.
    /// <para>
    /// It lives here because it is the context every statement runs in, the
    /// same way the connection is: a store that took it as a constructor
    /// argument would have to be rebuilt to switch profiles, and every store
    /// over one database has to agree on the answer at all times.
    /// </para>
    /// <para>
    /// Defaults to the profile that a library upgraded from a release without
    /// profiles inherits, so a caller that never sets one sees exactly what it
    /// saw before.
    /// </para>
    /// </summary>
    public Guid ActiveProfileId { get; set; } =
        SqliteCatalogMigrator.DefaultProfileId;

    public CatalogRecoveryNotice? LastRecovery { get; private set; }

    internal SemaphoreSlim OperationGate { get; } = new(1, 1);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var databaseExisted = File.Exists(DatabasePath);
            try
            {
                await InitializeCurrentDatabaseAsync(cancellationToken);
            }
            catch (Exception exception) when (
                databaseExisted &&
                IsRecoverableCorruption(exception))
            {
                var recoveryDirectory = QuarantineDatabaseFiles();
                await InitializeCurrentDatabaseAsync(cancellationToken);
                LastRecovery = new CatalogRecoveryNotice(
                    DateTimeOffset.UtcNow,
                    recoveryDirectory,
                    DescribeCorruption(exception));
            }

            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask InitializeCurrentDatabaseAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken);

        await ConfigureConnectionAsync(connection, cancellationToken);
        await SqliteCatalogMigrator.MigrateAsync(connection, cancellationToken);
        await ValidateIntegrityAsync(connection, cancellationToken);
    }

    internal SqliteConnection CreateConnection(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    internal static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // journal_mode is what makes synchronous = NORMAL safe: in the default
        // rollback-journal mode NORMAL skips the fsync that keeps the journal
        // recoverable, and a phone or television box losing power mid-write is
        // routine. WAL is persistent in the file header, so this is a no-op on
        // every connection after the first.
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 30000;
            PRAGMA synchronous = NORMAL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask ValidateIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        if (results.Count != 1 ||
            !string.Equals(results[0], "ok", StringComparison.OrdinalIgnoreCase))
        {
            var details = results.Count == 0
                ? "SQLite returned no integrity result."
                : string.Join(" ", results.Take(3));
            throw new SqliteCatalogCorruptionException(details);
        }
    }

    private static bool IsRecoverableCorruption(Exception exception)
    {
        if (exception is SqliteCatalogCorruptionException)
        {
            return true;
        }

        if (exception is not SqliteException sqliteException)
        {
            return false;
        }

        var primaryErrorCode = sqliteException.SqliteErrorCode & 0xff;
        return primaryErrorCode is 11 or 26;
    }

    private string QuarantineDatabaseFiles()
    {
        var parentDirectory = Path.GetDirectoryName(DatabasePath) ??
            throw new InvalidOperationException(
                "The catalog database has no parent directory.");
        var recoveryRoot = Path.Combine(parentDirectory, "recovery");
        var recoveryDirectory = Path.Combine(
            recoveryRoot,
            $"{Path.GetFileNameWithoutExtension(DatabasePath)}-" +
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-" +
            Guid.CreateVersion7().ToString("N")[..8]);
        var sourcePaths = new[]
        {
            DatabasePath,
            DatabasePath + "-wal",
            DatabasePath + "-shm",
            DatabasePath + "-journal"
        }.Where(File.Exists).ToArray();

        Directory.CreateDirectory(recoveryDirectory);
        var movedFiles = new List<(string Source, string Destination)>();
        try
        {
            foreach (var sourcePath in sourcePaths)
            {
                var destinationPath = Path.Combine(
                    recoveryDirectory,
                    Path.GetFileName(sourcePath));
                File.Move(sourcePath, destinationPath);
                movedFiles.Add((sourcePath, destinationPath));
            }

            return recoveryDirectory;
        }
        catch (Exception moveException)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var movedFile in movedFiles.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(movedFile.Destination) &&
                        !File.Exists(movedFile.Source))
                    {
                        File.Move(movedFile.Destination, movedFile.Source);
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add(rollbackException);
                }
            }

            if (rollbackErrors.Count == 0)
            {
                try
                {
                    Directory.Delete(recoveryDirectory);
                }
                catch
                {
                    // An empty recovery directory is harmless and can be inspected manually.
                }

                throw;
            }

            throw new IOException(
                "The damaged catalog could not be preserved safely.",
                new AggregateException([moveException, .. rollbackErrors]));
        }
    }

    private static string DescribeCorruption(Exception exception)
    {
        var message = exception.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (message.Length > 240)
        {
            message = message[..240];
        }

        return string.IsNullOrWhiteSpace(message)
            ? "SQLite reported a damaged or invalid database."
            : message;
    }

    private sealed class SqliteCatalogCorruptionException(string message) :
        Exception(message);
}
