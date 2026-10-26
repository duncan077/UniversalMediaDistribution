namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

internal sealed class TestDatabaseScope : IAsyncDisposable
{
    private TestDatabaseScope(string directoryPath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = Path.Combine(directoryPath, "catalog.db");
        Database = new SqliteCatalogDatabase(DatabasePath);
        Repository = new SqliteCatalogRepository(Database);
        BackupService = new SqliteDatabaseBackupService(Database);
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public SqliteCatalogDatabase Database { get; }

    public SqliteCatalogRepository Repository { get; }

    public SqliteDatabaseBackupService BackupService { get; }

    public static TestDatabaseScope Create()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "UniversalMediaDistribution.Tests",
            Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new TestDatabaseScope(directoryPath);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
