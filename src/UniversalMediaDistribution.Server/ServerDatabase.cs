using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;

namespace UniversalMediaDistribution.Server;

/// <summary>
/// Durable server state. Portable sync documents remain documents, while the
/// public catalog and crawler use indexed rows in the same SQLite database.
/// Connections never outlive an operation; the online backup API includes WAL.
/// </summary>
internal sealed class ServerDatabase
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly AsyncLocal<DocumentTransaction?> _transaction = new();
    private bool _initialized;

    public ServerDatabase(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        DatabasePath = Path.Combine(Root, "server.db");
        WriteGate = Gates.GetOrAdd(DatabasePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public SemaphoreSlim WriteGate { get; }

    // Keep multi-document operations (account/index/session, friend pairs,
    // shared list/index, revision check/write) in one SQLite transaction.
    public DocumentTransaction BeginWrite()
    {
        if (_transaction.Value is not null) throw new InvalidOperationException("Nested server writes are not supported.");
        var transaction = new DocumentTransaction(this, Open());
        _transaction.Value = transaction;
        return transaction;
    }

    public sealed class DocumentTransaction : IDisposable
    {
        private readonly ServerDatabase _owner;
        internal SqliteConnection Connection { get; }
        internal SqliteTransaction Transaction { get; }
        internal DocumentTransaction(ServerDatabase owner, SqliteConnection connection)
        {
            _owner = owner;
            Connection = connection;
            try { Transaction = connection.BeginTransaction(deferred: false); }
            catch { connection.Dispose(); throw; }
        }
        public void Commit() => Transaction.Commit();
        public T CommitAndReturn<T>(T value) { Commit(); return value; }
        public void Dispose()
        {
            _owner._transaction.Value = null;
            try { Transaction.Dispose(); }
            finally { Connection.Dispose(); }
        }
    }

    private DocumentCommand CreateDocumentCommand()
    {
        var transaction = _transaction.Value;
        var connection = transaction?.Connection ?? Open();
        var command = connection.CreateCommand();
        command.Transaction = transaction?.Transaction;
        return new DocumentCommand(command, transaction is null ? connection : null);
    }

    private sealed class DocumentCommand(SqliteCommand command, SqliteConnection? ownedConnection) : IDisposable
    {
        internal SqliteCommand Command { get; } = command;
        public void Dispose() { try { Command.Dispose(); } finally { ownedConnection?.Dispose(); } }
    }

    public SqliteConnection Open()
    {
        Initialize();
        return OpenCore();
    }

    private SqliteConnection OpenCore()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=30000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Initialize()
    {
        _initializeGate.Wait();
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Root);
            if (!File.Exists(DatabasePath))
            {
                try
                {
                    using var created = CreatePrivateFile(DatabasePath);
                }
                catch (IOException) when (File.Exists(DatabasePath)) { }
            }
            using var connection = OpenCore();
            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                if (!string.Equals(Convert.ToString(wal.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The server database requires a local filesystem supporting SQLite WAL.");
            }
            using var transaction = connection.BeginTransaction(deferred: false);
            using var schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS server_schema(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS server_documents(
                    key TEXT PRIMARY KEY, parent TEXT NOT NULL, payload TEXT NOT NULL CHECK(json_valid(payload)));
                CREATE INDEX IF NOT EXISTS ix_server_documents_parent ON server_documents(parent);
                CREATE TABLE IF NOT EXISTS catalog_items(
                    id TEXT PRIMARY KEY, media_type TEXT NOT NULL, subtype TEXT NOT NULL,
                    name TEXT NOT NULL, search_text TEXT NOT NULL, payload TEXT NOT NULL,
                    content_hash TEXT NOT NULL, revision INTEGER NOT NULL, updated_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_catalog_media ON catalog_items(media_type, name, id);
                CREATE TABLE IF NOT EXISTS catalog_names(
                    name_key TEXT NOT NULL, item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
                    PRIMARY KEY(name_key,item_id));
                CREATE TABLE IF NOT EXISTS catalog_state(
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL,
                    updated_at TEXT NOT NULL, source_label TEXT);
                CREATE TABLE IF NOT EXISTS catalog_redirects(
                    from_id TEXT PRIMARY KEY, to_id TEXT NOT NULL REFERENCES catalog_items(id));
                CREATE TABLE IF NOT EXISTS crawler_providers(
                    provider_id TEXT PRIMARY KEY, cursor TEXT, backfill_complete INTEGER NOT NULL DEFAULT 0,
                    last_discovery TEXT, last_updates TEXT, last_success TEXT,
                    failure_kind TEXT, failures INTEGER NOT NULL DEFAULT 0, retry_at TEXT);
                CREATE TABLE IF NOT EXISTS crawler_provider_turns(
                    provider_id TEXT PRIMARY KEY, last_attempt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS crawler_page_state(
                    provider_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS crawler_titles(
                    provider_id TEXT NOT NULL, external_id TEXT NOT NULL, preview TEXT NOT NULL,
                    item_id TEXT REFERENCES catalog_items(id), discovered_at TEXT NOT NULL,
                    last_seen TEXT NOT NULL, last_success TEXT, due_at TEXT NOT NULL,
                    failures INTEGER NOT NULL DEFAULT 0, failure_kind TEXT,
                    PRIMARY KEY(provider_id,external_id));
                CREATE INDEX IF NOT EXISTS ix_crawler_due ON crawler_titles(provider_id,due_at);
                CREATE TABLE IF NOT EXISTS crawler_leases(
                    name TEXT PRIMARY KEY, owner TEXT NOT NULL, expires_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS crawler_runs(
                    id TEXT PRIMARY KEY, started_at TEXT NOT NULL, finished_at TEXT,
                    status TEXT NOT NULL, titles_updated INTEGER NOT NULL DEFAULT 0,
                    failures INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS crawler_provider_settings(
                    provider_id TEXT PRIMARY KEY, interval_seconds INTEGER NOT NULL CHECK(interval_seconds BETWEEN 1 AND 60),
                    updated_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_crawler_runs_started ON crawler_runs(started_at DESC);
                CREATE TABLE IF NOT EXISTS server_events(
                    id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at TEXT NOT NULL,
                    level TEXT NOT NULL, source TEXT NOT NULL, provider_id TEXT, run_id TEXT,
                    stage TEXT NOT NULL, status TEXT NOT NULL, message TEXT NOT NULL, http_status INTEGER);
                CREATE INDEX IF NOT EXISTS ix_server_events_provider ON server_events(provider_id,id DESC);
                CREATE INDEX IF NOT EXISTS ix_server_events_time ON server_events(occurred_at);
                """;
            schema.ExecuteNonQuery();
            using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = "SELECT MAX(version) FROM server_schema;";
            var current = version.ExecuteScalar();
            if (current is not DBNull && current is not null && Convert.ToInt64(current) > 2)
                throw new InvalidOperationException("The server database was created by a newer server version.");
            if (current is DBNull or null)
            {
                ImportLegacyDocuments(connection, transaction);
                using var stamp = connection.CreateCommand();
                stamp.Transaction = transaction;
                stamp.CommandText = "INSERT INTO server_schema(version,applied_at) VALUES(1,$now);";
                stamp.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                stamp.ExecuteNonQuery();
            }
            if (current is DBNull or null || Convert.ToInt64(current) < 2)
            {
                using var legacy = connection.CreateCommand();
                legacy.Transaction = transaction;
                legacy.CommandText = "SELECT payload FROM server_documents WHERE key='online-catalog/current.json' AND NOT EXISTS(SELECT 1 FROM catalog_state);";
                if (legacy.ExecuteScalar() is string payload)
                    SqliteOnlineCatalogStore.ImportLegacy(connection, transaction,
                        JsonSerializer.Deserialize(payload, ServerJsonSerializerContext.Default.OnlineCatalogState)
                        ?? throw new InvalidDataException("The legacy online catalog is empty."));
                using var stamp = connection.CreateCommand();
                stamp.Transaction = transaction;
                stamp.CommandText = "INSERT INTO server_schema(version,applied_at) VALUES(2,$now);";
                stamp.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                stamp.ExecuteNonQuery();
            }
            transaction.Commit();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _initialized = true;
        }
        finally { _initializeGate.Release(); }
    }

    private void ImportLegacyDocuments(SqliteConnection connection, SqliteTransaction transaction)
    {
        // A one-time transaction, never a fallback-on-missing read: otherwise a
        // revoked session or deleted list could be resurrected by an old file.
        foreach (var folder in new[] { "accounts", "sessions", "user-sync", "shared-lists", "friends", "releases", "online-catalog" })
        {
            var directory = Path.Combine(Root, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }))
            {
                if (new FileInfo(path).Length > 256L * 1024 * 1024)
                    throw new InvalidDataException($"Legacy server document is too large: {Key(path)}");
                var payload = File.ReadAllText(path);
                using var parsed = JsonDocument.Parse(payload);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO server_documents(key,parent,payload) VALUES($key,$parent,$payload);";
                var key = Key(path);
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$parent", Parent(key));
                insert.Parameters.AddWithValue("$payload", payload);
                insert.ExecuteNonQuery();
            }
        }
    }

    public string Key(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Server state is outside its configured data directory.", nameof(path));
        return Path.GetRelativePath(Root, full).Replace('\\', '/');
    }

    private static string Parent(string key) => key[..key.LastIndexOf('/')];

    public ValueTask<T?> ReadJsonAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = CreateDocumentCommand();
        var command = scope.Command;
        command.CommandText = "SELECT payload FROM server_documents WHERE key=$key;";
        command.Parameters.AddWithValue("$key", Key(path));
        var payload = command.ExecuteScalar() as string;
        return ValueTask.FromResult(payload is null ? null : JsonSerializer.Deserialize(payload, type));
    }

    public ValueTask WriteJsonAtomicAsync<T>(string path, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = CreateDocumentCommand();
        var command = scope.Command;
        var key = Key(path);
        command.CommandText = "INSERT INTO server_documents(key,parent,payload) VALUES($key,$parent,$payload) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$parent", Parent(key));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, type));
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public bool Exists(string path)
    {
        using var scope = CreateDocumentCommand();
        var command = scope.Command;
        command.CommandText = "SELECT 1 FROM server_documents WHERE key=$key;";
        command.Parameters.AddWithValue("$key", Key(path));
        return command.ExecuteScalar() is not null;
    }

    public bool TryDeleteReporting(string path)
    {
        using var scope = CreateDocumentCommand();
        var command = scope.Command;
        command.CommandText = "DELETE FROM server_documents WHERE key=$key;";
        command.Parameters.AddWithValue("$key", Key(path));
        return command.ExecuteNonQuery() > 0;
    }

    public void TryDelete(string path) => TryDeleteReporting(path);

    public IReadOnlyList<string> EnumerateJson(string directory)
    {
        using var scope = CreateDocumentCommand();
        var command = scope.Command;
        command.CommandText = "SELECT key FROM server_documents WHERE parent=$parent ORDER BY key;";
        command.Parameters.AddWithValue("$parent", Key(directory));
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(Path.Combine(Root, reader.GetString(0).Replace('/', Path.DirectorySeparatorChar)));
        return paths;
    }

    public void Backup(string destination)
    {
        var full = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (File.Exists(full) || Directory.Exists(full) ||
            new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal" }.Any(path => string.Equals(path, full, comparison)))
            throw new IOException("Choose a new backup filename outside the active database files.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        try
        {
            using (var created = CreatePrivateFile(temporary)) { }
            using (var source = Open())
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
                using var check = target.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                    throw new InvalidDataException("The server database backup failed its integrity check.");
            }
            File.Move(temporary, full, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
