using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class ServerDatabaseTests
{
    [Fact]
    public async Task RegistrationRollsBackAllDocumentsIfSessionWriteFails()
    {
        using var scope = TestDirectory.Create();
        using var connection = new ServerDatabase(scope.Path).Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_sessions BEFORE INSERT ON server_documents WHEN NEW.key LIKE 'sessions/%'
            BEGIN SELECT RAISE(ABORT,'Simulated session failure'); END;
            """;
        command.ExecuteNonQuery();
        var store = new SqliteAccountStore(scope.Path, 100_000);
        var request = new AccountRegistrationRequest("atomic.user", "test password for atomicity", "Atomic", "Device");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.RegisterAsync(request, CancellationToken.None).AsTask());
        Assert.Null(await store.FindByUsernameAsync(request.Username, CancellationToken.None));
        command.CommandText = "SELECT COUNT(*) FROM server_documents;";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "DROP TRIGGER reject_sessions;";
        command.ExecuteNonQuery();
        Assert.NotNull(await store.RegisterAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedPublicCatalogIdsAreRejectedBeforeReplacingData()
    {
        using var scope = TestDirectory.Create();
        var catalog = new SqliteOnlineCatalogStore(scope.Path);
        var item = new CatalogTransferItem { Id = Guid.CreateVersion7(), MediaType = "book", Subtype = "manga", Name = "Original" };
        await catalog.SaveAsync(new CatalogTransferDocument { Items = [item] }, CancellationToken.None);
        await Assert.ThrowsAsync<CatalogTransferValidationException>(() => catalog.SaveAsync(new CatalogTransferDocument
        { Items = [item, item with { Name = "Different title" }] }, CancellationToken.None).AsTask());
        Assert.Equal("Original", Assert.Single((await catalog.GetAsync(CancellationToken.None))!.Document.Items).Name);
    }

    [Fact]
    public async Task LegacyPublicCatalogMigratesOnceWithItsRevisionAndMetadata()
    {
        using var scope = TestDirectory.Create();
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        var legacy = new OnlineCatalogState(27, now, "old-format-hash", new CatalogTransferDocument
        {
            ExportedAt = now,
            SourceLabel = "Legacy catalog",
            Items = [new CatalogTransferItem { Id = id, Name = "Preserved", MediaType = "book", Subtype = "manga",
                ThumbnailRef = "https://art.example/legacy.jpg", Units = [new() { Kind = "chapter", DisplayNumber = "2", SortOrder = 2 }] }]
        });
        var path = Path.Combine(scope.Path, "online-catalog", "current.json");
        await ServerFileStorage.WriteJsonAtomicAsync(path, legacy, ServerJsonSerializerContext.Default.OnlineCatalogState, CancellationToken.None);
        var original = await File.ReadAllTextAsync(path);
        var store = new SqliteOnlineCatalogStore(scope.Path);
        var migrated = (await store.GetAsync(CancellationToken.None))!;
        Assert.Equal(27, migrated.Revision);
        Assert.Equal(now, migrated.UpdatedAt);
        Assert.Equal(id, Assert.Single(migrated.Document.Items).Id);
        Assert.Equal(legacy.Document.Items[0].ThumbnailRef, migrated.Document.Items[0].ThumbnailRef);
        Assert.Single(migrated.Document.Items[0].Units);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        await store.SaveAsync(new CatalogTransferDocument(), CancellationToken.None);
        Assert.Empty((await new SqliteOnlineCatalogStore(scope.Path).GetAsync(CancellationToken.None))!.Document.Items);
    }

    [Fact]
    public async Task LargeCatalogUploadCommitsWithoutReReadingTheLegacySnapshot()
    {
        using var scope = TestDirectory.Create();
        var catalog = new SqliteOnlineCatalogStore(scope.Path);
        var text = new string('x', 1024 * 1024);
        var document = new CatalogTransferDocument
        {
            Items = Enumerable.Range(1, 31).Select(number => new CatalogTransferItem
            { MediaType = "book", Subtype = "manga", Name = $"Large {number}", Description = text }).ToArray()
        };
        var saved = await catalog.SaveAsync(document, CancellationToken.None);
        Assert.Equal(31, saved.Document.Items.Count);
        Assert.Equal(saved.Sha256, catalog.Metadata(CancellationToken.None)!.Sha256);
        await Assert.ThrowsAsync<CatalogSnapshotTooLargeException>(() => catalog.GetAsync(CancellationToken.None).AsTask());
        var page = catalog.Page(null, 100, CancellationToken.None);
        Assert.NotEmpty(page.Document.Items);
        Assert.NotNull(page.ContinuationToken);
        Assert.True(page.Document.Items.Count < 31);
    }

    [Fact]
    public async Task CatalogMetadataHashMatchesTheLegacySnapshot()
    {
        using var scope = TestDirectory.Create();
        var catalog = new SqliteOnlineCatalogStore(scope.Path);
        await catalog.SaveAsync(new CatalogTransferDocument
        {
            SourceLabel = "Source with á and <escaped>",
            Items = [new CatalogTransferItem { MediaType = "book", Subtype = "manga", Name = "A title" }]
        }, CancellationToken.None);
        var snapshot = (await catalog.GetAsync(CancellationToken.None))!;
        var metadata = catalog.Metadata(CancellationToken.None)!;
        Assert.Equal(snapshot.Revision, metadata.Revision);
        Assert.Equal(snapshot.Sha256, metadata.Sha256);
        Assert.Equal(1, metadata.ItemCount);
    }

    [Fact]
    public async Task ExistingAccountsAndSessionsMigrateWithoutChangingCredentialsOrRevivingRevocations()
    {
        using var scope = TestDirectory.Create();
        var id = Guid.CreateVersion7();
        var salt = RandomNumberGenerator.GetBytes(32);
        const string password = "test password preserved after migration";
        var account = new AccountRecord(id, "existing.user", "Existing",
            Convert.ToBase64String(salt), Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32)),
            100_000, DateTimeOffset.UtcNow.AddDays(-10));
        var token = AccountRules.CreateAccessToken();
        var tokenHash = Hash(token);
        var usernameHash = Hash(account.Username);
        var sessionPath = Path.Combine(scope.Path, "sessions", tokenHash[..2], tokenHash + ".json");
        await ServerFileStorage.WriteJsonAtomicAsync(Path.Combine(scope.Path, "accounts", "by-id", id.ToString("N") + ".json"),
            account, ServerJsonSerializerContext.Default.AccountRecord, CancellationToken.None);
        await ServerFileStorage.WriteJsonAtomicAsync(Path.Combine(scope.Path, "accounts", "by-username", usernameHash[..2], usernameHash + ".json"),
            new AccountIndexRecord(id), ServerJsonSerializerContext.Default.AccountIndexRecord, CancellationToken.None);
        await ServerFileStorage.WriteJsonAtomicAsync(sessionPath,
            new AccountSessionRecord(id, tokenHash, "Old device", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)),
            ServerJsonSerializerContext.Default.AccountSessionRecord, CancellationToken.None);
        var store = new SqliteAccountStore(scope.Path, 100_000);
        Assert.Equal(account, (await store.AuthenticateAsync(token, CancellationToken.None))!.Account);
        Assert.Equal(id, (await store.LoginAsync(new AccountLoginRequest(account.Username, password, "New device"), CancellationToken.None))!.AccountId);
        await store.RevokeAsync(token, CancellationToken.None);
        Assert.True(File.Exists(sessionPath));
        Assert.Null(await new SqliteAccountStore(scope.Path, 100_000).AuthenticateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task BackupIncludesAccountsSyncAndIndexedCatalogAndReopensIndependently()
    {
        using var scope = TestDirectory.Create();
        var account = await new SqliteAccountStore(scope.Path, 100_000).RegisterAsync(
            new AccountRegistrationRequest("backup.user", "test password for backup", "Backup", "Device"), CancellationToken.None);
        var sync = new UserSyncDocument { DeviceId = "device", DeviceName = "Device", Catalog = new CatalogTransferDocument() };
        await new SqliteUserSyncStore(scope.Path).SaveAsync(account.AccountId, new UserSyncUploadRequest(0, sync), CancellationToken.None);
        new SqliteOnlineCatalogStore(scope.Path).MergeScraped("provider", "title", new CatalogTransferItem
        { MediaType = "book", Subtype = "manga", Name = "Backed up" }, CancellationToken.None);
        var restoredRoot = Path.Combine(scope.Path, "restored");
        new ServerDatabase(scope.Path).Backup(Path.Combine(restoredRoot, "server.db"));
        Assert.NotNull(await new SqliteAccountStore(restoredRoot, 100_000).AuthenticateAsync(account.AccessToken, CancellationToken.None));
        Assert.Equal(1, (await new SqliteUserSyncStore(restoredRoot).GetAsync(account.AccountId, CancellationToken.None))!.Revision);
        Assert.Equal("Backed up", Assert.Single(new SqliteOnlineCatalogStore(restoredRoot).Page(null, 10, CancellationToken.None).Document.Items).Name);
        Assert.Empty(Directory.EnumerateFiles(restoredRoot, "*.tmp"));
        Assert.Throws<IOException>(() => new ServerDatabase(scope.Path).Backup(Path.Combine(restoredRoot, "server.db")));
    }

    [Fact]
    public void BackupRejectsDatabaseAndSidecarPaths()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        database.Initialize();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            Assert.Throws<IOException>(() => database.Backup(database.DatabasePath + suffix));
    }

    [Fact]
    public async Task InvalidLegacyJsonRollsBackMigrationAndCanBeRetried()
    {
        using var scope = TestDirectory.Create();
        var badPath = Path.Combine(scope.Path, "sessions", "broken.json");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, "{invalid");
        var database = new ServerDatabase(scope.Path);
        Assert.ThrowsAny<JsonException>(database.Initialize);
        Assert.Equal("{invalid", await File.ReadAllTextAsync(badPath));
        File.Delete(badPath);
        database.Initialize();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM server_schema;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
