using System.Security.Cryptography;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sync;

public sealed record CatalogTransferMergeResult(
    CatalogTransferDocument Document,
    IReadOnlyList<UserSyncCatalogRedirect> Redirects);

public static class CatalogTransferDocumentMerger
{
    /// <summary>
    /// The device stamped on redirects minted by a merge that has no device
    /// of its own. Fixed so the same fold produces the same version wherever
    /// it happens.
    /// </summary>
    private const string MergeDeviceId = "00000000000000000000000000000000";

    public static CatalogTransferDocument Merge(
        CatalogTransferDocument left,
        CatalogTransferDocument right,
        DateTimeOffset? exportedAt = null) =>
        MergeWithRedirects(left, right, exportedAt).Document;

    public static CatalogTransferMergeResult MergeWithRedirects(
        CatalogTransferDocument left,
        CatalogTransferDocument right,
        DateTimeOffset? exportedAt = null)
    {
        // Duplicate names are tolerated on the way in and refused on the way
        // out. Collapsing two spellings of one title onto a single item is
        // what this merge is for — the inputs come from devices that each
        // keyed the title by their own id — so rejecting them here refuses
        // the exact condition the merge resolves. The output check below is
        // the guarantee that it did resolve them.
        CatalogTransferValidator.Validate(
            left,
            allowMissingTorrentIdentity: false,
            allowDuplicateItemNames: true);
        CatalogTransferValidator.Validate(
            right,
            allowMissingTorrentIdentity: false,
            allowDuplicateItemNames: true);

        var groups = new List<CatalogTransferItem>();
        var groupParents = new List<int>();
        var groupsById = new Dictionary<Guid, int>();
        var groupsByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var redirects = new Dictionary<Guid, UserSyncCatalogRedirect>();
        var mergedAt = exportedAt ?? DateTimeOffset.UtcNow;

        int FindGroup(int index)
        {
            while (groupParents[index] != index)
            {
                groupParents[index] = groupParents[groupParents[index]];
                index = groupParents[index];
            }

            return index;
        }

        void FoldIntoGroup(int index, CatalogTransferItem item)
        {
            var current = groups[index];
            var combined = FoldPair(current, item);
            var keptId = current.Id ?? item.Id;
            if (current.Id is { } currentId &&
                item.Id is { } itemId && currentId != itemId)
            {
                var winner = currentId.CompareTo(itemId) <= 0 ? currentId : itemId;
                var loser = winner == currentId ? itemId : currentId;
                keptId = winner;
                redirects.TryAdd(loser, new UserSyncCatalogRedirect
                {
                    FromId = loser,
                    ToId = winner,
                    SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                        MergeDeviceId, mergedAt, $"redirect:{loser:N}")
                });
                groupsById[loser] = index;
            }

            groups[index] = combined.Id == keptId ? combined : combined with { Id = keptId };
            if (keptId is { } id)
            {
                groupsById[id] = index;
            }
        }

        foreach (var item in CatalogStableId.NormalizeLegacyIdentities(left.Items.Concat(right.Items)))
        {
            var mediaType = CatalogSubtypeResolver.ParseMediaType(
                item.MediaType);
            var nameKey = CatalogTransferIdentity.GetKey(item);

            // Identity binds harder than spelling: the same ID under two
            // names is one renamed title, while the same name under two IDs
            // is two entries that turned out to be one title.
            int index;
            if (item.Id is { } id && groupsById.TryGetValue(id, out var byId) &&
                CatalogTransferIdentity.AreCompatible(groups[FindGroup(byId)], item))
            {
                index = FindGroup(byId);
                if (groupsByName.TryGetValue(nameKey, out var namedGroup))
                {
                    namedGroup = FindGroup(namedGroup);
                    if (namedGroup != index)
                    {
                        // A rename can connect two groups already encountered.
                        // Fold both, and keep their old lookup keys routable.
                        FoldIntoGroup(index, groups[namedGroup]);
                        groupParents[namedGroup] = index;
                    }
                }
            }
            else if (groupsByName.TryGetValue(nameKey, out var byName))
            {
                index = FindGroup(byName);
            }
            else
            {
                index = -1;
            }

            if (index < 0)
            {
                groups.Add(item);
                index = groups.Count - 1;
                groupParents.Add(index);
            }
            else
            {
                FoldIntoGroup(index, item);
            }

            var stored = groups[index];
            if (stored.Id is { } storedId)
            {
                groupsById[storedId] = index;
            }

            // Both spellings stay routable: a third document naming either
            // one still folds into this group.
            groupsByName[nameKey] = index;
            groupsByName.TryAdd(
                CatalogTransferIdentity.GetKey(stored),
                index);
        }

