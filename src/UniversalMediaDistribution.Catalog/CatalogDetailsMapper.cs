using System.Globalization;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog;

public static class CatalogDetailsMapper
{
    /// <summary>
    /// The chapter or episode number a unit sorts by, or
    /// <see cref="decimal.MaxValue"/> when it has none.
    ///
    /// SortOrder cannot be trusted on its own: a provider's daily update
    /// carries only the chapters it just published and numbers them from zero
    /// within that batch, so chapter 67 arrives claiming position 0 and lands
    /// ahead of chapter 65. The number written on the chapter is the one thing
    /// that means the same in every batch, so it leads and SortOrder breaks
    /// ties — which keeps specials and anything else unnumbered in the order
    /// its provider gave, at the end.
    /// </summary>
    public static decimal UnitNumber(string? displayNumber) =>
        decimal.TryParse(
            displayNumber,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : decimal.MaxValue;

    public static CatalogItemDetails ToDetails(
        CatalogTransferItem transferItem,
        Guid catalogItemId,
        string? stableItemKey = null)
    {
        ArgumentNullException.ThrowIfNull(transferItem);
        var item = CatalogTransferMapper.ToCatalogItem(transferItem, catalogItemId);
        var itemKey = string.IsNullOrWhiteSpace(stableItemKey)
            ? $"item:{item.MediaType.ToString().ToLowerInvariant()}:{item.NameKey}"
            : stableItemKey.Trim();
        var itemIdentity = new CatalogContentIdentity(catalogItemId, itemKey);

        var directUnits = transferItem.Units
            .OrderBy(static unit => UnitNumber(unit.DisplayNumber))
            .ThenBy(static unit => unit.SortOrder)
            .Select(unit => MapUnit(catalogItemId, itemKey, seasonNumber: null, unit))
            .ToArray();
        var seasons = transferItem.Seasons
            .OrderBy(static season => season.SortOrder)
            .ThenBy(static season => season.Number)
            .Select(season => new CatalogSeasonDetails(
                season.Number,
                season.SortOrder,
                season.DisplayName,
                season.ThumbnailRef,
                season.ReleaseDate,
                season.Units
                    .OrderBy(static unit => UnitNumber(unit.DisplayNumber))
                    .ThenBy(static unit => unit.SortOrder)
                    .Select(unit => MapUnit(catalogItemId, itemKey, season.Number, unit))
                    .ToArray()))
            .ToArray();

        return new CatalogItemDetails(
            item,
            itemIdentity,
            transferItem.OriginalName,
            transferItem.ReleaseDate,
            transferItem.OriginalLanguage,
            transferItem.CountryCode,
            transferItem.ContentRating,
            transferItem.RuntimeSeconds,
            transferItem.PageCount,
            seasons,
            directUnits,
            transferItem.Sources
                .OrderBy(static source => source.SortOrder)
                .Select(MapSource)
                .ToArray(),
            transferItem.DistributionRights.Select(MapRight).ToArray());
    }

    private static CatalogContentUnitDetails MapUnit(
        Guid catalogItemId,
        string itemKey,
        int? seasonNumber,
        CatalogTransferUnit unit)
    {
        var kind = CatalogTransferMapper.ParseUnitKind(unit.Kind);
        var unitKey = $"{itemKey}/season:{seasonNumber?.ToString() ?? "none"}" +
            $"/unit:{kind.ToString().ToLowerInvariant()}:{NameNormalizer.Normalize(unit.DisplayNumber)}";
        var identity = new CatalogContentIdentity(
            catalogItemId,
            unitKey,
            kind,
            unit.DisplayNumber,
            seasonNumber);

        return new CatalogContentUnitDetails(
            identity,
            kind,
            unit.DisplayNumber,
            unit.SortOrder,
            unit.Name,
            unit.ThumbnailRef,
            unit.DurationSeconds,
            unit.PageCount,
            unit.ReleaseDate,
            unit.IsSpecial,
            unit.Sources
                .OrderBy(static source => source.SortOrder)
                .Select(MapSource)
                .ToArray());
    }

    private static CatalogMediaSourceDetails MapSource(CatalogTransferMediaSource source) => new(
        source.AssetRole,
        source.SourceLayout switch
        {
            "single_file" => CatalogMediaSourceLayout.SingleFile,
            "image_sequence" => CatalogMediaSourceLayout.ImageSequence,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.SourceLayout, "Unknown source layout.")
        },
        source.FileIndex,
        source.FilePath,
        source.MimeType,
        source.Container,
        source.VideoQuality,
        source.Language,
        source.SizeBytes,
        source.SortOrder,
        MapTorrent(source.Torrent));

    private static CatalogTorrentDetails MapTorrent(CatalogTransferTorrent torrent) => new(
        torrent.DisplayName,
        torrent.MagnetUri,
        string.IsNullOrWhiteSpace(torrent.TorrentMetadataBase64)
            ? ReadOnlyMemory<byte>.Empty
            : Convert.FromBase64String(torrent.TorrentMetadataBase64),
        torrent.InfoHashV1,
        torrent.InfoHashV2,
        torrent.IsPrivate,
        torrent.TotalSize,
        torrent.SourceUri,
        torrent.Trackers
            .OrderBy(static tracker => tracker.Tier)
            .ThenBy(static tracker => tracker.SortOrder)
            .Select(static tracker => new CatalogTrackerDetails(
                tracker.Name,
                new Uri(tracker.Endpoint, UriKind.Absolute),
                tracker.Transport,
                tracker.ClientFamily,
                tracker.RequiresAuthentication,
                tracker.Tier,
                tracker.SortOrder,
                tracker.CredentialKey))
            .ToArray(),
        LanguageTag.Normalize(torrent.Language));

    private static CatalogDistributionRightDetails MapRight(CatalogTransferRight right) => new(
        right.SourceName,
        new Uri(right.SourceUri, UriKind.Absolute),
        right.LicenseName,
        string.IsNullOrWhiteSpace(right.LicenseUri) ? null : new Uri(right.LicenseUri, UriKind.Absolute),
        right.Attribution,
        right.RightsNote,
        right.VerifiedAt);
}
