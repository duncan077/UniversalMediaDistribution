using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Catalog.Sync;

public sealed record UserSyncDocument
{
    public const string CurrentFormat =
        "universal-media-distribution.user-sync";
    public const int MinimumSupportedVersion = 1;
    public const int CurrentVersion = 4;

    public string Format { get; init; } = CurrentFormat;

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset ExportedAt { get; init; } =
        DateTimeOffset.UtcNow;

    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required CatalogTransferDocument Catalog { get; init; }

    public IReadOnlyList<UserSyncProgressEntry> Progress { get; init; } = [];

    public IReadOnlyList<UserSyncReaderPreference> ReaderPreferences
    {
        get;
        init;
    } = [];

    public IReadOnlyList<string> FavoriteContributorKeys { get; init; } = [];

    public IReadOnlyList<UserSyncFavoriteState> FavoriteStates
    {
        get;
        init;
    } = [];

    public IReadOnlyList<UserSyncFavoriteItemState> FavoriteItemStates
    {
        get;
        init;
    } = [];

    public IReadOnlyList<UserSyncMediaList> Lists { get; init; } = [];

    /// <summary>
    /// Identity redirects: forwarding notes left when two items that carried
    /// different sync IDs turned out to be one title and were folded. Not
    /// alternative names — a device that holds records under the losing ID
    /// follows the note and repoints them at the survivor.
    /// </summary>
    public IReadOnlyList<UserSyncCatalogRedirect> CatalogRedirects
    {
        get;
        init;
    } = [];
}

public sealed record UserSyncVersion
{
    public long Clock { get; init; }

    public required string DeviceId { get; init; }

    public Guid OperationId { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }
}

public sealed record UserSyncCatalogRedirect
{
    public required Guid FromId { get; init; }

    public required Guid ToId { get; init; }

    public required UserSyncVersion SyncVersion { get; init; }
}

public sealed record UserSyncCatalogItemIdentity
{
    public required string MediaType { get; init; }

    public string? Subtype { get; init; }

    /// <summary>
    /// The referenced item's sync identity. Resolution prefers this and only
    /// falls back to the name, so a record keeps pointing at its title
    /// through renames and merges. Null on records written before identities
    /// existed.
    /// </summary>
    public Guid? ItemId { get; init; }

    public required string Name { get; init; }
}

public sealed record UserSyncContentIdentity
{
    public required UserSyncCatalogItemIdentity Item { get; init; }

    public string? UnitKind { get; init; }

    public string? DisplayNumber { get; init; }

    public int? SeasonNumber { get; init; }
}

public sealed record UserSyncProgressEntry
{
    public required UserSyncContentIdentity Content { get; init; }

    public double Position { get; init; }

    public required string PositionKind { get; init; }

    public bool IsCompleted { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public UserSyncVersion? SyncVersion { get; init; }
}

public sealed record UserSyncReaderPreference
{
    public required UserSyncCatalogItemIdentity Item { get; init; }

    public required string DisplayMode { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public UserSyncVersion? SyncVersion { get; init; }
}

public sealed record UserSyncFavoriteState
{
    public required string ContributorKey { get; init; }

    public bool IsFavorite { get; init; }

    public required UserSyncVersion SyncVersion { get; init; }
}

public sealed record UserSyncFavoriteItemState
{
    public required UserSyncCatalogItemIdentity Item { get; init; }

    public bool IsFavorite { get; init; }

    public required UserSyncVersion SyncVersion { get; init; }
}

public sealed record UserSyncListItemState
{
    public required UserSyncCatalogItemIdentity Item { get; init; }

    public bool IsMember { get; init; }

    public required UserSyncVersion SyncVersion { get; init; }
}

public sealed record UserSyncMediaList
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<UserSyncCatalogItemIdentity> Items { get; init; } = [];

    public bool IsDeleted { get; init; }

    public UserSyncVersion? SyncVersion { get; init; }

