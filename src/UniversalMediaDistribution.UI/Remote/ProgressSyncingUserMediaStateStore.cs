using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.UI.Remote;

/// <summary>
/// A user-state store that reports every saved position, so the application can
/// upload a snapshot without each caller having to remember to ask.
/// <para>
/// Wrapping the store rather than calling out from the readers is what makes
/// this complete: positions are written from the image reader, the book reader
/// and the video player, and any of the three left out would be a place where
/// a friend's view of you silently stopped moving.
/// </para>
/// </summary>
public sealed class ProgressSyncingUserMediaStateStore(
    IUserMediaStateStore inner,
    Action<CatalogContentIdentity> onProgressSaved) : IUserMediaStateStore
{
    private readonly IUserMediaStateStore _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    private readonly Action<CatalogContentIdentity> _onProgressSaved =
        onProgressSaved ?? throw new ArgumentNullException(
            nameof(onProgressSaved));

    public async ValueTask SaveProgressAsync(
        CatalogContentIdentity content,
        MediaProgressState progress,
        CancellationToken cancellationToken = default)
    {
        // SQLite's async methods perform synchronous I/O. Keep writes off the
        // player/reader dispatcher, then announce progress on the caller's
        // context so the UI-owned sync scheduler keeps its thread affinity.
        await Task.Run(() => _inner.SaveProgressAsync(content, progress, cancellationToken).AsTask(), cancellationToken);

        // After the write, so a failed save never announces progress that was
        // not kept. The identity travels with it: a patch can only carry the
        // titles it knows have moved.
        _onProgressSaved(content);
    }

    public ValueTask<ReaderDevicePreferences> LoadReaderDevicePreferencesAsync(
        CancellationToken cancellationToken = default) =>
        _inner.LoadReaderDevicePreferencesAsync(cancellationToken);

    public ValueTask SaveReaderDevicePreferencesAsync(
        ReaderDevicePreferences preferences,
        CancellationToken cancellationToken = default) =>
        _inner.SaveReaderDevicePreferencesAsync(
            preferences,
            cancellationToken);

    public ValueTask<MediaProgressState?> LoadProgressAsync(
        CatalogContentIdentity content,
        CancellationToken cancellationToken = default) =>
        _inner.LoadProgressAsync(content, cancellationToken);

    public ValueTask<IReadOnlyList<CatalogProgressSummary>>
        LoadCatalogProgressAsync(
            CancellationToken cancellationToken = default) =>
        _inner.LoadCatalogProgressAsync(cancellationToken);

    public ValueTask<ReaderDisplayMode> LoadReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        CancellationToken cancellationToken = default) =>
        _inner.LoadReaderDisplayModeAsync(catalogItem, cancellationToken);

    public ValueTask SaveReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        ReaderDisplayMode displayMode,
        CancellationToken cancellationToken = default) =>
        _inner.SaveReaderDisplayModeAsync(
            catalogItem,
            displayMode,
            cancellationToken);

    public ValueTask<IReadOnlySet<string>> LoadFavoriteContributorKeysAsync(
        CancellationToken cancellationToken = default) =>
        _inner.LoadFavoriteContributorKeysAsync(cancellationToken);

    public ValueTask SetFavoriteContributorAsync(
        string contributorKey,
        bool isFavorite,
        CancellationToken cancellationToken = default) =>
        _inner.SetFavoriteContributorAsync(
            contributorKey,
            isFavorite,
            cancellationToken);

    public ValueTask<IReadOnlySet<Guid>> LoadFavoriteCatalogItemIdsAsync(
        CancellationToken cancellationToken = default) =>
        _inner.LoadFavoriteCatalogItemIdsAsync(cancellationToken);

    public ValueTask SetFavoriteCatalogItemAsync(
        Guid catalogItemId,
        bool isFavorite,
        CancellationToken cancellationToken = default) =>
        _inner.SetFavoriteCatalogItemAsync(
            catalogItemId,
            isFavorite,
            cancellationToken);

    public ValueTask<IReadOnlyList<UserMediaList>> LoadUserListsAsync(
        CancellationToken cancellationToken = default) =>
        _inner.LoadUserListsAsync(cancellationToken);

    public ValueTask<UserMediaList> CreateUserListAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        _inner.CreateUserListAsync(name, cancellationToken);

    public ValueTask<UserMediaList> RenameUserListAsync(
        Guid listId,
        string name,
        CancellationToken cancellationToken = default) =>
        _inner.RenameUserListAsync(listId, name, cancellationToken);

    public ValueTask DeleteUserListAsync(
        Guid listId,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteUserListAsync(listId, cancellationToken);

    public ValueTask SetUserListMembershipAsync(
        Guid listId,
        Guid catalogItemId,
        bool isMember,
        CancellationToken cancellationToken = default) =>
        _inner.SetUserListMembershipAsync(
            listId,
            catalogItemId,
            isMember,
            cancellationToken);
}
