using System.Text.Json;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class UserSyncPersistenceTests
{
    [Fact]
    public async Task RemoteServerSettings_DefaultToProductionServer()
    {
        await using var scope = TestDatabaseScope.Create();

        var settings = await new SqliteRemoteServerSettingsStore(
                scope.Database)
            .LoadAsync();

        Assert.Equal(
            RemoteServerSettings.DefaultServiceUrl,
            settings.ServiceUrl);
        Assert.True(settings.OnlineCatalogEnabled);
    }

    [Fact]
    public async Task PortableSnapshot_ReconcilesCatalogProgressFavoritesAndLists()
    {
        await using var source = TestDatabaseScope.Create();
        await source.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        var sourceBook = Assert.Single(
            await source.Repository.ListAsync(MediaType.Book));
        var sourceDetails = Assert.IsType<
            Application.Catalog.CatalogItemDetails>(
            await source.Repository.GetDetailsAsync(sourceBook.Id));
        var sourceUnit = Assert.Single(sourceDetails.Units);
        var sourceUserState = new SqliteUserMediaStateStore(
            source.Database);
        var updatedAt = DateTimeOffset.UtcNow;
        await sourceUserState.SaveProgressAsync(
            sourceUnit.Identity,
            new MediaProgressState(
                7,
                MediaPositionKind.Page,
                isCompleted: false,
                updatedAt));
        await sourceUserState.SaveReaderDisplayModeAsync(
            sourceDetails.Identity,
            ReaderDisplayMode.Continuous);
        var contributor = Assert.Single(sourceBook.Contributors);
        await sourceUserState.SetFavoriteContributorAsync(
            contributor.StableKey,
            isFavorite: true);
        await sourceUserState.SetFavoriteCatalogItemAsync(
            sourceBook.Id,
            isFavorite: true);
        var sourceList = await sourceUserState.CreateUserListAsync(
            "Read later");
        await sourceUserState.SetUserListMembershipAsync(
            sourceList.Id,
            sourceBook.Id,
            isMember: true);
        await new SqliteMetadataProviderSettingsStore(source.Database)
            .SaveAsync(new MetadataProviderSettings
            {
                TmdbApiKey = "must-not-sync",
                MyAnimeListClientId = "also-private"
            });

        var exported = await new SqliteUserSyncStore(
                source.Database,
                source.Repository)
            .ExportAsync("desktop-one", "Desktop one");

        var serialized = JsonSerializer.Serialize(exported);
        Assert.DoesNotContain(
            "must-not-sync",
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "also-private",
            serialized,
            StringComparison.Ordinal);
        Assert.Single(exported.Progress);
        Assert.Single(exported.FavoriteItemStates);
        Assert.Single(exported.Lists);

        await using var target = TestDatabaseScope.Create();
        await target.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("Local only"));
        var targetState = new SqliteUserMediaStateStore(target.Database);
        await targetState.SetFavoriteContributorAsync(
            "organization:old-provider",
            isFavorite: true);
        await targetState.CreateUserListAsync("Old list");
        var result = await new SqliteUserSyncStore(
                target.Database,
                target.Repository)
            .RestoreAsync(exported);

        Assert.True(result.CatalogCreatedCount >= 2);
        Assert.Equal(1, result.ProgressCount);
        Assert.Equal(2, result.ListCount);
        Assert.Contains(
            await target.Repository.ListAsync(MediaType.Movie),
            static item => item.Name == "Local only");
        var restoredBook = Assert.Single(
            await target.Repository.ListAsync(MediaType.Book));
        var restoredDetails = Assert.IsType<
            Application.Catalog.CatalogItemDetails>(
            await target.Repository.GetDetailsAsync(restoredBook.Id));
        var restoredProgress = Assert.IsType<MediaProgressState>(
            await targetState.LoadProgressAsync(
                Assert.Single(restoredDetails.Units).Identity));
        Assert.Equal(7, restoredProgress.Position);
        Assert.Equal(MediaPositionKind.Page, restoredProgress.PositionKind);
        Assert.Equal(
            ReaderDisplayMode.Continuous,
            await targetState.LoadReaderDisplayModeAsync(
                restoredDetails.Identity));
        var restoredFavorites =
            await targetState.LoadFavoriteContributorKeysAsync();
        Assert.Equal(2, restoredFavorites.Count);
        Assert.Contains(contributor.StableKey, restoredFavorites);
        Assert.Contains(
            "organization:old-provider",
            restoredFavorites);
        Assert.Equal(
            [restoredBook.Id],
            await targetState.LoadFavoriteCatalogItemIdsAsync());
        var restoredLists = await targetState.LoadUserListsAsync();
        Assert.Equal(2, restoredLists.Count);
        var restoredList = Assert.Single(
            restoredLists,
            static list => list.Name == "Read later");
        Assert.Equal(sourceList.Id, restoredList.Id);
        Assert.Equal("Read later", restoredList.Name);
        Assert.Equal([restoredBook.Id], restoredList.CatalogItemIds);
    }

    [Fact]
    public async Task RemoteServerSettings_PersistWithoutCredentials()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteRemoteServerSettingsStore(scope.Database);
        var initial = await store.LoadAsync();
        Assert.False(string.IsNullOrWhiteSpace(initial.DeviceId));

        var expected = initial with
        {
            ServiceUrl = "https://sync.example.test/base",
            Username = "Alice.Reader",
            DeviceName = "Living room",
            OnlineCatalogEnabled = true
        };
        await store.SaveAsync(expected);
        var actual = await new SqliteRemoteServerSettingsStore(
                scope.Database)
            .LoadAsync();

        Assert.Equal(
            "https://sync.example.test/base/",
            actual.ServiceUrl);
        Assert.Equal("alice.reader", actual.Username);
        Assert.Equal(initial.DeviceId, actual.DeviceId);
        Assert.True(actual.OnlineCatalogEnabled);
    }

    [Fact]
    public async Task DivergentOfflineDevices_EventuallyConverge()
    {
        await using var first = TestDatabaseScope.Create();
        await using var second = TestDatabaseScope.Create();
        await first.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        await second.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        await ConfigureDeviceAsync(
            first.Database,
            "device-first",
            "First device");
        await ConfigureDeviceAsync(
            second.Database,
            "device-second",
            "Second device");

        var firstState = new SqliteUserMediaStateStore(first.Database);
        var secondState = new SqliteUserMediaStateStore(second.Database);
        var firstBook = await GetBookAsync(first);
        var secondBook = await GetBookAsync(second);
        await firstState.SaveProgressAsync(
            Assert.Single(firstBook.Units).Identity,
            new MediaProgressState(
                9,
                MediaPositionKind.Page,
                isCompleted: false,
                DateTimeOffset.UtcNow));
        await firstState.SetFavoriteContributorAsync(
            "organization:first",
            isFavorite: true);
        await firstState.SetFavoriteCatalogItemAsync(
            firstBook.Item.Id,
            isFavorite: true);
        await firstState.CreateUserListAsync("Read together");
        await secondState.SetFavoriteContributorAsync(
            "organization:second",
            isFavorite: true);
        var secondList =
            await secondState.CreateUserListAsync("Read together");
        await secondState.SetUserListMembershipAsync(
            secondList.Id,
            secondBook.Item.Id,
            isMember: true);

        var firstSync = new SqliteUserSyncStore(
            first.Database,
            first.Repository);
        var secondSync = new SqliteUserSyncStore(
            second.Database,
            second.Repository);
        var firstOffline = await firstSync.ExportAsync(
            "device-first",
            "First device");
        var secondOffline = await secondSync.ExportAsync(
            "device-second",
            "Second device");

        await firstSync.RestoreAsync(secondOffline);
        var reconciled = await firstSync.ExportAsync(
            "device-first",
            "First device");
        await secondSync.RestoreAsync(reconciled);
        await firstSync.RestoreAsync(await secondSync.ExportAsync(
            "device-second",
            "Second device"));

        foreach (var state in new[] { firstState, secondState })
        {
            var favorites =
                await state.LoadFavoriteContributorKeysAsync();
            Assert.Contains("organization:first", favorites);
            Assert.Contains("organization:second", favorites);
            Assert.Single(
                await state.LoadFavoriteCatalogItemIdsAsync());
            var list = Assert.Single(
                await state.LoadUserListsAsync(),
                static candidate =>
                    candidate.Name == "Read together");
            Assert.Single(list.CatalogItemIds);
            var allLists = await state.LoadUserListsAsync();
            Assert.Equal(2, allLists.Count);
            Assert.Equal(
                2,
                allLists
                    .Select(static candidate =>
                        UserMediaList.GetNameKey(candidate.Name))
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        var firstProgress = await firstState.LoadProgressAsync(
            Assert.Single((await GetBookAsync(first)).Units).Identity);
        var secondProgress = await secondState.LoadProgressAsync(
            Assert.Single((await GetBookAsync(second)).Units).Identity);
        Assert.Equal(firstProgress, secondProgress);
        Assert.Equal(9, firstProgress?.Position);
    }

    [Fact]
    public async Task OfflineRemovals_UseTombstonesAndDoNotResurrect()
    {
        await using var first = TestDatabaseScope.Create();
        await using var second = TestDatabaseScope.Create();
        await first.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        await second.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        await ConfigureDeviceAsync(
            first.Database,
            "device-first",
            "First device");
        await ConfigureDeviceAsync(
            second.Database,
            "device-second",
            "Second device");
        var firstState = new SqliteUserMediaStateStore(first.Database);
        var secondState = new SqliteUserMediaStateStore(second.Database);
        var firstBook = await GetBookAsync(first);
        const string removedFavorite = "organization:remove-me";
        await firstState.SetFavoriteContributorAsync(
            removedFavorite,
            isFavorite: true);
        await firstState.SetFavoriteCatalogItemAsync(
            firstBook.Item.Id,
            isFavorite: true);
        var list = await firstState.CreateUserListAsync("Offline list");
        await firstState.SetUserListMembershipAsync(
            list.Id,
            firstBook.Item.Id,
            isMember: true);

        var firstSync = new SqliteUserSyncStore(
            first.Database,
            first.Repository);
        var secondSync = new SqliteUserSyncStore(
            second.Database,
            second.Repository);
        await secondSync.RestoreAsync(await firstSync.ExportAsync(
            "device-first",
            "First device"));

        await firstState.SetFavoriteContributorAsync(
            removedFavorite,
            isFavorite: false);
        await firstState.SetFavoriteCatalogItemAsync(
            firstBook.Item.Id,
            isFavorite: false);
        await firstState.SetUserListMembershipAsync(
            list.Id,
            firstBook.Item.Id,
            isMember: false);
        await secondState.SetFavoriteContributorAsync(
            "organization:second-only",
            isFavorite: true);

        await firstSync.RestoreAsync(await secondSync.ExportAsync(
            "device-second",
            "Second device"));
        var merged = await firstSync.ExportAsync(
            "device-first",
            "First device");
        await secondSync.RestoreAsync(merged);

        foreach (var state in new[] { firstState, secondState })
        {
            var favorites =
                await state.LoadFavoriteContributorKeysAsync();
            Assert.DoesNotContain(removedFavorite, favorites);
            Assert.Contains("organization:second-only", favorites);
            Assert.Empty(
                await state.LoadFavoriteCatalogItemIdsAsync());
            var restoredList = Assert.Single(
                await state.LoadUserListsAsync(),
                candidate => candidate.Id == list.Id);
            Assert.Empty(restoredList.CatalogItemIds);
        }

        await firstState.DeleteUserListAsync(list.Id);
        await secondSync.RestoreAsync(await firstSync.ExportAsync(
            "device-first",
            "First device"));
        Assert.DoesNotContain(
            await secondState.LoadUserListsAsync(),
            candidate => candidate.Id == list.Id);
    }

    [Fact]
    public void CatalogMerge_ConvergesRegardlessOfArrivalOrder()
    {
        var first = TestCatalogDocuments.RichCatalog();
        var second = first with
        {
            SourceLabel = "Second offline device",
            Items = first.Items
                .Select(static item => item.Name == "Sky Archive"
                    ? item with
                    {
                        Description =
                            "A newer offline catalog representation."
                    }
                    : item)
                .ToArray()
        };
        var third = TestCatalogDocuments.SingleMovie("Local addition");
        var exportedAt = DateTimeOffset.Parse(
            "2026-07-24T12:00:00Z");

        var leftFirst = CatalogTransferDocumentMerger.Merge(
            CatalogTransferDocumentMerger.Merge(
                first,
                second,
                exportedAt),
            third,
            exportedAt);
        var rightFirst = CatalogTransferDocumentMerger.Merge(
            first,
            CatalogTransferDocumentMerger.Merge(
                third,
                second,
                exportedAt),
            exportedAt);

        Assert.Equal(
            JsonSerializer.Serialize(leftFirst),
            JsonSerializer.Serialize(rightFirst));
        Assert.Equal(3, leftFirst.Items.Count);
    }

    private static async ValueTask ConfigureDeviceAsync(
        SqliteCatalogDatabase database,
        string deviceId,
        string deviceName)
    {
        await new SqliteRemoteServerSettingsStore(database).SaveAsync(
            new RemoteServerSettings
            {
                DeviceId = deviceId,
                DeviceName = deviceName
            });
    }

    private static async ValueTask<
            Application.Catalog.CatalogItemDetails>
        GetBookAsync(TestDatabaseScope scope)
    {
        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));
        return Assert.IsType<
            Application.Catalog.CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(item.Id));
    }
}