    public IReadOnlyList<UserSyncListItemState> ItemStates { get; init; } = [];
}

public sealed record UserSyncRestoreResult(
    int CatalogCreatedCount,
    int CatalogUpdatedCount,
    int ProgressCount,
    int ReaderPreferenceCount,
    int FavoriteContributorCount,
    int ListCount);

public interface IUserSyncStore
{
    ValueTask<UserSyncDocument> ExportAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default);

    ValueTask<UserSyncRestoreResult> RestoreAsync(
        UserSyncDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports only the positions belonging to the named titles.
    /// <para>
    /// Defaulted to unsupported so a store without it keeps working: the
    /// caller falls back to a full export, which is what it did before patches
    /// existed.
    /// </para>
    /// </summary>
    ValueTask<UserSyncDocument> ExportProgressPatchAsync(
        string deviceId,
        string deviceName,
        IReadOnlyCollection<Guid> catalogItemIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This user-sync store cannot export a progress patch.");
}

public static class UserSyncDocumentValidator
{
    public const int MaximumProgressCount = 250_000;
    public const int MaximumFavoriteContributorCount = 25_000;
    public const int MaximumFavoriteStateCount = 100_000;
    public const int MaximumFavoriteItemStateCount = 250_000;
    public const int MaximumReaderPreferenceCount = 100_000;
    public const int MaximumCatalogRedirectCount = 250_000;
    public const int MaximumListItemCount = 100_000;
    public const int MaximumListStateCount = 10_000;
    public const int MaximumDeviceIdLength = 128;
    public const int MaximumDeviceNameLength = 100;

    public static void Validate(
        UserSyncDocument? document,
        bool allowUnresolvedCatalogReferences = false,
        bool allowDuplicateCatalogReferences = false)
    {
        if (document is null)
        {
            throw new InvalidDataException("The sync document is empty.");
        }

        if (!string.Equals(
                document.Format,
                UserSyncDocument.CurrentFormat,
                StringComparison.Ordinal) ||
            document.Version is <
                UserSyncDocument.MinimumSupportedVersion or
                > UserSyncDocument.CurrentVersion)
        {
            throw new InvalidDataException(
                "The sync document format or version is unsupported.");
        }

        ValidateDeviceId(document.DeviceId);
        ValidateDeviceName(document.DeviceName);
        if (document.ExportedAt == default)
        {
            throw new InvalidDataException(
                "The sync document requires an export timestamp.");
        }

        // A snapshot is merged from several devices, each of which keyed the
        // same title by its own id. Two spellings landing on one normalized
        // name is ordinary there, and the importer collapses them on the way
        // in, so rejecting the whole snapshot for it only strands the account.
        CatalogTransferValidator.Validate(
            document.Catalog,
            allowMissingTorrentIdentity: false,
            allowDuplicateItemNames: true);
        ArgumentNullException.ThrowIfNull(document.Progress);
        ArgumentNullException.ThrowIfNull(document.ReaderPreferences);
        ArgumentNullException.ThrowIfNull(document.FavoriteContributorKeys);
        ArgumentNullException.ThrowIfNull(document.FavoriteStates);
        ArgumentNullException.ThrowIfNull(document.FavoriteItemStates);
        ArgumentNullException.ThrowIfNull(document.Lists);
        ArgumentNullException.ThrowIfNull(document.CatalogRedirects);

        if (document.Progress.Count > MaximumProgressCount ||
            document.ReaderPreferences.Count >
                MaximumReaderPreferenceCount ||
            document.FavoriteContributorKeys.Count >
                (document.Version >= 2
                    ? MaximumFavoriteStateCount
                    : MaximumFavoriteContributorCount) ||
            document.FavoriteStates.Count >
                MaximumFavoriteStateCount ||
            document.FavoriteItemStates.Count >
                MaximumFavoriteItemStateCount ||
            document.Lists.Count > MaximumListStateCount ||
            document.CatalogRedirects.Count > MaximumCatalogRedirectCount)
        {
            throw new InvalidDataException(
                "The sync document exceeds a supported collection limit.");
        }

        var redirectTargets = new Dictionary<Guid, Guid>();
        foreach (var redirect in document.CatalogRedirects)
        {
            if (redirect is null ||
                redirect.FromId == Guid.Empty ||
                redirect.ToId == Guid.Empty ||
                redirect.FromId == redirect.ToId)
            {
                throw new InvalidDataException(
                    "The sync document contains an invalid catalog redirect.");
            }

            ValidateSyncVersion(redirect.SyncVersion);
            if (!redirectTargets.TryAdd(redirect.FromId, redirect.ToId))
            {
                throw new InvalidDataException(
                    "The sync document contains a duplicate catalog redirect.");
            }
        }

        var (catalogItemKeys,
                catalogContentKeys,
                catalogItemKeysById,
                deletedItemKeys) =
            CreateCatalogIdentitySets(document.Catalog);
        var progressKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var progress in document.Progress)
        {
            if (progress is null ||
                !double.IsFinite(progress.Position) ||
                progress.Position < 0 ||
                progress.UpdatedAt == default ||
                !string.Equals(
                    progress.PositionKind,
                    "seconds",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    progress.PositionKind,
                    "page",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The sync document contains invalid progress.");
            }

            var key = ValidateContentIdentity(progress.Content);
            var progressItemKey = ValidateItemIdentity(progress.Content.Item);
            var resolvedProgressItem = ResolveItemKey(
                progress.Content.Item,
                progressItemKey,
                catalogItemKeys,
                catalogItemKeysById,
                redirectTargets);
            if (resolvedProgressItem is not null &&
                !string.Equals(
                    resolvedProgressItem,
                    progressItemKey,
                    StringComparison.Ordinal))
            {
                key = string.Concat(
                    resolvedProgressItem,
                    key.AsSpan(progressItemKey.Length));
            }

            if (!allowUnresolvedCatalogReferences &&
                (resolvedProgressItem is null ||
                (!catalogContentKeys.Contains(key) &&
                 !deletedItemKeys.Contains(resolvedProgressItem))))
            {
                throw new InvalidDataException(
                    $"Synchronized progress target '{key}' is not in the catalog.");
            }

            if (document.Version >= 2)
            {
                ValidateSyncVersion(progress.SyncVersion);
            }

            if (!progressKeys.Add(key) && !allowDuplicateCatalogReferences)
            {
                throw new InvalidDataException(
                    "The sync document contains duplicate progress.");
            }
        }

        var readerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preference in document.ReaderPreferences)
        {
            if (preference is null ||
                preference.UpdatedAt == default ||
                !string.Equals(
                    preference.DisplayMode,
                    "paged",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    preference.DisplayMode,
                    "continuous",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The sync document contains an invalid reader preference.");
            }

            var key = ValidateItemIdentity(preference.Item);
            var resolvedReader = ResolveItemKey(
                    preference.Item,
                    key,
                    catalogItemKeys,
                    catalogItemKeysById,
                    redirectTargets);
            if (resolvedReader is null && !allowUnresolvedCatalogReferences)
            {
                throw new InvalidDataException(
                    $"Synchronized reader target '{key}' is not in the catalog.");
            }
            key = resolvedReader ?? key;

            if (document.Version >= 2)
            {
                ValidateSyncVersion(preference.SyncVersion);
            }

            if (!readerKeys.Add(key) && !allowDuplicateCatalogReferences)
            {
                throw new InvalidDataException(
                    "The sync document contains duplicate reader preferences.");
            }
        }

        var favoriteKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var favorite in document.FavoriteContributorKeys)
        {
            if (string.IsNullOrWhiteSpace(favorite) ||
                favorite.Length > 512 ||
                !favoriteKeys.Add(favorite.Trim()))
            {
                throw new InvalidDataException(
                    "The sync document contains an invalid or duplicate favorite provider.");
            }
        }

        var favoriteStateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var favorite in document.FavoriteStates)
        {
            if (favorite is null ||
                string.IsNullOrWhiteSpace(favorite.ContributorKey) ||
                favorite.ContributorKey.Length > 512 ||
                !favoriteStateKeys.Add(favorite.ContributorKey.Trim()))
            {
                throw new InvalidDataException(
                    "The sync document contains an invalid or duplicate favorite state.");
            }

            ValidateSyncVersion(favorite.SyncVersion);
        }

        var favoriteItemKeys =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (var favorite in document.FavoriteItemStates)
        {
            if (favorite is null)
            {
                throw new InvalidDataException(
                    "The sync document contains an invalid favorite item state.");
            }

            var itemKey = ValidateItemIdentity(favorite.Item);
            var resolvedFavorite = ResolveItemKey(
                favorite.Item,
                itemKey,
                catalogItemKeys,
                catalogItemKeysById,
                redirectTargets);
            if (!favoriteItemKeys.Add(resolvedFavorite ?? itemKey) &&
                !allowDuplicateCatalogReferences)
            {
                throw new InvalidDataException(
                    "The sync document contains a duplicate favorite item state.");
            }

            if (favorite.IsFavorite && resolvedFavorite is null &&
                !allowUnresolvedCatalogReferences)
            {
                throw new InvalidDataException(
                    $"Synchronized favorite target '{itemKey}' is not in the catalog.");
            }

            ValidateSyncVersion(favorite.SyncVersion);
        }

        var listIds = new HashSet<Guid>();
        var listNames = new HashSet<string>(StringComparer.Ordinal);
        var totalListItems = 0;
        var liveListCount = 0;
        foreach (var list in document.Lists)
        {
            if (list is null ||
                list.Id == Guid.Empty ||
                !listIds.Add(list.Id))
            {
                throw new InvalidDataException(
                    "The sync document contains an invalid or duplicate list.");
            }

            if (document.Version >= 2)
            {
                ValidateSyncVersion(list.SyncVersion);
            }

            ArgumentNullException.ThrowIfNull(list.Items);
            ArgumentNullException.ThrowIfNull(list.ItemStates);
            var normalizedName = string.Empty;
            if (!list.IsDeleted)
            {
                if (list.CreatedAt == default ||
                    list.UpdatedAt < list.CreatedAt)
                {
                    throw new InvalidDataException(
                        "The sync document contains invalid list timestamps.");
                }

                normalizedName = UserMediaList.NormalizeName(list.Name);
                if (!listNames.Add(UserMediaList.GetNameKey(normalizedName)) &&
                    !allowDuplicateCatalogReferences)
                {
                    throw new InvalidDataException(
                        "The sync document contains duplicate list names.");
                }

                liveListCount++;
            }

            totalListItems = checked(
                totalListItems +
                (document.Version >= 2
                    ? list.ItemStates.Count
                    : list.Items.Count));
            if (totalListItems > MaximumListItemCount)
            {
                throw new InvalidDataException(
                    "The sync document contains too many list items.");
            }

            var itemKeys = new HashSet<string>(StringComparer.Ordinal);
            if (document.Version >= 2)
            {
                foreach (var itemState in list.ItemStates)
                {
                    if (itemState is null)
                    {
                        throw new InvalidDataException(
                            "The sync document contains an invalid list item state.");
                    }

                    var itemKey = ValidateItemIdentity(itemState.Item);
                    var resolvedListItem = ResolveItemKey(
                        itemState.Item,
                        itemKey,
                        catalogItemKeys,
                        catalogItemKeysById,
                        redirectTargets);
                    ValidateSyncVersion(itemState.SyncVersion);
                    if (!itemKeys.Add(resolvedListItem ?? itemKey) &&
                        !allowDuplicateCatalogReferences)
                    {
                        throw new InvalidDataException(
                            $"The list '{normalizedName}' contains a duplicate item state.");
                    }

                    if (!list.IsDeleted &&
                        itemState.IsMember &&
                        resolvedListItem is null &&
                        !allowUnresolvedCatalogReferences)
                    {
                        throw new InvalidDataException(
                            $"Synchronized list target '{itemKey}' is not in the catalog.");
                    }
                }
            }
            else
            {
                foreach (var item in list.Items)
                {
                    var itemKey = ValidateItemIdentity(item);
                    var resolvedListItem = ResolveItemKey(
                            item,
                            itemKey,
                            catalogItemKeys,
                            catalogItemKeysById,
                            redirectTargets);
                    if (resolvedListItem is null && !allowUnresolvedCatalogReferences)
                    {
                        throw new InvalidDataException(
                            $"Synchronized list target '{itemKey}' is not in the catalog.");
                    }
                    itemKey = resolvedListItem ?? itemKey;

                    if (!itemKeys.Add(itemKey) && !allowDuplicateCatalogReferences)
                    {
                        throw new InvalidDataException(
                            $"The list '{normalizedName}' contains a duplicate item.");
                    }
                }
            }
        }

        if (document.Version == 1 &&
            liveListCount > UserMediaList.MaximumListCount)
        {
            throw new InvalidDataException(
                "The sync document contains too many active lists.");
        }
    }

