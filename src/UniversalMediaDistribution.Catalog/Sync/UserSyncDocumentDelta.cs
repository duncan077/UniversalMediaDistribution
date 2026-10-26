using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sync;

/// <summary>
/// The record-level view of a sync document: what changed between two
/// documents, and the smallest partial document that carries a chosen set of
/// records without failing validation.
/// </summary>
/// <remarks>
/// A routine sync moves a handful of positions and the day's new chapters,
/// yet the snapshot model ships the whole library both ways to say so. Keyed
/// records make the difference computable: the server stamps each record with
/// the revision that last changed it and answers "since revision N" with just
/// those records; a device diffs its export against the last state it proved
/// the server has and uploads just that. The full snapshot remains the
/// bootstrap and the repair path — this is an optimization, never the only
/// copy of anything.
/// </remarks>
public static class UserSyncDocumentDelta
{
    /// <summary>
    /// Keys of every record in <paramref name="current"/> that is absent from
    /// or different in <paramref name="previous"/>. A null previous marks
    /// everything changed.
    /// </summary>
    public static IReadOnlySet<string> ComputeChangedKeys(
        UserSyncDocument? previous,
        UserSyncDocument current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var previousRecords = previous is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : IndexRecords(previous);
        foreach (var (key, record) in EnumerateRecords(current))
        {
            if (!previousRecords.TryGetValue(key, out var before) ||
                !RecordEquals(key, before, record))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    /// <summary>
    /// A partial document carrying exactly the chosen records — plus the
    /// catalog items those records point at, because a record naming a title
    /// the document does not carry would fail validation on arrival.
    /// </summary>
    public static UserSyncDocument Extract(
        UserSyncDocument document,
        IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(keys);

        var itemsByKey = new Dictionary<string, CatalogTransferItem>(
            StringComparer.Ordinal);
        var itemsById = new Dictionary<Guid, CatalogTransferItem>();
        foreach (var item in document.Catalog.Items)
        {
            itemsByKey.TryAdd(NameKey(item), item);
            if (item.Id is { } id)
            {
                itemsById.TryAdd(id, item);
            }
        }

        var included = new Dictionary<string, CatalogTransferItem>(
            StringComparer.Ordinal);

        void IncludeItem(CatalogTransferItem item) =>
            included.TryAdd(NameKey(item), item);

        void IncludeReferenced(UserSyncCatalogItemIdentity identity)
        {
            if (identity.ItemId is { } id &&
                itemsById.TryGetValue(id, out var byId))
            {
                IncludeItem(byId);
                return;
            }

            if (itemsByKey.TryGetValue(
                    UserSyncDocumentValidator.ValidateItemIdentity(identity),
                    out var byName))
            {
                IncludeItem(byName);
            }
            else if (identity.Subtype is null)
            {
                var legacyKey = UserSyncDocumentValidator.ValidateItemIdentity(identity);
                var matches = document.Catalog.Items.Where(item =>
                    UserSyncDocumentValidator.CreateItemKey(
                        CatalogSubtypeResolver.ParseMediaType(item.MediaType), item.Name) == legacyKey)
                    .Take(2).ToArray();
                if (matches.Length == 1) IncludeItem(matches[0]);
            }
        }

        foreach (var item in document.Catalog.Items)
        {
            if (keys.Contains("item:" + ItemKey(item)))
            {
                IncludeItem(item);
            }
        }

        var progress = document.Progress
            .Where(entry => keys.Contains(
                "progress:" +
                UserSyncDocumentValidator.ValidateContentIdentity(
                    entry.Content)))
            .ToArray();
        foreach (var entry in progress)
        {
            IncludeReferenced(entry.Content.Item);
        }

        var readerPreferences = document.ReaderPreferences
            .Where(entry => keys.Contains(
                "reader:" +
                UserSyncDocumentValidator.ValidateItemIdentity(entry.Item)))
            .ToArray();
        foreach (var entry in readerPreferences)
        {
            IncludeReferenced(entry.Item);
        }

        var favoriteStates = document.FavoriteStates
            .Where(state => keys.Contains(
                "favorite:" + state.ContributorKey.Trim()))
            .ToArray();

        var favoriteItems = document.FavoriteItemStates
            .Where(state => keys.Contains(
                "favitem:" +
                UserSyncDocumentValidator.ValidateItemIdentity(state.Item)))
            .ToArray();
        foreach (var state in favoriteItems)
        {
            IncludeReferenced(state.Item);
        }

        var lists = new List<UserSyncMediaList>();
        foreach (var list in document.Lists)
        {
            var listKey = "list:" + list.Id.ToString("N");
            var states = list.ItemStates
                .Where(state => keys.Contains(
                    "listitem:" + list.Id.ToString("N") + "|" +
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        state.Item)))
                .ToArray();
            if (!keys.Contains(listKey) && states.Length == 0)
            {
                continue;
            }

            foreach (var state in states)
            {
                IncludeReferenced(state.Item);
            }

            lists.Add(list with { Items = [], ItemStates = states });
        }

        var redirects = document.CatalogRedirects
            .Where(redirect => keys.Contains(
                "redirect:" + redirect.FromId.ToString("N")))
            .ToArray();

        return document with
        {
            ExportedAt = document.ExportedAt,
            Catalog = new CatalogTransferDocument
            {
                ExportedAt = document.Catalog.ExportedAt,
                SourceLabel = document.Catalog.SourceLabel,
                Items = included.Values
                    .OrderBy(NameKey, StringComparer.Ordinal)
                    .ToArray()
            },
            Progress = progress,
            ReaderPreferences = readerPreferences,
            FavoriteStates = favoriteStates,
            FavoriteContributorKeys = [],
            FavoriteItemStates = favoriteItems,
            Lists = lists,
            CatalogRedirects = redirects
        };
    }

    /// <summary>
    /// Whether the partial carries anything at all.
    /// </summary>
    public static bool IsEmpty(UserSyncDocument document) =>
        document.Catalog.Items.Count == 0 &&
        document.Progress.Count == 0 &&
        document.ReaderPreferences.Count == 0 &&
        document.FavoriteStates.Count == 0 &&
        document.FavoriteItemStates.Count == 0 &&
        document.Lists.Count == 0 &&
        document.CatalogRedirects.Count == 0;

    /// <summary>
    /// Every record key the document carries, item keys included.
    /// </summary>
    public static IEnumerable<string> EnumerateKeys(
        UserSyncDocument document) =>
        EnumerateRecords(document).Select(static record => record.Key);

    private static Dictionary<string, object> IndexRecords(
        UserSyncDocument document)
    {
        var records = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, record) in EnumerateRecords(document))
        {
            records.TryAdd(key, record);
        }