        var result = new CatalogTransferDocument
        {
            ExportedAt = mergedAt,
            SourceLabel = "Device sync merge",
            Items = groups
                .Where((_, index) => FindGroup(index) == index)
                .OrderBy(
                    CatalogTransferIdentity.GetKey,
                    StringComparer.Ordinal)
                .ToArray()
        };
        CatalogTransferValidator.Validate(result);
        return new CatalogTransferMergeResult(
            result,
            redirects.Values
                .OrderBy(static redirect => redirect.FromId)
                .ToArray());
    }

    /// <summary>
    /// Folds two views of one title into one.
    /// </summary>
    /// <remarks>
    /// With versions on both sides the newest edit wins the item's own fields
    /// — including a deletion, which survives whole, and loses whole to a
    /// newer edit. Collections still union either way: a chapter recorded on
    /// the older side is not an "older value" of anything, and dropping it is
    /// how positions end up pointing outside the catalog. A versionless side
    /// loses to a versioned one; two versionless sides fall back to the
    /// content-hash order, which at least every device computes identically.
    /// </remarks>
    private static CatalogTransferItem FoldPair(
        CatalogTransferItem current,
        CatalogTransferItem item)
    {
        // Most full snapshots repeat the exact stored title. Keep its object
        // identity so the local restore can also skip writing its entire graph.
        // Versions alone cannot prove equality: chapter updates may share the
        // same item version, so compare the full payload before skipping it.
        if (ReferenceEquals(current, item) ||
            current.Id == item.Id && current.SyncVersion == item.SyncVersion &&
            JsonSerializer.SerializeToUtf8Bytes(current, CatalogTransferJsonContext.Default.CatalogTransferItem)
                .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(item, CatalogTransferJsonContext.Default.CatalogTransferItem)))
        {
            return current;
        }
        var (newer, older) = OrderByVersion(current, item);
        if (newer is null || older is null)
        {
            return CompareCanonical(item, current) > 0
                ? CatalogTransferIdentity.CombineConverged(current, item)
                : CatalogTransferIdentity.CombineConverged(item, current);
        }

        if (newer.Deleted)
        {
            return newer;
        }

        if (older.Deleted)
        {
            return newer;
        }

        var unioned = CatalogTransferIdentity.CombineConverged(newer, older);
        return newer with
        {
            Units = unioned.Units,
            Seasons = unioned.Seasons,
            AlternativeNames = unioned.AlternativeNames,
            Genres = unioned.Genres,
            Categories = unioned.Categories,
            Contributors = unioned.Contributors,
            DistributionRights = unioned.DistributionRights,
            Sources = unioned.Sources
        };
    }

    private static (CatalogTransferItem? Newer, CatalogTransferItem? Older)
        OrderByVersion(CatalogTransferItem left, CatalogTransferItem right)
    {
        if (left.SyncVersion is null && right.SyncVersion is null)
        {
            return (null, null);
        }

        if (left.SyncVersion is null)
        {
            return (right, left);
        }

        if (right.SyncVersion is null)
        {
            return (left, right);
        }

        return UserSyncDocumentMerger.CompareVersions(
                left.SyncVersion,
                right.SyncVersion) >= 0
            ? (left, right)
            : (right, left);
    }

    private static int CompareCanonical(
        CatalogTransferItem left,
        CatalogTransferItem right)
    {
        var leftHash = Hash(left);
        var rightHash = Hash(right);
        return leftHash.AsSpan().SequenceCompareTo(rightHash);
    }

    private static byte[] Hash(CatalogTransferItem item)
    {
        // The identity is deliberately outside the fingerprint: two views of
        // one title must hash by what they say about it, not by which ID they
        // happened to be born under.
        var document = new CatalogTransferDocument
        {
            ExportedAt = DateTimeOffset.UnixEpoch,
            Items =
            [
                item with { Id = null, SyncVersion = null, Deleted = false }
            ]
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            CatalogTransferJsonContext.Default.CatalogTransferDocument);
        return SHA256.HashData(bytes);
    }
}
