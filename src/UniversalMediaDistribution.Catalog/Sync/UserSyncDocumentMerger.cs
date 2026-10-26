using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sync;

public static class UserSyncDocumentMerger
{
    public static UserSyncDocument Normalize(UserSyncDocument document)
    {
        // Validate payloads before reconciling identities. References may be
        // duplicated by a rename, or absent from a partial/stale catalog.
        // Keep unresolved references until both devices' catalogs are merged.
        UserSyncDocumentValidator.Validate(
            document,
            allowUnresolvedCatalogReferences: true,
            allowDuplicateCatalogReferences: true);
        var normalized = document.Version == 1
            ? UpgradeVersionOne(document) with { Version = 2 }
            : Canonicalize(document);

        // Stamped against its own catalog, so every reference leaves here
        // carrying the identity of the title it names — which is what lets it
        // survive the renames and folds later merges perform.
        normalized = RemapOntoCatalog(
            normalized,
            normalized.Catalog,
            normalized.CatalogRedirects);
        UserSyncDocumentValidator.Validate(
            normalized,
            allowUnresolvedCatalogReferences: true);
        return normalized;
    }

    public static UserSyncDocument Merge(
        UserSyncDocument local,
        UserSyncDocument remote,
        CatalogTransferDocument mergedCatalog,
        string deviceId,
        string deviceName,
        DateTimeOffset? exportedAt = null,
        IReadOnlyList<UserSyncCatalogRedirect>? additionalRedirects = null)
    {
        ArgumentNullException.ThrowIfNull(mergedCatalog);
        UserSyncDocumentValidator.ValidateDeviceId(deviceId);
        UserSyncDocumentValidator.ValidateDeviceName(deviceName);
        var leftNormalized = Normalize(local);
        var rightNormalized = Normalize(remote);

        // The forwarding notes must exist before anything is remapped: a
        // record held under an identity the catalog merge just folded away
        // can only find its title by following them.
        var redirects = MergeRedirects(
            leftNormalized.CatalogRedirects,
            rightNormalized.CatalogRedirects,
            additionalRedirects);
        var left = RemapOntoCatalog(leftNormalized, mergedCatalog, redirects);
        var right = RemapOntoCatalog(
            rightNormalized,
            mergedCatalog,
            redirects);

        var progress = MergeByKey(
            left.Progress,
            right.Progress,
            static entry =>
                UserSyncDocumentValidator.ValidateContentIdentity(
                    entry.Content),
            static entry => entry.SyncVersion!,
            CompareProgressPayload);
        var preferences = MergeByKey(
            left.ReaderPreferences,
            right.ReaderPreferences,
            static entry =>
                UserSyncDocumentValidator.ValidateItemIdentity(entry.Item),
            static entry => entry.SyncVersion!,
            CompareReaderPayload);
        var favorites = MergeByKey(
            left.FavoriteStates,
            right.FavoriteStates,
            static state => state.ContributorKey.Trim(),
            static state => state.SyncVersion,
            CompareFavoritePayload);
        var favoriteItems = MergeByKey(
            left.FavoriteItemStates,
            right.FavoriteItemStates,
            static state =>
                UserSyncDocumentValidator.ValidateItemIdentity(
                    state.Item),
            static state => state.SyncVersion,
            CompareFavoriteItemPayload);
        var lists = MergeLists(left.Lists, right.Lists);

        var merged = Canonicalize(new UserSyncDocument
        {
            Version = UserSyncDocument.CurrentVersion,
            ExportedAt = exportedAt ?? DateTimeOffset.UtcNow,
            DeviceId = deviceId.Trim(),
            DeviceName = deviceName.Trim(),
            Catalog = mergedCatalog,
            Progress = progress,
            ReaderPreferences = preferences,
            FavoriteStates = favorites,
            FavoriteItemStates = favoriteItems,
            Lists = lists,
            CatalogRedirects = redirects
        });
        UserSyncDocumentValidator.Validate(
            merged,
            allowUnresolvedCatalogReferences: true);
        return merged;
    }

    public static int CompareVersions(
        UserSyncVersion left,
        UserSyncVersion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var clock = left.Clock.CompareTo(right.Clock);
        if (clock != 0)
        {
            return clock;
        }

        var device = string.CompareOrdinal(left.DeviceId, right.DeviceId);
        return device != 0
            ? device
            : left.OperationId.CompareTo(right.OperationId);
    }

