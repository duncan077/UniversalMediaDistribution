using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Application.Catalog;

public sealed record CatalogItemDetails(
    CatalogItem Item,
    CatalogContentIdentity Identity,
    string? OriginalName,
    string? ReleaseDate,
    string? OriginalLanguage,
    string? CountryCode,
    string? ContentRating,
    int? RuntimeSeconds,
    int? PageCount,
    IReadOnlyList<CatalogSeasonDetails> Seasons,
    IReadOnlyList<CatalogContentUnitDetails> Units,
    IReadOnlyList<CatalogMediaSourceDetails> Sources,
    IReadOnlyList<CatalogDistributionRightDetails> DistributionRights);

public sealed record CatalogSeasonDetails(
    int Number,
    int SortOrder,
    string? DisplayName,
    string? ThumbnailRef,
    string? ReleaseDate,
    IReadOnlyList<CatalogContentUnitDetails> Units);

public sealed record CatalogContentUnitDetails(
    CatalogContentIdentity Identity,
    ContentUnitKind Kind,
    string DisplayNumber,
    int SortOrder,
    string? Name,
    string? ThumbnailRef,
    int? DurationSeconds,
    int? PageCount,
    string? ReleaseDate,
    bool IsSpecial,
    IReadOnlyList<CatalogMediaSourceDetails> Sources);

public sealed record CatalogContentIdentity
{
    public CatalogContentIdentity(
        Guid catalogItemId,
        string stableKey,
        ContentUnitKind? unitKind = null,
        string? displayNumber = null,
        int? seasonNumber = null)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException("A content identity requires a catalog item identifier.", nameof(catalogItemId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        if ((unitKind is null) != string.IsNullOrWhiteSpace(displayNumber))
        {
            throw new ArgumentException("Unit kind and display number must either both be supplied or both be omitted.");
        }

        if (seasonNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber));
        }

        CatalogItemId = catalogItemId;
        StableKey = stableKey.Trim();
        UnitKind = unitKind;
        DisplayNumber = string.IsNullOrWhiteSpace(displayNumber) ? null : displayNumber.Trim();
        SeasonNumber = seasonNumber;
    }

    public Guid CatalogItemId { get; }

    public string StableKey { get; }

    public ContentUnitKind? UnitKind { get; }

    public string? DisplayNumber { get; }

    public int? SeasonNumber { get; }

    public bool IsUnit => UnitKind is not null;
}

public enum CatalogMediaSourceLayout
{
    SingleFile = 1,
    ImageSequence = 2
}

public sealed record CatalogMediaSourceDetails(
    string AssetRole,
    CatalogMediaSourceLayout Layout,
    int? FileIndex,
    string? FilePath,
    string? MimeType,
    string? Container,
    string? VideoQuality,
    string? Language,
    long? SizeBytes,
    int SortOrder,
    CatalogTorrentDetails Torrent);

public sealed record CatalogTorrentDetails(
    string? DisplayName,
    string? MagnetUri,
    ReadOnlyMemory<byte> TorrentMetadata,
    string? InfoHashV1,
    string? InfoHashV2,
    bool IsPrivate,
    long? TotalSize,
    string? SourceUri,
    IReadOnlyList<CatalogTrackerDetails> Trackers,
    string Language = LanguageTag.Undetermined);

public sealed record CatalogTrackerDetails(
    string? Name,
    Uri Endpoint,
    string Transport,
    string ClientFamily,
    bool RequiresAuthentication,
    int Tier,
    int SortOrder,
    string? CredentialKey = null)
{
    public override string ToString()
    {
        var port = Endpoint.IsDefaultPort ? string.Empty : $":{Endpoint.Port}";
        return $"{Transport} tracker {Endpoint.Scheme}://{Endpoint.IdnHost}{port}/…";
    }
}

public sealed record CatalogDistributionRightDetails(
    string SourceName,
    Uri SourceUri,
    string LicenseName,
    Uri? LicenseUri,
    string? Attribution,
    string? RightsNote,
    string? VerifiedAt);