        return records;
    }

    private static IEnumerable<(string Key, object Record)> EnumerateRecords(
        UserSyncDocument document)
    {
        foreach (var item in document.Catalog.Items)
        {
            yield return ("item:" + ItemKey(item), item);
        }

        foreach (var redirect in document.CatalogRedirects)
        {
            yield return (
                "redirect:" + redirect.FromId.ToString("N"),
                redirect);
        }

        foreach (var entry in document.Progress)
        {
            yield return (
                "progress:" +
                UserSyncDocumentValidator.ValidateContentIdentity(
                    entry.Content),
                entry);
        }

        foreach (var entry in document.ReaderPreferences)
        {
            yield return (
                "reader:" +
                UserSyncDocumentValidator.ValidateItemIdentity(entry.Item),
                entry);
        }

        foreach (var state in document.FavoriteStates)
        {
            yield return ("favorite:" + state.ContributorKey.Trim(), state);
        }

        foreach (var state in document.FavoriteItemStates)
        {
            yield return (
                "favitem:" +
                UserSyncDocumentValidator.ValidateItemIdentity(state.Item),
                state);
        }

        foreach (var list in document.Lists)
        {
            yield return ("list:" + list.Id.ToString("N"), ListMeta(list));
            foreach (var state in list.ItemStates)
            {
                yield return (
                    "listitem:" + list.Id.ToString("N") + "|" +
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        state.Item),
                    state);
            }
        }
    }

    /// <summary>
    /// An item is identified by its sync ID when it has one — so a rename is
    /// a change to one record rather than a disappearance and an arrival —
    /// and by its name key when it predates identities.
    /// </summary>
    private static string ItemKey(CatalogTransferItem item) =>
        item.Id is { } id ? "i:" + id.ToString("N") : "n:" + NameKey(item);

    private static string NameKey(CatalogTransferItem item) =>
        UserSyncDocumentValidator.CreateItemKey(
            CatalogSubtypeResolver.ParseMediaType(item.MediaType),
            item.Name, item.Subtype);

    private static bool RecordEquals(string key, object before, object after)
    {
        if (ReferenceEquals(before, after))
        {
            return true;
        }

        if (key.StartsWith("item:", StringComparison.Ordinal))
        {
            // Items carry nested collections, which record equality compares
            // by reference; the serialized form compares by content.
            return ItemJson((CatalogTransferItem)before) ==
                ItemJson((CatalogTransferItem)after);
        }

        // Every other record is scalars and nested records, where value
        // equality is exactly content equality.
        return Equals(before, after);
    }

    private static string ItemJson(CatalogTransferItem item) =>
        JsonSerializer.Serialize(
            item,
            CatalogTransferJsonContext.Default.CatalogTransferItem);

    private static (
        Guid Id,
        string Name,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        bool IsDeleted,
        UserSyncVersion? SyncVersion) ListMeta(UserSyncMediaList list) =>
        (
            list.Id,
            list.Name,
            list.CreatedAt,
            list.UpdatedAt,
            list.IsDeleted,
            list.SyncVersion);
}