    public static void ValidateSyncVersion(UserSyncVersion? version)
    {
        if (version is null ||
            version.Clock is <= 0 or long.MaxValue ||
            version.OperationId == Guid.Empty ||
            version.ModifiedAt == default)
        {
            throw new InvalidDataException(
                "The sync document contains an invalid record version.");
        }

        ValidateDeviceId(version.DeviceId);
    }

    /// <summary>
    /// Finds the catalog key a reference actually points at: by sync identity
    /// first, through any redirect chain, and only then by the name — which is
    /// how records from before identities existed still find their title.
    /// Null when nothing in the catalog answers to it.
    /// </summary>
    private static string? ResolveItemKey(
        UserSyncCatalogItemIdentity identity,
        string itemKey,
        HashSet<string> itemKeys,
        Dictionary<Guid, string> itemKeysById,
        Dictionary<Guid, Guid> redirectTargets)
    {
        if (identity.ItemId is { } itemId)
        {
            var hops = 0;
            var current = itemId;
            while (true)
            {
                if (itemKeysById.TryGetValue(current, out var resolved) &&
                    (identity.Subtype is null || !identity.MediaType.Equals("book", StringComparison.OrdinalIgnoreCase) ||
                        resolved[..resolved.IndexOf(':')] == itemKey[..itemKey.IndexOf(':')]))
                {
                    return resolved;
                }

                if (!redirectTargets.TryGetValue(current, out var next) ||
                    ++hops > redirectTargets.Count)
                {
                    break;
                }

                current = next;
            }
        }

        if (itemKeys.Contains(itemKey)) return itemKey;
        if (identity.Subtype is null && identity.MediaType.Equals("book", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = itemKey[itemKey.IndexOf(':')..];
            var matches = itemKeys.Where(key => key.StartsWith("book.", StringComparison.Ordinal) &&
                key[key.IndexOf(':')..] == suffix).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        return null;
    }

    private static (
            HashSet<string> ItemKeys,
            HashSet<string> ContentKeys,
            Dictionary<Guid, string> ItemKeysById,
            HashSet<string> DeletedItemKeys)
        CreateCatalogIdentitySets(CatalogTransferDocument catalog)
    {
        var itemKeys = new HashSet<string>(StringComparer.Ordinal);
        var contentKeys = new HashSet<string>(StringComparer.Ordinal);
        var itemKeysById = new Dictionary<Guid, string>();
        var deletedItemKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in catalog.Items)
        {
            var identity = new UserSyncCatalogItemIdentity
            {
                MediaType = item.MediaType,
                Subtype = item.Subtype,
                Name = item.Name
            };
            var itemKey = ValidateItemIdentity(identity);
            itemKeys.Add(itemKey);
            contentKeys.Add(itemKey);
            if (item.Deleted)
            {
                // A record still naming a deleted title is stale, not
                // invalid: it rides along and dies at import, where the
                // title it points at no longer exists.
                deletedItemKeys.Add(itemKey);
            }

            if (item.Id is { } syncId)
            {
                itemKeysById.TryAdd(syncId, itemKey);
            }
            foreach (var unit in item.Units)
            {
                contentKeys.Add(ValidateContentIdentity(
                    new UserSyncContentIdentity
                    {
                        Item = identity,
                        UnitKind = unit.Kind,
                        DisplayNumber = unit.DisplayNumber
                    }));
            }

            foreach (var season in item.Seasons)
            {
                foreach (var unit in season.Units)
                {
                    contentKeys.Add(ValidateContentIdentity(
                        new UserSyncContentIdentity
                        {
                            Item = identity,
                            UnitKind = unit.Kind,
                            DisplayNumber = unit.DisplayNumber,
                            SeasonNumber = season.Number
                        }));
                }
            }
        }

        return (itemKeys, contentKeys, itemKeysById, deletedItemKeys);
    }

    public static string ValidateItemIdentity(
        UserSyncCatalogItemIdentity? identity)
    {
        if (identity is null ||
            string.IsNullOrWhiteSpace(identity.Name))
        {
            throw new InvalidDataException(
                "A synchronized catalog identity requires a title.");
        }

        var mediaType = CatalogSubtypeResolver.ParseMediaType(
            identity.MediaType);
        return CreateItemKey(mediaType, identity.Name, identity.Subtype);
    }

    public static string ValidateContentIdentity(
        UserSyncContentIdentity? identity)
    {
        if (identity is null)
        {
            throw new InvalidDataException(
                "A synchronized content identity is required.");
        }

        var itemKey = ValidateItemIdentity(identity.Item);
        var hasUnitKind = !string.IsNullOrWhiteSpace(identity.UnitKind);
        var hasDisplayNumber =
            !string.IsNullOrWhiteSpace(identity.DisplayNumber);
        if (hasUnitKind != hasDisplayNumber ||
            identity.SeasonNumber is < 0)
        {
            throw new InvalidDataException(
                "A synchronized unit identity is incomplete.");
        }

        if (!hasUnitKind)
        {
            if (identity.SeasonNumber is not null)
            {
                throw new InvalidDataException(
                    "An item identity cannot include a season.");
            }

            return itemKey;
        }

        var unitKind = ParseUnitKind(identity.UnitKind!);
        return string.Join(
            ':',
            itemKey,
            UnitKindToValue(unitKind),
            identity.SeasonNumber?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            NameNormalizer.Normalize(identity.DisplayNumber!));
    }

    public static string CreateItemKey(
        MediaType mediaType,
        string name,
        string? subtype = null) =>
        ContentItemKey.Create(mediaType, name, subtype is null ? null :
            CatalogSubtypeResolver.ResolveCode(mediaType, subtype));

    public static ContentUnitKind ParseUnitKind(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "episode" => ContentUnitKind.Episode,
            "chapter" => ContentUnitKind.Chapter,
            "track" => ContentUnitKind.Track,
            "audio_chapter" => ContentUnitKind.AudioChapter,
            _ => throw new InvalidDataException(
                $"Unknown synchronized unit kind '{value}'.")
        };

    public static string UnitKindToValue(ContentUnitKind value) => value switch
    {
        ContentUnitKind.Episode => "episode",
        ContentUnitKind.Chapter => "chapter",
        ContentUnitKind.Track => "track",
        ContentUnitKind.AudioChapter => "audio_chapter",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static string PositionKindToValue(MediaPositionKind value) =>
        value switch
        {
            MediaPositionKind.Seconds => "seconds",
            MediaPositionKind.Page => "page",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                null)
        };

    public static string ReaderDisplayModeToValue(ReaderDisplayMode value) =>
        value switch
        {
            ReaderDisplayMode.Paged => "paged",
            ReaderDisplayMode.Continuous => "continuous",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                null)
        };

    public static void ValidateDeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumDeviceIdLength ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-')))
        {
            throw new InvalidDataException(
                "The sync device ID is invalid.");
        }
    }

    public static void ValidateDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Trim().Length > MaximumDeviceNameLength ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "The sync device name is invalid.");
        }
    }
}
