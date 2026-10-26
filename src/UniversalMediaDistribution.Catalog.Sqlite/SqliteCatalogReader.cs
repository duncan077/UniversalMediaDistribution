using Microsoft.Data.Sqlite;
using System.Globalization;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

internal static partial class SqliteCatalogReader
{
    public static async ValueTask<CatalogCounts> GetCountsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN media_type = 'movie' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN media_type = 'book' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN media_type = 'audio' THEN 1 ELSE 0 END)
            FROM catalog_items;
            """;
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CatalogCounts(0, 0, 0, 0);
        }

        return new CatalogCounts(
            checked((int)reader.GetInt64(0)),
            reader.IsDBNull(1) ? 0 : checked((int)reader.GetInt64(1)),
            reader.IsDBNull(2) ? 0 : checked((int)reader.GetInt64(2)),
            reader.IsDBNull(3) ? 0 : checked((int)reader.GetInt64(3)));
    }

    /// <summary>
    /// The newest unit of every title in one pass, so a shelf of cards costs
    /// one query rather than one per card.
    /// <para>
    /// Specials are left out: they carry their own numbering, so one would
    /// present itself as the latest chapter when it is not the next one.
    /// </para>
    /// </summary>
    public static async ValueTask<IReadOnlyDictionary<Guid, CatalogLatestUnit>>
        GetLatestUnitsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT catalog_item_id, unit_kind, display_number, sort_order
            FROM (
                SELECT catalog_item_id,
                       unit_kind,
                       display_number,
                       sort_order,
                       ROW_NUMBER() OVER (
                           PARTITION BY catalog_item_id
                           ORDER BY sort_order DESC) AS rank
                FROM content_units
                WHERE is_special = 0)
            WHERE rank = 1;
            """;
        var result = new Dictionary<Guid, CatalogLatestUnit>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetString(1) switch
            {
                "episode" => ContentUnitKind.Episode,
                "chapter" => ContentUnitKind.Chapter,
                "track" => ContentUnitKind.Track,
                "audio_chapter" => ContentUnitKind.AudioChapter,
                _ => (ContentUnitKind?)null
            };
            if (kind is null)
            {
                continue;
            }

            result[Guid.Parse(reader.GetString(0))] = new CatalogLatestUnit(
                kind.Value,
                reader.GetString(2),
                reader.GetInt32(3));
        }

        return result;
    }

    public static async ValueTask<CatalogItemDetails?> GetDetailsAsync(
        SqliteConnection connection,
        Guid catalogItemId,
        CancellationToken cancellationToken)
    {
        var databaseId = catalogItemId.ToString("D");
        var items = await ReadItemBuildersAsync(
            connection,
            databaseId,
            cancellationToken);
        if (!items.TryGetValue(databaseId, out var selectedItem))
        {
            return null;
        }

        await ReadGenresAsync(connection, items, databaseId, cancellationToken);
        await ReadCategoriesAsync(connection, items, databaseId, cancellationToken);
        await ReadAliasesAsync(connection, items, databaseId, cancellationToken);
        await ReadContributorsAsync(connection, items, databaseId, cancellationToken);
        var seasons = await ReadSeasonsAsync(
            connection,
            items,
            databaseId,
            cancellationToken);
        var units = await ReadUnitsAsync(
            connection,
            items,
            seasons,
            databaseId,
            cancellationToken);
        await ReadRightsAsync(connection, items, databaseId, cancellationToken);
        var torrents = await ReadTorrentsAsync(
            connection,
            databaseId,
            cancellationToken);
        await ReadTrackersAsync(
            connection,
            torrents,
            includeCredentialKeys: true,
            databaseId,
            cancellationToken);
        await ReadSourcesAsync(
            connection,
            items,
            units,
            torrents,
            databaseId,
            cancellationToken);

        return CatalogDetailsMapper.ToDetails(
            selectedItem.Build(),
            catalogItemId,
            $"item:{catalogItemId:N}");
    }

    public static async ValueTask<IReadOnlyList<CatalogItem>> ListAsync(
        SqliteConnection connection,
        MediaType? mediaType,
        CancellationToken cancellationToken)
    {
        var rows = new List<ItemRow>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, media_type, subtype_code, name, description,
                       thumbnail_ref, backdrop_ref, publication_status,
                       release_date, updated_at, release_year, created_at
                FROM catalog_items
                WHERE ($media_type IS NULL OR media_type = $media_type)
                ORDER BY name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue(
                "$media_type",
                mediaType is null ? DBNull.Value : mediaType.Value.ToString().ToLowerInvariant());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ItemRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    DateTimeOffset.Parse(
                        reader.GetString(9),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    DateTimeOffset.Parse(
                        reader.GetString(11),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
            }
        }

        if (rows.Count == 0)
        {
            return [];
        }

        var genres = rows.ToDictionary(static row => row.DatabaseId, static _ => new List<string>(), StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cig.catalog_item_id, g.name
                FROM catalog_item_genres cig
                JOIN genres g ON g.id = cig.genre_id
                JOIN catalog_items ci ON ci.id = cig.catalog_item_id
                WHERE ($media_type IS NULL OR ci.media_type = $media_type)
                ORDER BY g.name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue(
                "$media_type",
                mediaType is null
                    ? DBNull.Value
                    : mediaType.Value.ToString().ToLowerInvariant());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (genres.TryGetValue(reader.GetString(0), out var itemGenres))
                {
                    itemGenres.Add(reader.GetString(1));
                }
            }
        }

        var categories = rows.ToDictionary(
            static row => row.DatabaseId,
            static _ => new List<string>(),
            StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cic.catalog_item_id, c.name
                FROM catalog_item_categories cic
                JOIN categories c ON c.id = cic.category_id
                JOIN catalog_items ci ON ci.id = cic.catalog_item_id
                WHERE ($media_type IS NULL OR ci.media_type = $media_type)
                ORDER BY c.name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue(
                "$media_type",
                mediaType is null
                    ? DBNull.Value
                    : mediaType.Value.ToString().ToLowerInvariant());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (categories.TryGetValue(reader.GetString(0), out var itemCategories))
                {
                    itemCategories.Add(reader.GetString(1));
                }
            }
        }

        var alternativeNames = rows.ToDictionary(
            static row => row.DatabaseId,
            static _ => new List<CatalogAlternativeName>(),
            StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT can.catalog_item_id, can.name, can.language, can.alias_type
                FROM catalog_alternative_names can
                JOIN catalog_items ci ON ci.id = can.catalog_item_id
                WHERE ($media_type IS NULL OR ci.media_type = $media_type)
                ORDER BY can.name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue(
                "$media_type",
                mediaType is null
                    ? DBNull.Value
                    : mediaType.Value.ToString().ToLowerInvariant());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (alternativeNames.TryGetValue(reader.GetString(0), out var itemAlternativeNames))
                {
                    itemAlternativeNames.Add(new CatalogAlternativeName(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)));
                }
            }
        }

        var contributors = rows.ToDictionary(
            static row => row.DatabaseId,
            static _ => new Dictionary<string, ListContributorBuilder>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cic.catalog_item_id, c.id, c.name, c.kind, c.url,
                       c.thumbnail_ref, cic.role, cic.character_name, cic.sort_order
                FROM catalog_item_credits cic
                JOIN contributors c ON c.id = cic.contributor_id
                JOIN catalog_items ci ON ci.id = cic.catalog_item_id
                WHERE ($media_type IS NULL OR ci.media_type = $media_type)
                ORDER BY cic.catalog_item_id, cic.sort_order, c.name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue(
                "$media_type",
                mediaType is null
                    ? DBNull.Value
                    : mediaType.Value.ToString().ToLowerInvariant());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!contributors.TryGetValue(reader.GetString(0), out var itemContributors))
                {
                    continue;
                }

                var contributorId = reader.GetString(1);
                if (!itemContributors.TryGetValue(contributorId, out var contributor))
                {
                    contributor = new ListContributorBuilder(
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5));
                    itemContributors.Add(contributorId, contributor);
                }

                contributor.Credits.Add(new CatalogContributorCredit(
                    reader.GetString(6),
                    string.IsNullOrWhiteSpace(reader.GetString(7))
                        ? null
                        : reader.GetString(7),
                    reader.GetInt32(8)));
            }
        }

        return rows.Select(row => new CatalogItem(
                Guid.Parse(row.DatabaseId),
                CatalogSubtypeResolver.ParseMediaType(row.MediaType),
                row.SubtypeCode,
                row.Name,
                row.Description,
                row.ThumbnailRef,
                row.BackdropRef,
                Enum.IsDefined(typeof(PublicationStatus), row.PublicationStatus)
                    ? (PublicationStatus)row.PublicationStatus
                    : PublicationStatus.Unknown,
                genres[row.DatabaseId],
                categories[row.DatabaseId],
                alternativeNames[row.DatabaseId],
                contributors[row.DatabaseId].Values.Select(static contributor =>
                    contributor.Build()),
                row.ReleaseDate,
                row.UpdatedAt,
                row.ReleaseYear,
                row.CreatedAt))
            .ToArray();
    }

    public static ValueTask<CatalogTransferDocument> ExportAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        ExportCompleteCatalogAsync(connection, cancellationToken);

    private sealed record ItemRow(
        string DatabaseId,
        string MediaType,
        string SubtypeCode,
        string Name,
        string Description,
        string? ThumbnailRef,
        string? BackdropRef,
        int PublicationStatus,
        string? ReleaseDate,
        DateTimeOffset UpdatedAt,
        int? ReleaseYear,
        DateTimeOffset CreatedAt);

    private sealed class ListContributorBuilder(
        string name,
        string kind,
        string? url,
        string? thumbnailRef)
    {
        public List<CatalogContributorCredit> Credits { get; } = [];

        public CatalogContributor Build() =>
            new(name, kind, url, thumbnailRef, Credits);
    }
}
