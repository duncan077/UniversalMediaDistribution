using System.Collections.Frozen;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Application.UserState;

public enum MediaPositionKind
{
    Seconds = 1,
    Page = 2
}

public sealed record MediaProgressState
{
    public MediaProgressState(
        double position,
        MediaPositionKind positionKind,
        bool isCompleted,
        DateTimeOffset updatedAt)
    {
        if (!double.IsFinite(position) || position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!Enum.IsDefined(positionKind))
        {
            throw new ArgumentOutOfRangeException(nameof(positionKind));
        }

        Position = position;
        PositionKind = positionKind;
        IsCompleted = isCompleted;
        UpdatedAt = updatedAt;
    }

    public double Position { get; }

    public MediaPositionKind PositionKind { get; }

    public bool IsCompleted { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public sealed record CatalogProgressSummary(
    CatalogContentIdentity Content,
    MediaProgressState Progress,
    /// <summary>
    /// How many units the title has past the one this progress sits on, so a
    /// card can say a chapter arrived after the one you stopped at. Zero when
    /// the progress is on the title itself rather than a unit, since there is
    /// then nothing to be past.
    /// </summary>
    int LaterUnitCount = 0,
    /// <summary>
    /// The number of the title's newest unit, for naming what is waiting.
    /// </summary>
    string? LatestUnitNumber = null)
{
    public Guid CatalogItemId => Content.CatalogItemId;

    public bool HasLaterUnits => LaterUnitCount > 0;
}

public sealed record UserMediaList
{
    public const int MaximumNameLength = 80;
    public const int MaximumListCount = 100;

    public UserMediaList(
        Guid id,
        string name,
        IEnumerable<Guid> catalogItemIds,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A user media list requires an ID.",
                nameof(id));
        }

        ArgumentNullException.ThrowIfNull(catalogItemIds);
        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A user media list cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        Name = NormalizeName(name);
        CatalogItemIds = catalogItemIds
            .Where(static itemId => itemId != Guid.Empty)
            .ToFrozenSet();
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public string Name { get; }

    public IReadOnlySet<Guid> CatalogItemIds { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public int ItemCount => CatalogItemIds.Count;

    public bool Contains(Guid catalogItemId) =>
        CatalogItemIds.Contains(catalogItemId);

    public static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = string.Join(
            ' ',
            name.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
        if (normalized.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"A list name cannot exceed {MaximumNameLength} characters.",
                nameof(name));
        }

        return normalized;
    }

    public static string GetNameKey(string name) =>
        NameNormalizer.Normalize(NormalizeName(name));
}

/// <summary>
/// Reading preferences that belong to this device rather than to a title: the
/// zoom the reader opens at and the direction the chapter list is sorted in.
/// They are deliberately not part of a sync snapshot, because a phone and a
/// television want different zoom levels.
/// </summary>
public sealed record ReaderDevicePreferences(
    double ZoomFactor,
    bool ChapterOrderDescending,
    ReaderDisplayMode DisplayMode = ReaderDisplayMode.Paged)
{
    public const double MinimumZoomFactor = 0.5;
    public const double MaximumZoomFactor = 2;

    public static ReaderDevicePreferences Default { get; } =
        new(1, ChapterOrderDescending: false, ReaderDisplayMode.Paged);

    public ReaderDevicePreferences Normalize() =>
        this with
        {
            ZoomFactor = double.IsFinite(ZoomFactor)
                ? Math.Round(
                    Math.Clamp(
                        ZoomFactor,
                        MinimumZoomFactor,
                        MaximumZoomFactor),
                    2)
                : Default.ZoomFactor
        };
}

public interface IUserMediaStateStore
{
    /// <summary>
    /// Device-scoped reading preferences. The default implementation keeps the
    /// built-in defaults, so a store without durable settings still works.
    /// </summary>
    ValueTask<ReaderDevicePreferences> LoadReaderDevicePreferencesAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ReaderDevicePreferences.Default);

