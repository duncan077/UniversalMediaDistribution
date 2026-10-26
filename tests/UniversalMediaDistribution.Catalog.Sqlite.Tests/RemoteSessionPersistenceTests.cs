using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class RemoteSessionPersistenceTests
{
    [Fact]
    public async Task ASavedSessionSurvivesReopeningTheDatabase()
    {
        await using var scope = TestDatabaseScope.Create();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
        var accountId = Guid.CreateVersion7();
        await new SqliteRemoteSessionStore(scope.Database).SaveAsync(
            new RemoteSessionState
            {
                ServiceUrl = "https://media.example.com/",
                AccessToken = new string('t', 43),
                AccountId = accountId,
                Username = "alice",
                DisplayName = "Alice",
                ExpiresAt = expiresAt
            });

        // A second store over a fresh connection stands in for the next run.
        var restored = await new SqliteRemoteSessionStore(
            new SqliteCatalogDatabase(scope.DatabasePath)).LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal("https://media.example.com/", restored.ServiceUrl);
        Assert.Equal(new string('t', 43), restored.AccessToken);
        Assert.Equal(accountId, restored.AccountId);
        Assert.Equal("alice", restored.Username);
        Assert.Equal("Alice", restored.DisplayName);
        Assert.Equal(
            expiresAt.ToUnixTimeMilliseconds(),
            restored.ExpiresAt.ToUnixTimeMilliseconds());
        Assert.True(restored.IsUsable(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task NoSessionIsReportedForAnUntouchedDatabase()
    {
        await using var scope = TestDatabaseScope.Create();

        Assert.Null(
            await new SqliteRemoteSessionStore(scope.Database).LoadAsync());
    }

    [Fact]
    public async Task SigningOutLeavesNoTokenBehind()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteRemoteSessionStore(scope.Database);
        await store.SaveAsync(new RemoteSessionState
        {
            ServiceUrl = "https://media.example.com/",
            AccessToken = new string('t', 43),
            AccountId = Guid.CreateVersion7(),
            Username = "alice",
            DisplayName = "Alice",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });

        await store.SaveAsync(null);

        Assert.Null(await store.LoadAsync());
        // Every column is gone, not just the token, so nothing about the
        // account is left in the database.
        Assert.Empty(await ReadSessionKeysAsync(scope));
    }

    [Fact]
    public async Task SigningInAgainReplacesTheStoredSession()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteRemoteSessionStore(scope.Database);
        await store.SaveAsync(new RemoteSessionState
        {
            ServiceUrl = "https://media.example.com/",
            AccessToken = new string('o', 43),
            AccountId = Guid.CreateVersion7(),
            Username = "alice",
            DisplayName = "Alice",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });

        var second = new RemoteSessionState
        {
            ServiceUrl = "https://other.example.com/",
            AccessToken = new string('n', 43),
            AccountId = Guid.CreateVersion7(),
            Username = "bob",
            DisplayName = "Bob",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        await store.SaveAsync(second);

        var restored = await store.LoadAsync();
        Assert.NotNull(restored);
        Assert.Equal(second.AccessToken, restored.AccessToken);
        Assert.Equal("bob", restored.Username);
        Assert.Equal("https://other.example.com/", restored.ServiceUrl);
    }

    private static async Task<IReadOnlyList<string>> ReadSessionKeysAsync(
        TestDatabaseScope scope)
    {
        await scope.Database.InitializeAsync();
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = scope.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                // Pooled handles would outlive this connection and keep the
                // temporary database file locked past the end of the test.
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key
            FROM profile_settings
            WHERE key LIKE 'remote_session_%';
            """;
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }
}
