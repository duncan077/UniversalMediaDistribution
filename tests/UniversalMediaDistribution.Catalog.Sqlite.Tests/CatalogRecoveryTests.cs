using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class CatalogRecoveryTests
{
    [Fact]
    public async Task InitializeAsync_QuarantinesInvalidDatabaseAndCreatesCleanSchema()
    {
        await using var scope = TestDatabaseScope.Create();
        var invalidDatabase = Enumerable.Range(0, 512)
            .Select(static index => (byte)(index % 251))
            .ToArray();
        await File.WriteAllBytesAsync(scope.DatabasePath, invalidDatabase);

        await scope.Database.InitializeAsync();

        var recovery = Assert.IsType<CatalogRecoveryNotice>(
            scope.Database.LastRecovery);
        var preservedDatabasePath = Path.Combine(
            recovery.RecoveryDirectory,
            Path.GetFileName(scope.DatabasePath));
        Assert.True(File.Exists(preservedDatabasePath));
        Assert.Equal(
            invalidDatabase,
            await File.ReadAllBytesAsync(preservedDatabasePath));
        Assert.True(File.Exists(scope.DatabasePath));
        Assert.Equal(
            15L,
            await ExecuteScalarAsync(scope.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(
            "ok",
            await ExecuteScalarTextAsync(scope.DatabasePath, "PRAGMA quick_check;"));
    }

    [Fact]
    public async Task InitializeAsync_DoesNotQuarantineUnsupportedFutureSchema()
    {
        await using var scope = TestDatabaseScope.Create();
        await ExecuteNonQueryAsync(
            scope.DatabasePath,
            "PRAGMA user_version = 999;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Database.InitializeAsync().AsTask());

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
        Assert.Null(scope.Database.LastRecovery);
        Assert.True(File.Exists(scope.DatabasePath));
        Assert.False(Directory.Exists(Path.Combine(scope.DirectoryPath, "recovery")));
    }

    private static async Task<long> ExecuteScalarAsync(
        string databasePath,
        string commandText)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ExecuteScalarTextAsync(
        string databasePath,
        string commandText)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteNonQueryAsync(
        string databasePath,
        string commandText)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection CreateConnection(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
}