    ValueTask SaveReaderDevicePreferencesAsync(
        ReaderDevicePreferences preferences,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    ValueTask<MediaProgressState?> LoadProgressAsync(
        CatalogContentIdentity content,
        CancellationToken cancellationToken = default);

    ValueTask SaveProgressAsync(
        CatalogContentIdentity content,
        MediaProgressState progress,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CatalogProgressSummary>>
        LoadCatalogProgressAsync(
            CancellationToken cancellationToken = default);

    ValueTask<ReaderDisplayMode> LoadReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        CancellationToken cancellationToken = default);

    ValueTask SaveReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        ReaderDisplayMode displayMode,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlySet<string>> LoadFavoriteContributorKeysAsync(
        CancellationToken cancellationToken = default);

    ValueTask SetFavoriteContributorAsync(
        string contributorKey,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlySet<Guid>> LoadFavoriteCatalogItemIdsAsync(
        CancellationToken cancellationToken = default);

    ValueTask SetFavoriteCatalogItemAsync(
        Guid catalogItemId,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<UserMediaList>> LoadUserListsAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<UserMediaList>>([]);

    ValueTask<UserMediaList> CreateUserListAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Named media lists are not supported by this user-state store.");

    ValueTask<UserMediaList> RenameUserListAsync(
        Guid listId,
        string name,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Named media lists are not supported by this user-state store.");

    ValueTask DeleteUserListAsync(
        Guid listId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Named media lists are not supported by this user-state store.");

    ValueTask SetUserListMembershipAsync(
        Guid listId,
        Guid catalogItemId,
        bool isMember,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Named media lists are not supported by this user-state store.");
}

public sealed class InMemoryUserMediaStateStore : IUserMediaStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProgressEntry> _progress =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReaderDisplayMode> _readerModes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _favoriteContributorKeys = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _favoriteCatalogItemIds = [];
    private readonly Dictionary<Guid, UserMediaList> _userLists = [];

    public ValueTask<MediaProgressState?> LoadProgressAsync(
        CatalogContentIdentity content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _progress.GetValueOrDefault(content.StableKey)?.Progress);
        }
    }

    public ValueTask SaveProgressAsync(
        CatalogContentIdentity content,
        MediaProgressState progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _progress[content.StableKey] = new ProgressEntry(content, progress);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<CatalogProgressSummary>>
        LoadCatalogProgressAsync(
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<CatalogProgressSummary>>(
                _progress.Values
                    .OrderByDescending(
                        static entry => entry.Progress.UpdatedAt)
                    .Select(static entry => new CatalogProgressSummary(
                        entry.Content,
                        entry.Progress))
                    .ToArray());
        }
    }

    public ValueTask<ReaderDisplayMode> LoadReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        CancellationToken cancellationToken = default)
    {
        EnsureCatalogItem(catalogItem);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _readerModes.GetValueOrDefault(catalogItem.StableKey, ReaderDisplayMode.Paged));
        }
    }

