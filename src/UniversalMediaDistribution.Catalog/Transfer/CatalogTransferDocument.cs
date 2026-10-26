using System.Text.Json.Serialization;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

// Defaulted members remain settable so source-generated JSON deserialization preserves
// their initial values when optional properties are omitted from a transfer document.

public sealed record CatalogTransferDocument
{
    public const string CurrentFormat = "universal-media-distribution.catalog";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = CurrentFormat;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? SourceLabel { get; init; }

    public IReadOnlyList<CatalogTransferItem> Items { get; set; } = [];
}

public sealed record CatalogTransferItem
{
    /// <summary>
    /// The item's sync identity: minted by <see cref="CatalogStableId"/> when
    /// the item first exists and never changed afterwards, so it survives the
    /// renames and merges that display names cannot. Null on documents from
    /// devices that predate identities; those resolve by name key as before.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("syncId")]
    public Guid? Id { get; init; }

    /// <summary>
    /// When the item itself was last edited — not its units. Newest edit wins
    /// when two devices changed the same title; absent on documents from
    /// devices that predate item versions, which lose ties to any version.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserSyncVersion? SyncVersion { get; init; }

    /// <summary>
    /// A tombstone: the title was removed from the account. It keeps its
    /// identity and version so the deletion can win or lose against edits
    /// like any other change, and so devices can suppress re-creating it.
    /// </summary>
    public bool Deleted { get; init; }

    public required string MediaType { get; init; }

    public required string Subtype { get; init; }

    public required string Name { get; init; }

    public string? OriginalName { get; init; }

    public string? SortName { get; init; }

    public string? Description { get; init; }

    public string? ReleaseDate { get; init; }

    /// <summary>
    /// Original release or first-air year. Carried separately from
    /// <see cref="ReleaseDate"/> because many sources publish a year and no
    /// date, and a bare year in a date field cannot be told apart from a real
    /// one. Null means unknown, in which case the year is derived from
    /// <see cref="ReleaseDate"/>.
    /// </summary>
    public int? ReleaseYear { get; init; }

    public string? EndDate { get; init; }

    public string? PublicationStatus { get; init; }

    public string? OriginalLanguage { get; init; }

    public string? CountryCode { get; init; }

    public string? ContentRating { get; init; }

    public int? RuntimeSeconds { get; init; }

    public int? PageCount { get; init; }

    public string? ThumbnailRef { get; init; }

    public string? BackdropRef { get; init; }

    public IReadOnlyList<string> Genres { get; set; } = [];

    public IReadOnlyList<string> Categories { get; set; } = [];

    public IReadOnlyList<CatalogTransferAlias> AlternativeNames { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CatalogTransferAlias>? Aliases { get; set; }

    public IReadOnlyList<CatalogTransferContributor> Contributors { get; set; } = [];

    public IReadOnlyList<CatalogTransferSeason> Seasons { get; set; } = [];

    public IReadOnlyList<CatalogTransferUnit> Units { get; set; } = [];

    public IReadOnlyList<CatalogTransferRight> DistributionRights { get; set; } = [];

    public IReadOnlyList<CatalogTransferMediaSource> Sources { get; set; } = [];
}

public sealed record CatalogTransferAlias
{
    public required string Name { get; init; }

    public string? Language { get; init; }

    public string AliasType { get; set; } = "other";
}

public sealed record CatalogTransferContributor
{
    public required string Name { get; init; }

    public string Kind { get; set; } = "person";

    public string? Url { get; init; }

    public string? ThumbnailRef { get; init; }

    public IReadOnlyList<CatalogTransferCredit> Credits { get; set; } = [];
}

public sealed record CatalogTransferCredit
{
    public required string Role { get; init; }

    public string? CharacterName { get; init; }

    public int SortOrder { get; init; }
}

public sealed record CatalogTransferSeason
{
    public int Number { get; init; }

    public int SortOrder { get; init; }

    public string? DisplayName { get; init; }

    public string? ThumbnailRef { get; init; }

    public string? ReleaseDate { get; init; }

    public IReadOnlyList<CatalogTransferUnit> Units { get; set; } = [];
}

public sealed record CatalogTransferUnit
{
    public required string Kind { get; init; }

    public required string DisplayNumber { get; init; }

    public int SortOrder { get; init; }

    public string? Name { get; init; }

    public string? ThumbnailRef { get; init; }

    public int? DurationSeconds { get; init; }

    public int? PageCount { get; init; }

    public string? ReleaseDate { get; init; }

    public bool IsSpecial { get; init; }

    public IReadOnlyList<CatalogTransferMediaSource> Sources { get; set; } = [];
}

public sealed record CatalogTransferRight
{
    public required string SourceName { get; init; }

    public required string SourceUri { get; init; }

    public required string LicenseName { get; init; }

    public string? LicenseUri { get; init; }

    public string? Attribution { get; init; }

    public string? RightsNote { get; init; }

    public string? VerifiedAt { get; init; }
}

public sealed record CatalogTransferMediaSource
{
    public string AssetRole { get; set; } = "primary";

    public string SourceLayout { get; set; } = "single_file";

    public int? FileIndex { get; init; }

    public string? FilePath { get; init; }

    public string? MimeType { get; init; }

    public string? Container { get; init; }

    public string? VideoQuality { get; init; }

    public string? Language { get; init; }

    public long? SizeBytes { get; init; }

    public int SortOrder { get; init; }

    public required CatalogTransferTorrent Torrent { get; init; }
}

public sealed record CatalogTransferTorrent
{
    private string? _language = LanguageTag.Undetermined;

    public string? DisplayName { get; init; }

    public string Language
    {
        get => LanguageTag.Normalize(_language);
        init => _language = value;
    }

    public string? MagnetUri { get; init; }

    public string? TorrentMetadataBase64 { get; init; }

    public string? InfoHashV1 { get; init; }

    public string? InfoHashV2 { get; init; }

    public bool IsPrivate { get; init; }

    public long? TotalSize { get; init; }

    public string? SourceUri { get; init; }

    public IReadOnlyList<CatalogTransferTracker> Trackers { get; set; } = [];
}

public sealed record CatalogTransferTracker
{
    public string? Name { get; init; }

    public required string Endpoint { get; init; }

    public required string Transport { get; init; }

    public required string ClientFamily { get; init; }

    public bool RequiresAuthentication { get; init; }

    [JsonIgnore]
    public string? CredentialKey { get; init; }

    public int Tier { get; init; }

    public int SortOrder { get; init; }

    public override string ToString()
    {
        var display = Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            ? $"{endpoint.Scheme}://{endpoint.IdnHost}" +
              (endpoint.IsDefaultPort ? string.Empty : $":{endpoint.Port}") +
              "/…"
            : "<invalid tracker endpoint>";
        return $"{Transport} tracker {display}";
    }
}
