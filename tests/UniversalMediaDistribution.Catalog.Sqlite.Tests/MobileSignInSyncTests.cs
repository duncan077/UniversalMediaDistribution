using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Reproduces what a freshly installed phone does the moment sign-in succeeds:
/// download the account snapshot, restore it, export the merged state, and hand
/// that export back to the server. The server rejects an export that fails
/// <see cref="UserSyncDocumentValidator"/>, and the app reports that rejection
/// as a failed login.
/// </summary>
public sealed class MobileSignInSyncTests
{
    [Fact]
    public async Task FreshDevice_RestoreThenExport_ProducesAnUploadableSnapshot()
    {
        var snapshot = await CreateAccountSnapshotAsync();
        UserSyncDocumentValidator.Validate(snapshot);

        await using var phone = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(
            phone.Database,
            phone.Repository);

        await store.RestoreAsync(snapshot);
        var upload = await store.ExportAsync(
            "5d41402abc4b2a76b9719d911017c592",
            "Pixel 8");

        // Fails the same way the server does when the phone uploads.
        UserSyncDocumentValidator.Validate(upload);
    }

    [Fact]
    public async Task FreshDevice_RestoreThenExport_KeepsTheAccountState()
    {
        var snapshot = await CreateAccountSnapshotAsync();

        await using var phone = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(
            phone.Database,
            phone.Repository);
        var restore = await store.RestoreAsync(snapshot);
        var upload = await store.ExportAsync("phone-device", "Pixel 8");

        Assert.True(restore.CatalogCreatedCount > 0);
        Assert.NotEmpty(upload.Catalog.Items);
        Assert.Single(upload.Progress);
        Assert.Single(upload.Lists);
    }

    private static async Task<UserSyncDocument> CreateAccountSnapshotAsync()
    {
        await using var desktop = TestDatabaseScope.Create();
        await desktop.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        var book = Assert.Single(
            await desktop.Repository.ListAsync(MediaType.Book));
        var details = Assert.IsType<Application.Catalog.CatalogItemDetails>(
            await desktop.Repository.GetDetailsAsync(book.Id));
        var unit = Assert.Single(details.Units);
        var state = new SqliteUserMediaStateStore(desktop.Database);
        await state.SaveProgressAsync(
            unit.Identity,
            new MediaProgressState(
                12,
                MediaPositionKind.Page,
                isCompleted: false,
                DateTimeOffset.UtcNow));
        await state.SaveReaderDisplayModeAsync(
            details.Identity,
            ReaderDisplayMode.Continuous);
        await state.SetFavoriteCatalogItemAsync(book.Id, isFavorite: true);
        await state.SetFavoriteContributorAsync(
            Assert.Single(book.Contributors).StableKey,
            isFavorite: true);
        var list = await state.CreateUserListAsync("Read later");
        await state.SetUserListMembershipAsync(
            list.Id,
            book.Id,
            isMember: true);

        return await new SqliteUserSyncStore(
                desktop.Database,
                desktop.Repository)
            .ExportAsync("desktop-device", "Desktop");
    }
}