    public ValueTask SaveReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        ReaderDisplayMode displayMode,
        CancellationToken cancellationToken = default)
    {
        EnsureCatalogItem(catalogItem);
        if (!Enum.IsDefined(displayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(displayMode));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _readerModes[catalogItem.StableKey] = displayMode;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlySet<string>> LoadFavoriteContributorKeysAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(
                    _favoriteContributorKeys,
                    StringComparer.Ordinal));
        }
    }

    public ValueTask SetFavoriteContributorAsync(
        string contributorKey,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contributorKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (isFavorite)
            {
                _favoriteContributorKeys.Add(contributorKey.Trim());
            }
            else
            {
                _favoriteContributorKeys.Remove(contributorKey.Trim());
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlySet<Guid>> LoadFavoriteCatalogItemIdsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlySet<Guid>>(
                _favoriteCatalogItemIds.ToHashSet());
        }
    }

    public ValueTask SetFavoriteCatalogItemAsync(
        Guid catalogItemId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A favorite requires a catalog item ID.",
                nameof(catalogItemId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (isFavorite)
            {
                _favoriteCatalogItemIds.Add(catalogItemId);
            }
            else
            {
                _favoriteCatalogItemIds.Remove(catalogItemId);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<UserMediaList>> LoadUserListsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<UserMediaList>>(
                _userLists.Values
                    .OrderBy(static list => list.CreatedAt)
                    .ThenBy(static list => list.Name, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public ValueTask<UserMediaList> CreateUserListAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = UserMediaList.NormalizeName(name);
        var nameKey = UserMediaList.GetNameKey(normalizedName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_userLists.Count >= UserMediaList.MaximumListCount)
            {
                throw new InvalidOperationException(
                    $"No more than {UserMediaList.MaximumListCount} lists may be created.");
            }

            EnsureUniqueListName(nameKey);
            var now = DateTimeOffset.UtcNow;
            var list = new UserMediaList(
                Guid.CreateVersion7(),
                normalizedName,
                [],
                now,
                now);
            _userLists.Add(list.Id, list);
            return ValueTask.FromResult(list);
        }
    }

    public ValueTask<UserMediaList> RenameUserListAsync(
        Guid listId,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureListId(listId);
        var normalizedName = UserMediaList.NormalizeName(name);
        var nameKey = UserMediaList.GetNameKey(normalizedName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = GetList(listId);
            EnsureUniqueListName(nameKey, listId);
            var renamed = new UserMediaList(
                current.Id,
                normalizedName,
                current.CatalogItemIds,
                current.CreatedAt,
                DateTimeOffset.UtcNow);
            _userLists[listId] = renamed;
            return ValueTask.FromResult(renamed);
        }
    }

    public ValueTask DeleteUserListAsync(
        Guid listId,
        CancellationToken cancellationToken = default)
    {
        EnsureListId(listId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_userLists.Remove(listId))
            {
                throw new KeyNotFoundException(
                    $"User media list '{listId:D}' was not found.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SetUserListMembershipAsync(
        Guid listId,
        Guid catalogItemId,
        bool isMember,
        CancellationToken cancellationToken = default)
    {
        EnsureListId(listId);
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "List membership requires a catalog item ID.",
                nameof(catalogItemId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = GetList(listId);
            var itemIds = current.CatalogItemIds.ToHashSet();
            var changed = isMember
                ? itemIds.Add(catalogItemId)
                : itemIds.Remove(catalogItemId);
            if (changed)
            {
                _userLists[listId] = new UserMediaList(
                    current.Id,
                    current.Name,
                    itemIds,
                    current.CreatedAt,
                    DateTimeOffset.UtcNow);
            }
        }

        return ValueTask.CompletedTask;
    }

    private UserMediaList GetList(Guid listId) =>
        _userLists.GetValueOrDefault(listId) ??
        throw new KeyNotFoundException(
            $"User media list '{listId:D}' was not found.");

    private void EnsureUniqueListName(
        string nameKey,
        Guid? exceptListId = null)
    {
        if (_userLists.Values.Any(list =>
                list.Id != exceptListId &&
                string.Equals(
                    UserMediaList.GetNameKey(list.Name),
                    nameKey,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "A list with this name already exists.");
        }
    }

    private static void EnsureListId(Guid listId)
    {
        if (listId == Guid.Empty)
        {
            throw new ArgumentException(
                "A user media list requires an ID.",
                nameof(listId));
        }
    }

    private static void EnsureCatalogItem(CatalogContentIdentity content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.IsUnit)
        {
            throw new ArgumentException("Reader display mode belongs to the catalog item, not a content unit.", nameof(content));
        }
    }

    private sealed record ProgressEntry(
        CatalogContentIdentity Content,
        MediaProgressState Progress);
}