    public static UserSyncVersion CreateLegacyVersion(
        string deviceId,
        DateTimeOffset modifiedAt,
        string discriminator)
    {
        UserSyncDocumentValidator.ValidateDeviceId(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        if (modifiedAt == default)
        {
            throw new ArgumentException(
                "A legacy sync version requires a timestamp.",
                nameof(modifiedAt));
        }

        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                string.Join(
                    '\n',
                    deviceId.Trim(),
                    modifiedAt.ToString("O", CultureInfo.InvariantCulture),
                    discriminator)));
        Span<byte> operationBytes = stackalloc byte[16];
        bytes.AsSpan(0, operationBytes.Length).CopyTo(operationBytes);
        return new UserSyncVersion
        {
            Clock = Math.Max(1, modifiedAt.ToUnixTimeMilliseconds()),
            DeviceId = deviceId.Trim(),
            OperationId = new Guid(operationBytes),
            ModifiedAt = modifiedAt
        };
    }

    private static UserSyncDocument UpgradeVersionOne(
        UserSyncDocument document)
    {
        var progress = document.Progress
            .Select(entry => entry with
            {
                SyncVersion = CreateLegacyVersion(
                    document.DeviceId,
                    entry.UpdatedAt,
                    "progress:" +
                    UserSyncDocumentValidator.ValidateContentIdentity(
                        entry.Content))
            })
            .ToArray();
        var preferences = document.ReaderPreferences
            .Select(entry => entry with
            {
                SyncVersion = CreateLegacyVersion(
                    document.DeviceId,
                    entry.UpdatedAt,
                    "reader:" +
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        entry.Item))
            })
            .ToArray();
        var favorites = document.FavoriteContributorKeys
            .Select(key => new UserSyncFavoriteState
            {
                ContributorKey = key.Trim(),
                IsFavorite = true,
                SyncVersion = CreateLegacyVersion(
                    document.DeviceId,
                    document.ExportedAt,
                    "favorite:" + key.Trim())
            })
            .ToArray();
        var lists = document.Lists
            .Select(list =>
            {
                var version = CreateLegacyVersion(
                    document.DeviceId,
                    list.UpdatedAt,
                    "list:" + list.Id.ToString("N"));
                return list with
                {
                    IsDeleted = false,
                    SyncVersion = version,
                    ItemStates = list.Items
                        .Select(item => new UserSyncListItemState
                        {
                            Item = item,
                            IsMember = true,
                            SyncVersion = CreateLegacyVersion(
                                document.DeviceId,
                                list.UpdatedAt,
                                "list-item:" +
                                list.Id.ToString("N") +
                                ":" +
                                UserSyncDocumentValidator
                                    .ValidateItemIdentity(item))
                        })
                        .ToArray()
                };
            })
            .ToArray();

        return Canonicalize(document with
        {
            Version = UserSyncDocument.CurrentVersion,
            Progress = progress,
            ReaderPreferences = preferences,
            FavoriteStates = favorites,
            Lists = lists
        });
    }

    private static UserSyncDocument Canonicalize(
        UserSyncDocument document)
    {
        var favorites = document.FavoriteStates
            .OrderBy(
                static state => state.ContributorKey,
                StringComparer.Ordinal)
            .ToArray();
        var favoriteItems = document.FavoriteItemStates
            .OrderBy(
                static state =>
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        state.Item),
                StringComparer.Ordinal)
            .ToArray();
        var lists = CanonicalizeListNames(
            document.Lists
                .Select(CanonicalizeList)
                .ToArray());
        return document with
        {
            Version = document.Version,
            CatalogRedirects = document.CatalogRedirects
                .OrderBy(static redirect => redirect.FromId)
                .ToArray(),
            FavoriteStates = favorites,
            FavoriteItemStates = favoriteItems,
            FavoriteContributorKeys = favorites
                .Where(static state => state.IsFavorite)
                .Select(static state => state.ContributorKey.Trim())
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Progress = document.Progress
                .OrderBy(
                    static entry =>
                        UserSyncDocumentValidator.ValidateContentIdentity(
                            entry.Content),
                    StringComparer.Ordinal)
                .ToArray(),
            ReaderPreferences = document.ReaderPreferences
                .OrderBy(
                    static entry =>
                        UserSyncDocumentValidator.ValidateItemIdentity(
                            entry.Item),
                    StringComparer.Ordinal)
                .ToArray(),
            Lists = lists
                .OrderBy(static list => list.IsDeleted)
                .ThenBy(static list => list.CreatedAt)
                .ThenBy(static list => list.Id)
                .ToArray()
        };
    }

    private static UserSyncMediaList CanonicalizeList(
        UserSyncMediaList list)
    {
        var states = list.ItemStates
            .OrderBy(
                static state =>
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        state.Item),
                StringComparer.Ordinal)
            .ToArray();
        var updatedAt = list.IsDeleted
            ? list.UpdatedAt
            : states
                .Select(static state => state.SyncVersion.ModifiedAt)
                .Append(list.SyncVersion!.ModifiedAt)
                .Append(list.UpdatedAt)
                .Max();
        return list with
        {
            Name = list.IsDeleted
                ? list.Name?.Trim() ?? string.Empty
                : UserMediaList.NormalizeName(list.Name),
            UpdatedAt = updatedAt,
            ItemStates = states,
            Items = list.IsDeleted
                ? []
                : states
                    .Where(static state => state.IsMember)
                    .Select(static state => state.Item)
                    .ToArray()
        };
    }

    private static IReadOnlyList<UserSyncMediaList> CanonicalizeListNames(
        IReadOnlyList<UserSyncMediaList> lists)
    {
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<Guid, UserSyncMediaList>();
        foreach (var list in lists
                     .Where(static list => !list.IsDeleted)
                     .OrderByDescending(
                         static list => list.SyncVersion,
                         UserSyncVersionComparer.Instance)
                     .ThenBy(static list => list.Id))
        {
            var name = UserMediaList.NormalizeName(list.Name);
            var nameKey = UserMediaList.GetNameKey(name);
            if (!usedNames.Add(nameKey))
            {
                name = CreateConflictListName(name, list.Id, usedNames);
            }

            result[list.Id] = list with { Name = name };
        }

        foreach (var list in lists.Where(static list => list.IsDeleted))
        {
            result[list.Id] = list;
        }

        return result.Values.ToArray();
    }

    private static string CreateConflictListName(
        string original,
        Guid listId,
        ISet<string> usedNames)
    {
        var id = listId.ToString("N");
        for (var suffixLength = 6;
             suffixLength <= 32;
             suffixLength += 2)
        {
            var candidateSuffix = $" ({id[..suffixLength]})";
            var maximumBaseLength =
                UserMediaList.MaximumNameLength - candidateSuffix.Length;
            var candidate =
                original[..Math.Min(original.Length, maximumBaseLength)]
                    .TrimEnd() +
                candidateSuffix;
            if (usedNames.Add(UserMediaList.GetNameKey(candidate)))
            {
                return candidate;
            }
        }

        throw new InvalidDataException(
            "Synchronized list names could not be made unique.");
    }

    /// <summary>
    /// Points every recorded position, preference, favourite and list entry at
    /// the title the merged catalog actually carries.
    /// </summary>
    /// <remarks>
    /// Merging collapses two spellings of one title onto a single item and
    /// keeps the spelling it dropped as an alternative name. Anything still
    /// naming that spelling would then read as pointing outside the catalog,
    /// and the account would lose a reading position to a rename it never
    /// made — so the identity is rewritten to follow the title rather than
    /// the name it was recorded under.
    /// </remarks>
    private static UserSyncDocument RemapOntoCatalog(
        UserSyncDocument document,
        CatalogTransferDocument catalog,
        IReadOnlyList<UserSyncCatalogRedirect> redirects)
    {
        var index = CatalogReferenceIndex.Create(catalog, redirects);

        // Two entries can land on one identity here, and the collections are
        // keyed by it. They are collapsed the same way the merge collapses
        // the two sides - newest version wins - rather than by dropping
        // whichever came second.
        return document with
        {
            Progress = MergeByKey(
                [],
                document.Progress
                    .Select(entry => entry with
                    {
                        Content = entry.Content with
                        {
                            Item = index.Resolve(entry.Content.Item)
                        }
                    })
                    .ToArray(),
                static entry =>
                    UserSyncDocumentValidator.ValidateContentIdentity(
                        entry.Content),
                static entry => entry.SyncVersion!,
                CompareProgressPayload),
            ReaderPreferences = MergeByKey(
                [],
                document.ReaderPreferences
                    .Select(entry => entry with
                    {
                        Item = index.Resolve(entry.Item)
                    })
                    .ToArray(),
                static entry =>
                    UserSyncDocumentValidator.ValidateItemIdentity(entry.Item),
                static entry => entry.SyncVersion!,
                CompareReaderPayload),
            FavoriteItemStates = MergeByKey(
                [],
                document.FavoriteItemStates
                    .Select(state => state with
                    {
                        Item = index.Resolve(state.Item)
                    })
                    .ToArray(),
                static state =>
                    UserSyncDocumentValidator.ValidateItemIdentity(state.Item),
                static state => state.SyncVersion,
                CompareFavoriteItemPayload),
            Lists = document.Lists
                .Select(list => list with
                {
                    Items = list.Items
                        .Select(index.Resolve)
                        .DistinctBy(
                            UserSyncDocumentValidator.ValidateItemIdentity,
                            StringComparer.Ordinal)
                        .ToArray(),
                    ItemStates = MergeByKey(
                        [],
                        list.ItemStates
                            .Select(state => state with
                            {
                                Item = index.Resolve(state.Item)
                            })
                            .ToArray(),
                        static state =>
                            UserSyncDocumentValidator.ValidateItemIdentity(
                                state.Item),
                        static state => state.SyncVersion,
                        CompareListItemPayload)
                })
                .ToArray()
        };
    }

    /// <summary>
    /// Answers where a reference points in the merged catalog: by sync
    /// identity first, through any redirect chain, then by primary name, and
    /// finally by the names the catalog only carries as alternatives. What
    /// comes back is the canonical identity - ID and current spelling - so
    /// every record for one title carries the same reference after a remap.
    /// </summary>
    private sealed class CatalogReferenceIndex
    {
        private readonly Dictionary<Guid, UserSyncCatalogItemIdentity> _byId =
            [];
        private readonly Dictionary<string, UserSyncCatalogItemIdentity>
            _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, UserSyncCatalogItemIdentity>
            _byAlias = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Guid> _redirects = [];
        private readonly Dictionary<string, UserSyncCatalogItemIdentity?> _legacyNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, UserSyncCatalogItemIdentity?> _legacyAliases = new(StringComparer.Ordinal);

        public static CatalogReferenceIndex Create(
            CatalogTransferDocument catalog,
            IReadOnlyList<UserSyncCatalogRedirect> redirects)
        {
            var index = new CatalogReferenceIndex();
            foreach (var item in catalog.Items)
            {
                var identity = new UserSyncCatalogItemIdentity
                {
                    MediaType = item.MediaType,
                    Subtype = item.Subtype,
                    ItemId = item.Id,
                    Name = item.Name
                };
                index._byName.TryAdd(
                    UserSyncDocumentValidator.ValidateItemIdentity(identity),
                    identity);
                if (item.Id is { } syncId)
                {
                    index._byId.TryAdd(syncId, identity);
                }
                var legacyKey = UserSyncDocumentValidator.CreateItemKey(
                    CatalogSubtypeResolver.ParseMediaType(item.MediaType), item.Name);
                if (index._legacyNames.TryGetValue(legacyKey, out var previous) &&
                    (previous is null || UserSyncDocumentValidator.ValidateItemIdentity(previous) !=
                        UserSyncDocumentValidator.ValidateItemIdentity(identity)))
                    index._legacyNames[legacyKey] = null;
                else index._legacyNames[legacyKey] = identity;
            }

            foreach (var item in catalog.Items)
            {
                var identity = index._byName[
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        new UserSyncCatalogItemIdentity
                        {
                            MediaType = item.MediaType,
                            Subtype = item.Subtype,
                            Name = item.Name
                        })];
                foreach (var alias in GetAliasNames(item))
                {
                    var key = UserSyncDocumentValidator.ValidateItemIdentity(
                        new UserSyncCatalogItemIdentity
                        {
                            MediaType = item.MediaType,
                            Subtype = item.Subtype,
                            Name = alias
                        });

                    // A name the catalog still lists as a title of its own is
                    // not an alias for anything: only names with no item of
                    // their own redirect, so this can never move a record off
                    // a live title.
                    if (!index._byName.ContainsKey(key))
                    {
                        index._byAlias.TryAdd(key, identity);
                    }
                    var legacyAlias = UserSyncDocumentValidator.CreateItemKey(
                        CatalogSubtypeResolver.ParseMediaType(item.MediaType), alias);
                    if (index._legacyAliases.TryGetValue(legacyAlias, out var previous) &&
                        (previous is null || UserSyncDocumentValidator.ValidateItemIdentity(previous) !=
                            UserSyncDocumentValidator.ValidateItemIdentity(identity)))
                        index._legacyAliases[legacyAlias] = null;
                    else index._legacyAliases[legacyAlias] = identity;
                }
            }

            foreach (var redirect in redirects)
            {
                index._redirects.TryAdd(redirect.FromId, redirect.ToId);
            }

            return index;
        }

        public UserSyncCatalogItemIdentity Resolve(
            UserSyncCatalogItemIdentity identity)
        {
            if (identity.ItemId is { } itemId)
            {
                var hops = 0;
                var current = itemId;
                while (true)
                {
                    if (_byId.TryGetValue(current, out var byId) &&
                        (identity.Subtype is null || SameBookKind(identity, byId)))
                    {
                        return byId;
                    }

                    if (!_redirects.TryGetValue(current, out var next) ||
                        ++hops > _redirects.Count)
                    {
                        break;
                    }

                    current = next;
                }
            }

            var key = UserSyncDocumentValidator.ValidateItemIdentity(identity);
            if (identity.Subtype is null && _legacyNames.TryGetValue(key, out var legacy))
            {
                return legacy ?? identity;
            }
            if (identity.Subtype is null && _legacyAliases.TryGetValue(key, out var legacyAlias))
                return legacyAlias ?? identity;
            if (_byName.TryGetValue(key, out var exact))
            {
                return exact;
            }

            return _byAlias.TryGetValue(key, out var aliased)
                ? aliased
                : identity;
        }

        private static bool SameBookKind(UserSyncCatalogItemIdentity left, UserSyncCatalogItemIdentity right) =>
            !string.Equals(left.MediaType, "book", StringComparison.OrdinalIgnoreCase) ||
            CatalogSubtypeResolver.ResolveCode(UniversalMediaDistribution.Domain.Catalog.MediaType.Book, left.Subtype!) ==
            CatalogSubtypeResolver.ResolveCode(UniversalMediaDistribution.Domain.Catalog.MediaType.Book, right.Subtype!);

        private static IEnumerable<string> GetAliasNames(
            CatalogTransferItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.OriginalName))
            {
                yield return item.OriginalName;
            }

            foreach (var alias in
                     CatalogTransferMapper.GetAlternativeNames(item))
            {
                if (!string.IsNullOrWhiteSpace(alias.Name))
                {
                    yield return alias.Name;
                }
            }
        }
    }

    private static IReadOnlyList<UserSyncCatalogRedirect> MergeRedirects(
        IReadOnlyList<UserSyncCatalogRedirect> left,
        IReadOnlyList<UserSyncCatalogRedirect> right,
        IReadOnlyList<UserSyncCatalogRedirect>? additional)
    {
        var merged = MergeByKey(
            left,
            right,
            static redirect => redirect.FromId.ToString("N"),
            static redirect => redirect.SyncVersion,
            CompareRedirectPayload);
        return additional is not { Count: > 0 }
            ? merged
            : MergeByKey(
                merged,
                additional,
                static redirect => redirect.FromId.ToString("N"),
                static redirect => redirect.SyncVersion,
                CompareRedirectPayload);
    }

    private static int CompareRedirectPayload(
        UserSyncCatalogRedirect left,
        UserSyncCatalogRedirect right) =>
        left.ToId.CompareTo(right.ToId);

    private static IReadOnlyList<T> MergeByKey<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, string> keySelector,
        Func<T, UserSyncVersion> versionSelector,
        Func<T, T, int> payloadComparer)
    {
        var result = left.ToDictionary(
            keySelector,
            static value => value,
            StringComparer.Ordinal);
        foreach (var candidate in right)
        {
            var key = keySelector(candidate);
            if (!result.TryGetValue(key, out var current) ||
                IsCandidateNewer(
                    current,
                    candidate,
                    versionSelector,
                    payloadComparer))
            {
                result[key] = candidate;
            }
        }

        return result
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .ToArray();
    }

    private static bool IsCandidateNewer<T>(
        T current,
        T candidate,
        Func<T, UserSyncVersion> versionSelector,
        Func<T, T, int> payloadComparer)
    {
        var version = CompareVersions(
            versionSelector(candidate),
            versionSelector(current));
        return version > 0 ||
               version == 0 &&
               payloadComparer(candidate, current) > 0;
    }

    private static IReadOnlyList<UserSyncMediaList> MergeLists(
        IReadOnlyList<UserSyncMediaList> left,
        IReadOnlyList<UserSyncMediaList> right)
    {
        var leftById = left.ToDictionary(static list => list.Id);
        var rightById = right.ToDictionary(static list => list.Id);
        var result = new List<UserSyncMediaList>();
        foreach (var id in leftById.Keys
                     .Concat(rightById.Keys)
                     .Distinct()
                     .Order())
        {
            var hasLeft = leftById.TryGetValue(id, out var leftList);
            var hasRight = rightById.TryGetValue(id, out var rightList);
            if (!hasLeft)
            {
                result.Add(rightList!);
                continue;
            }

            if (!hasRight)
            {
                result.Add(leftList!);
                continue;
            }

            var selected = IsCandidateNewer(
                    leftList!,
                    rightList!,
                    static list => list.SyncVersion!,
                    CompareListPayload)
                ? rightList!
                : leftList!;
            result.Add(selected with
            {
                ItemStates = MergeByKey(
                    leftList!.ItemStates,
                    rightList!.ItemStates,
                    static state =>
                        UserSyncDocumentValidator.ValidateItemIdentity(
                            state.Item),
                    static state => state.SyncVersion,
                    CompareListItemPayload)
            });
        }

        return result;
    }

    private static int CompareProgressPayload(
        UserSyncProgressEntry left,
        UserSyncProgressEntry right)
    {
        var completed = left.IsCompleted.CompareTo(right.IsCompleted);
        if (completed != 0)
        {
            return completed;
        }

        var positionKind = string.CompareOrdinal(
            left.PositionKind,
            right.PositionKind);
        return positionKind != 0
            ? positionKind
            : left.Position.CompareTo(right.Position);
    }

    private static int CompareReaderPayload(
        UserSyncReaderPreference left,
        UserSyncReaderPreference right) =>
        string.CompareOrdinal(left.DisplayMode, right.DisplayMode);

    // Removal wins an impossible equal-version conflict.
    private static int CompareFavoritePayload(
        UserSyncFavoriteState left,
        UserSyncFavoriteState right) =>
        right.IsFavorite.CompareTo(left.IsFavorite);

    // Removal wins an impossible equal-version conflict.
    private static int CompareFavoriteItemPayload(
        UserSyncFavoriteItemState left,
        UserSyncFavoriteItemState right) =>
        right.IsFavorite.CompareTo(left.IsFavorite);

    private static int CompareListPayload(
        UserSyncMediaList left,
        UserSyncMediaList right)
    {
        if (left.IsDeleted != right.IsDeleted)
        {
            return right.IsDeleted.CompareTo(left.IsDeleted);
        }

        return string.CompareOrdinal(left.Name, right.Name);
    }

    // Removal wins an impossible equal-version conflict.
    private static int CompareListItemPayload(
        UserSyncListItemState left,
        UserSyncListItemState right) =>
        right.IsMember.CompareTo(left.IsMember);

    private sealed class UserSyncVersionComparer :
        IComparer<UserSyncVersion?>
    {
        public static UserSyncVersionComparer Instance { get; } = new();

        public int Compare(UserSyncVersion? x, UserSyncVersion? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            return y is null ? 1 : CompareVersions(x, y);
        }
    }
}
