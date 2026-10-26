using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class OnlineServerStorageTests
{
    [Fact]
    public async Task AccountRegistration_LoginAuthenticationAndRevocationPersist()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteAccountStore(
            scope.Path,
            passwordIterations: 100_000,
            sessionLifetime: TimeSpan.FromDays(2));
        var registered = await store.RegisterAsync(
            new AccountRegistrationRequest(
                " Alice.Reader ",
                "correct horse battery staple",
                "Alice",
                "Desktop"),
            CancellationToken.None);

        Assert.Equal("alice.reader", registered.Username);
        Assert.True(AccountRules.IsAccessTokenShapeValid(
            registered.AccessToken));
        var authenticated = Assert.IsType<AuthenticatedAccount>(
            await store.AuthenticateAsync(
                registered.AccessToken,
                CancellationToken.None));
        Assert.Equal(registered.AccountId, authenticated.Account.AccountId);

        var reopened = new SqliteAccountStore(
            scope.Path,
            passwordIterations: 100_000);
        Assert.Null(await reopened.LoginAsync(
            new AccountLoginRequest(
                "alice.reader",
                "incorrect password",
                "Phone"),
            CancellationToken.None));
        var loggedIn = Assert.IsType<AccountSessionResponse>(
            await reopened.LoginAsync(
                new AccountLoginRequest(
                    "alice.reader",
                    "correct horse battery staple",
                    "Phone"),
                CancellationToken.None));
        await reopened.RevokeAsync(
            loggedIn.AccessToken,
            CancellationToken.None);
        Assert.Null(await reopened.AuthenticateAsync(
            loggedIn.AccessToken,
            CancellationToken.None));
    }

    [Fact]
    public async Task AdminAccountCreationDoesNotCreateAUserSession()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteAccountStore(
            scope.Path,
            passwordIterations: 100_000);

        var account = await store.CreateAsync(
            new AdminAccountRegistrationRequest(
                "provisioned.user",
                "correct horse battery staple",
                "Provisioned User"),
            CancellationToken.None);

        Assert.Equal("provisioned.user", account.Username);
        using var connection = new ServerDatabase(scope.Path).Open();
        using var sessionCount = connection.CreateCommand();
        sessionCount.CommandText = "SELECT COUNT(*) FROM server_documents WHERE parent LIKE 'sessions/%';";
        Assert.Equal(0L, sessionCount.ExecuteScalar());

        var loggedIn = await store.LoginAsync(
            new AccountLoginRequest(
                "provisioned.user",
                "correct horse battery staple",
                "Desktop"),
            CancellationToken.None);
        Assert.NotNull(loggedIn);
        Assert.Equal(1L, sessionCount.ExecuteScalar());
    }

    [Fact]
    public async Task UserSnapshot_UsesOptimisticRevisionsAndPersists()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        var document = CreateSyncDocument();

        var first = await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(0, document),
            CancellationToken.None);
        Assert.Equal(1, first.Revision);

        var conflict = await Assert.ThrowsAsync<SyncConflictException>(
            () => store.SaveAsync(
                accountId,
                new UserSyncUploadRequest(0, document),
                CancellationToken.None).AsTask());
        Assert.Equal(1, conflict.CurrentRevision);

        var reopened = new SqliteUserSyncStore(scope.Path);
        var restored = Assert.IsType<UserSyncSnapshotState>(
            await reopened.GetAsync(accountId, CancellationToken.None));
        Assert.Equal(1, restored.Revision);
        Assert.Equal("device-one", restored.UpdatedByDeviceId);
        // Upgrading a version-one upload yields the version its shape
        // actually has. The server stores what the device said, never a
        // format newer than the fleet that wrote it.
        Assert.Equal(2, restored.Document.Version);
        Assert.Single(restored.Document.FavoriteStates);
        Assert.Single(restored.Document.Lists);
    }

    [Fact]
    public void StreamedJsonMutation_IsAcceptedAndBounded()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/accounts/login";
        context.Request.ContentLength = null;
        var feature = new MutableRequestBodySizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        var accepted = JsonMutationRequestPolicy.ApplyLimit(
            context,
            40L * 1024 * 1024);

        Assert.True(accepted);
        Assert.Equal(32L * 1024, feature.MaxRequestBodySize);
        Assert.NotEqual(
            StatusCodes.Status411LengthRequired,
            context.Response.StatusCode);
    }

    [Theory]
    [InlineData("PUT", "/api/v1/sync/snapshot")]
    [InlineData("PATCH", "/api/v1/sync/snapshot/progress")]
    public void StreamedSyncMutationsApplyTheLimitBeforeDecompression(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.ContentEncoding = "gzip";
        var feature = new MutableRequestBodySizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        Assert.True(JsonMutationRequestPolicy.ApplyLimit(context, 4096));

        Assert.Equal(4096, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task SnapshotWithMissingCatalogReference_IsPreservedForReconciliation()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var incomplete = CreateSyncDocument() with
        {
            Catalog = new Catalog.Transfer.CatalogTransferDocument()
        };

        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(0, incomplete),
            CancellationToken.None);

        var saved = await store.GetAsync(accountId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Empty(saved.Document.Catalog.Items);
        Assert.Equal(incomplete.Progress.Count, saved.Document.Progress.Count);
        Assert.Single(Assert.Single(saved.Document.Lists).Items);
    }

    [Fact]
    public async Task OnlineCatalog_SearchMatchesContainedText()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        var saved = await store.SaveAsync(
            CreateTestCatalog(),
            CancellationToken.None);

        var found = SqliteOnlineCatalogStore.Search(
            saved,
            "copper",
            "book",
            limit: 20);
        var item = Assert.Single(found.Items);
        Assert.Equal("Copper Skies", item.Name);
        Assert.Empty(SqliteOnlineCatalogStore.Search(
            saved,
            "copper",
            "movie",
            limit: 20).Items);
    }

    [Fact]
    public async Task SharedList_IsDerivedFromOwnedSnapshotAndCanBeRevoked()
    {
        using var scope = TestDirectory.Create();
        var syncStore = new SqliteUserSyncStore(scope.Path);
        var sharedStore = new SqliteSharedListStore(
            scope.Path,
            syncStore);
        var account = new AccountRecord(
            Guid.CreateVersion7(),
            "alice",
            "Alice",
            Convert.ToBase64String(new byte[32]),
            Convert.ToBase64String(new byte[32]),
            100_000,
            DateTimeOffset.UtcNow);
        var document = CreateSyncDocument();
        await syncStore.SaveAsync(
            account.AccountId,
            new UserSyncUploadRequest(0, document),
            CancellationToken.None);

        var shared = await sharedStore.PublishAsync(
            account,
            document.Lists[0].Id,
            CancellationToken.None);
        Assert.Equal("Read later", shared.Name);
        Assert.Single(shared.Catalog.Items);
        Assert.Equal(
            shared.ShareId,
            (await sharedStore.PublishAsync(
                account,
                document.Lists[0].Id,
                CancellationToken.None)).ShareId);

        Assert.True(await sharedStore.RevokeAsync(
            account.AccountId,
            shared.ShareId,
            CancellationToken.None));
        Assert.Null(await sharedStore.GetAsync(
            shared.ShareId,
            CancellationToken.None));
    }

    [Fact]
    public async Task SharedList_FollowsLaterSnapshotsAndDisappearsWithItsList()
    {
        using var scope = TestDirectory.Create();
        var syncStore = new SqliteUserSyncStore(scope.Path);
        var sharedStore = new SqliteSharedListStore(
            scope.Path,
            syncStore);
        var account = new AccountRecord(
            Guid.CreateVersion7(),
            "alice",
            "Alice",
            Convert.ToBase64String(new byte[32]),
            Convert.ToBase64String(new byte[32]),
            100_000,
            DateTimeOffset.UtcNow);
        var original = CreateSyncDocument();
        await syncStore.SaveAsync(
            account.AccountId,
            new UserSyncUploadRequest(0, original),
            CancellationToken.None);
        var shared = await sharedStore.PublishAsync(
            account,
            original.Lists[0].Id,
            CancellationToken.None);
        var renamedList = original.Lists[0] with
        {
            Name = "Updated list",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        var renamedDocument = original with
        {
            ExportedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Lists = [renamedList]
        };
        var renamedSnapshot = await syncStore.SaveAsync(
            account.AccountId,
            new UserSyncUploadRequest(1, renamedDocument),
            CancellationToken.None);

        await sharedStore.SynchronizeAsync(
            account,
            renamedSnapshot,
            CancellationToken.None);

        var updated = Assert.IsType<SharedListRecord>(
            await sharedStore.GetAsync(
                shared.ShareId,
                CancellationToken.None));
        Assert.Equal("Updated list", updated.Name);
        Assert.Equal(shared.ShareId, updated.ShareId);

        var deletedDocument = renamedDocument with
        {
            ExportedAt = DateTimeOffset.UtcNow.AddMinutes(2),
            Lists = []
        };
        var deletedSnapshot = await syncStore.SaveAsync(
            account.AccountId,
            new UserSyncUploadRequest(2, deletedDocument),
            CancellationToken.None);
        await sharedStore.SynchronizeAsync(
            account,
            deletedSnapshot,
            CancellationToken.None);

        Assert.Null(await sharedStore.GetAsync(
            shared.ShareId,
            CancellationToken.None));
    }

    private static UserSyncDocument CreateSyncDocument()
    {
        var catalog = CreateTestCatalog();
        return new UserSyncDocument
        {
            Version = 1,
            DeviceId = "device-one",
            DeviceName = "Test desktop",
            Catalog = catalog,
            FavoriteContributorKeys = ["organization:open frame collective"],
            Progress =
            [
                new UserSyncProgressEntry
                {
                    Content = new UserSyncContentIdentity
                    {
                        Item = new UserSyncCatalogItemIdentity
                        {
                            MediaType = "movie",
                            Name = "Signal in the Dust"
                        }
                    },
                    Position = 120,
                    PositionKind = "seconds",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ],
            Lists =
            [
                new UserSyncMediaList
                {
                    Id = Guid.CreateVersion7(),
                    Name = "Read later",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Items =
                    [
                        new UserSyncCatalogItemIdentity
                        {
                            MediaType = "book",
                            Name = "Copper Skies"
                        }
                    ]
                }
            ]
        };
    }

    private static CatalogTransferDocument CreateTestCatalog() =>
        new()
        {
            SourceLabel = "Server storage tests",
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "movie",
                    Name = "Signal in the Dust",
                    Description = "Test movie",
                    Contributors =
                    [
                        new CatalogTransferContributor
                        {
                            Name = "Open Frame Collective",
                            Kind = "organization",
                            Credits =
                            [
                                new CatalogTransferCredit
                                {
                                    Role = "Distributor"
                                }
                            ]
                        }
                    ]
                },
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = "Copper Skies",
                    Description = "Test manga",
                    Categories = ["Manga"]
                }
            ]
        };

    private sealed class MutableRequestBodySizeFeature :
        IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }
}

internal sealed class TestDirectory : IDisposable
{
    private TestDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TestDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "UniversalMediaDistribution.Tests",
            Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestDirectory(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
