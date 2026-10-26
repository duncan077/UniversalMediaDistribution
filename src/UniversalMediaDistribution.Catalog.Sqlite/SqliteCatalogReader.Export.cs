using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

internal static partial class SqliteCatalogReader
{
    public static async ValueTask<CatalogTransferDocument> ExportMatchingItemsAsync(
        SqliteConnection connection, IReadOnlyList<CatalogTransferItem> candidates,
        CancellationToken cancellationToken)
    {
        var identities = await ExportIdentitiesAsync(connection, cancellationToken);
        var matches = new Dictionary<string, CatalogTransferItem>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CatalogTransferIdentity.FindBestMatch(identities.Items, candidate) is { } match)
                matches.TryAdd(CatalogTransferIdentity.GetKey(match), match);
        }

        // For broad updates a single batched read beats many per-title
        // queries. Sparse refreshes avoid hydrating unrelated chapter graphs.
        if (matches.Count > 0 && matches.Count * 4 >= identities.Items.Count)
            return await ExportCompleteCatalogAsync(connection, cancellationToken);

        var items = new List<CatalogTransferItem>();
        foreach (var match in matches.Values)
        {
            if (match.Deleted)
            {
                items.Add(match);
                continue;
            }
            await using var command = connection.CreateCommand();
            if (match.Id is { } syncId)
            {
                command.CommandText = "SELECT id FROM catalog_items WHERE sync_id = $id;";
                command.Parameters.AddWithValue("$id", syncId.ToString("D"));
            }
            else
            {
                command.CommandText = "SELECT id FROM catalog_items WHERE subtype_code = $subtype AND name_key = $name;";
                command.Parameters.AddWithValue("$subtype", CatalogSubtypeResolver.ResolveCode(
                    CatalogSubtypeResolver.ParseMediaType(match.MediaType), match.Subtype));
                command.Parameters.AddWithValue("$name", NameNormalizer.Normalize(match.Name));
            }
            if (await command.ExecuteScalarAsync(cancellationToken) is string id)
            {
                var single = await ExportCompleteCatalogAsync(connection, cancellationToken, id);
                items.AddRange(single.Items);
            }
        }
        return new CatalogTransferDocument { Items = items };
    }

    // Import convergence needs names, formats and synchronization identities,
    // never the library's descriptions, episodes, sources or torrent graphs.
    public static async ValueTask<CatalogTransferDocument> ExportIdentitiesAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, ItemBuilder>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, media_type, subtype_code, name, original_name,
                       original_language, sync_id
                FROM catalog_items;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                items.Add(id, new ItemBuilder
                {
                    DatabaseId = id,
                    MediaType = reader.GetString(1),
                    Subtype = ShortSubtype(reader.GetString(2)),
                    Name = reader.GetString(3),
                    OriginalName = GetNullableString(reader, 4),
                    OriginalLanguage = GetNullableString(reader, 5),
                    SyncId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))
                });
            }
        }
        await ReadAliasesAsync(connection, items, null, cancellationToken);
        var tombstones = await ReadTombstoneItemsAsync(connection, cancellationToken);
        return new CatalogTransferDocument
        {
            Items = items.Values
                .OrderBy(static item => item.MediaType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static item => item.Build())
                .Concat(tombstones).ToArray()
        };
    }

    /// <summary>
    /// Exports the catalog, or one title of it.
    /// <para>
    /// Every reader below already accepted a filter that nothing ever passed.
    /// Threading it through is what lets a progress patch carry the handful of
    /// titles it touches instead of the 2.5 MiB the whole catalog comes to.
    /// </para>
    /// </summary>
    private static async ValueTask<CatalogTransferDocument> ExportCompleteCatalogAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        string? catalogItemId = null)
    {
        var items = await ReadItemBuildersAsync(
            connection,
            catalogItemId,
            cancellationToken);
        if (items.Count == 0)
        {
            // A catalog holding nothing but deletions still has deletions to
            // report; skipping them here is how a device that deleted its
            // last title would fail to tell the others.
            return new CatalogTransferDocument
            {
                ExportedAt = DateTimeOffset.UtcNow,
                SourceLabel = "SQLite catalog",
                Items = catalogItemId is null
                    ? (await ReadTombstoneItemsAsync(
                        connection,
                        cancellationToken)).ToArray()
                    : []
            };
        }

        await ReadGenresAsync(connection, items, catalogItemId, cancellationToken);
        await ReadCategoriesAsync(connection, items, catalogItemId, cancellationToken);
        await ReadAliasesAsync(connection, items, catalogItemId, cancellationToken);
        await ReadContributorsAsync(connection, items, catalogItemId, cancellationToken);
        var seasons = await ReadSeasonsAsync(
            connection,
            items,
            catalogItemId,
            cancellationToken);
        var units = await ReadUnitsAsync(
            connection,
            items,
            seasons,
            catalogItemId,
            cancellationToken);
        await ReadRightsAsync(connection, items, catalogItemId, cancellationToken);
        var torrents = await ReadTorrentsAsync(
            connection,
            catalogItemId,
            cancellationToken);
        await ReadTrackersAsync(
            connection,
            torrents,
            includeCredentialKeys: false,
            catalogItemId,
            cancellationToken);
        await ReadSourcesAsync(
            connection,
            items,
            units,
            torrents,
            catalogItemId,
            cancellationToken);

        // Deletions travel with the catalog. Only recent ones: a device
        // offline past the horizon re-imports the title at worst, which is
        // annoying rather than corrupting, and the table stays bounded.
        IReadOnlyList<CatalogTransferItem> tombstones = catalogItemId is null
            ? await ReadTombstoneItemsAsync(connection, cancellationToken)
            : [];

        return new CatalogTransferDocument
        {
            ExportedAt = DateTimeOffset.UtcNow,
            SourceLabel = "SQLite catalog",
            Items = items.Values
                .OrderBy(static item => item.MediaType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static item => item.Build())
                .Concat(tombstones)
                .ToArray()
        };
    }

    private static async ValueTask<IReadOnlyList<CatalogTransferItem>>
        ReadTombstoneItemsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sync_id, media_type, subtype_code, name, clock, device_id,
                   operation_id, modified_at
            FROM catalog_tombstones
            WHERE modified_at >= $horizon;
            """;
        command.Parameters.AddWithValue(
            "$horizon",
            DateTimeOffset.UtcNow.AddDays(-180).ToString(
                "O",
                CultureInfo.InvariantCulture));
        var tombstones = new List<CatalogTransferItem>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tombstones.Add(new CatalogTransferItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                Deleted = true,
                MediaType = reader.GetString(1),
                Subtype = ShortSubtype(reader.GetString(2)),
                Name = reader.GetString(3),
                SyncVersion = new UserSyncVersion
                {
                    Clock = reader.GetInt64(4),
                    DeviceId = reader.GetString(5),
                    OperationId = Guid.Parse(reader.GetString(6)),
                    ModifiedAt = DateTimeOffset.Parse(
                        reader.GetString(7),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)
                }
            });
        }

        return tombstones;
    }

    /// <summary>
    /// Exports only the named titles, complete with their units and seasons.
    /// </summary>
    public static async ValueTask<CatalogTransferDocument> ExportItemsAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> catalogItemIds,
        CancellationToken cancellationToken)
    {
        var items = new List<CatalogTransferItem>();
        foreach (var catalogItemId in catalogItemIds)
        {
            var single = await ExportCompleteCatalogAsync(
                connection,
                cancellationToken,
                catalogItemId);
            items.AddRange(single.Items);
        }

        return new CatalogTransferDocument
        {
            ExportedAt = DateTimeOffset.UtcNow,
            SourceLabel = "SQLite catalog",
            Items = items
                .OrderBy(static item => item.MediaType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static async ValueTask<Dictionary<string, ItemBuilder>> ReadItemBuildersAsync(
        SqliteConnection connection,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, ItemBuilder>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, media_type, subtype_code, name, original_name, sort_name,
                   description, release_date, end_date, publication_status,
                   original_language, country_code, content_rating, runtime_seconds,
                   page_count, thumbnail_ref, backdrop_ref, release_year,
                   sync_id, sync_clock, sync_device, sync_operation,
                   sync_modified
            FROM catalog_items
            WHERE ($catalog_item_id IS NULL OR id = $catalog_item_id);
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            items.Add(id, new ItemBuilder
            {
                DatabaseId = id,
                MediaType = reader.GetString(1),
                Subtype = ShortSubtype(reader.GetString(2)),
                Name = reader.GetString(3),
                OriginalName = GetNullableString(reader, 4),
                SortName = GetNullableString(reader, 5),
                Description = reader.GetString(6),
                ReleaseDate = GetNullableString(reader, 7),
                EndDate = GetNullableString(reader, 8),
                PublicationStatus = PublicationStatusToString(reader.GetInt32(9)),
                OriginalLanguage = GetNullableString(reader, 10),
                CountryCode = GetNullableString(reader, 11),
                ContentRating = GetNullableString(reader, 12),
                RuntimeSeconds = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                PageCount = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                ThumbnailRef = GetNullableString(reader, 15),
                BackdropRef = GetNullableString(reader, 16),
                ReleaseYear = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                SyncId = reader.IsDBNull(18)
                    ? null
                    : Guid.Parse(reader.GetString(18)),
                SyncVersion = reader.IsDBNull(19) || reader.IsDBNull(20)
                    ? null
                    : new UserSyncVersion
                    {
                        Clock = reader.GetInt64(19),
                        DeviceId = reader.GetString(20),
                        OperationId = Guid.Parse(reader.GetString(21)),
                        ModifiedAt = DateTimeOffset.Parse(
                            reader.GetString(22),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind)
                    }
            });
        }

        return items;
    }

    private static async ValueTask ReadGenresAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cig.catalog_item_id, g.name
            FROM catalog_item_genres cig
            JOIN genres g ON g.id = cig.genre_id
            WHERE ($catalog_item_id IS NULL OR cig.catalog_item_id = $catalog_item_id)
            ORDER BY g.name COLLATE NOCASE;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (items.TryGetValue(reader.GetString(0), out var item))
            {
                item.Genres.Add(reader.GetString(1));
            }
        }
    }

    private static async ValueTask ReadCategoriesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cic.catalog_item_id, c.name
            FROM catalog_item_categories cic
            JOIN categories c ON c.id = cic.category_id
            WHERE ($catalog_item_id IS NULL OR cic.catalog_item_id = $catalog_item_id)
            ORDER BY c.name COLLATE NOCASE;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (items.TryGetValue(reader.GetString(0), out var item))
            {
                item.Categories.Add(reader.GetString(1));
            }
        }
    }

    private static async ValueTask ReadAliasesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT catalog_item_id, name, language, alias_type
            FROM catalog_alternative_names
            WHERE ($catalog_item_id IS NULL OR catalog_item_id = $catalog_item_id)
            ORDER BY name COLLATE NOCASE;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (items.TryGetValue(reader.GetString(0), out var item))
            {
                item.AlternativeNames.Add(new CatalogTransferAlias
                {
                    Name = reader.GetString(1),
                    Language = reader.GetString(2),
                    AliasType = reader.GetString(3)
                });
            }
        }
    }

    private static async ValueTask ReadContributorsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cic.catalog_item_id, c.id, c.name, c.kind, c.url,
                   c.thumbnail_ref, cic.role, cic.character_name, cic.sort_order
            FROM catalog_item_credits cic
            JOIN contributors c ON c.id = cic.contributor_id
            WHERE ($catalog_item_id IS NULL OR cic.catalog_item_id = $catalog_item_id)
            ORDER BY cic.catalog_item_id, cic.sort_order, c.name COLLATE NOCASE;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!items.TryGetValue(reader.GetString(0), out var item))
            {
                continue;
            }

            var contributorId = reader.GetString(1);
            if (!item.Contributors.TryGetValue(contributorId, out var contributor))
            {
                contributor = new ContributorBuilder
                {
                    Name = reader.GetString(2),
                    Kind = reader.GetString(3),
                    Url = GetNullableString(reader, 4),
                    ThumbnailRef = GetNullableString(reader, 5)
                };
                item.Contributors.Add(contributorId, contributor);
            }

            contributor.Credits.Add(new CatalogTransferCredit
            {
                Role = reader.GetString(6),
                CharacterName = NullIfEmpty(reader.GetString(7)),
                SortOrder = reader.GetInt32(8)
            });
        }
    }

    private static async ValueTask<Dictionary<string, SeasonBuilder>> ReadSeasonsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        var seasons = new Dictionary<string, SeasonBuilder>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, catalog_item_id, season_number, display_name,
                   thumbnail_ref, release_date, sort_order
            FROM seasons
            WHERE ($catalog_item_id IS NULL OR catalog_item_id = $catalog_item_id)
            ORDER BY catalog_item_id, sort_order, season_number;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = reader.GetString(1);
            if (!items.TryGetValue(itemId, out var item))
            {
                continue;
            }

            var season = new SeasonBuilder
            {
                DatabaseId = reader.GetString(0),
                Number = reader.GetInt32(2),
                DisplayName = GetNullableString(reader, 3),
                ThumbnailRef = GetNullableString(reader, 4),
                ReleaseDate = GetNullableString(reader, 5),
                SortOrder = reader.GetInt32(6)
            };
            seasons.Add(season.DatabaseId, season);
            item.Seasons.Add(season);
        }

        return seasons;
    }

    private static async ValueTask<Dictionary<string, UnitBuilder>> ReadUnitsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        IReadOnlyDictionary<string, SeasonBuilder> seasons,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        var units = new Dictionary<string, UnitBuilder>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, catalog_item_id, season_id, unit_kind, display_number,
                   name, thumbnail_ref, duration_seconds, page_count,
                   release_date, is_special, sort_order
            FROM content_units
            WHERE ($catalog_item_id IS NULL OR catalog_item_id = $catalog_item_id)
            ORDER BY catalog_item_id, season_id, sort_order;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var unit = new UnitBuilder
            {
                DatabaseId = reader.GetString(0),
                Kind = reader.GetString(3),
                DisplayNumber = reader.GetString(4),
                Name = GetNullableString(reader, 5),
                ThumbnailRef = GetNullableString(reader, 6),
                DurationSeconds = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                PageCount = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                ReleaseDate = GetNullableString(reader, 9),
                IsSpecial = reader.GetInt32(10) != 0,
                SortOrder = reader.GetInt32(11)
            };
            units.Add(unit.DatabaseId, unit);

            if (!reader.IsDBNull(2) && seasons.TryGetValue(reader.GetString(2), out var season))
            {
                season.Units.Add(unit);
            }
            else if (items.TryGetValue(reader.GetString(1), out var item))
            {
                item.Units.Add(unit);
            }
        }

        return units;
    }

    private static async ValueTask ReadRightsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT catalog_item_id, source_name, source_uri, license_name,
                   license_uri, attribution, rights_note, verified_at
            FROM distribution_rights
            WHERE ($catalog_item_id IS NULL OR catalog_item_id = $catalog_item_id)
            ORDER BY source_name COLLATE NOCASE;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (items.TryGetValue(reader.GetString(0), out var item))
            {
                item.Rights.Add(new CatalogTransferRight
                {
                    SourceName = reader.GetString(1),
                    SourceUri = reader.GetString(2),
                    LicenseName = reader.GetString(3),
                    LicenseUri = GetNullableString(reader, 4),
                    Attribution = GetNullableString(reader, 5),
                    RightsNote = GetNullableString(reader, 6),
                    VerifiedAt = GetNullableString(reader, 7)
                });
            }
        }
    }

    private static async ValueTask<Dictionary<string, TorrentBuilder>> ReadTorrentsAsync(
        SqliteConnection connection,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        var torrents = new Dictionary<string, TorrentBuilder>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, language, magnet_uri, torrent_metadata,
                   info_hash_v1, info_hash_v2, is_private, total_size, source_uri
            FROM torrent_packages tp
            WHERE $catalog_item_id IS NULL OR EXISTS (
                SELECT 1
                FROM media_sources ms
                LEFT JOIN content_units cu ON cu.id = ms.content_unit_id
                WHERE ms.torrent_package_id = tp.id
                  AND (
                      ms.catalog_item_id = $catalog_item_id OR
                      cu.catalog_item_id = $catalog_item_id
                  )
            );
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var torrent = new TorrentBuilder
            {
                DatabaseId = reader.GetString(0),
                DisplayName = GetNullableString(reader, 1),
                Language = reader.GetString(2),
                MagnetUri = GetNullableString(reader, 3),
                TorrentMetadataBase64 = reader.IsDBNull(4) ? null : Convert.ToBase64String((byte[])reader.GetValue(4)),
                InfoHashV1 = GetNullableString(reader, 5),
                InfoHashV2 = GetNullableString(reader, 6),
                IsPrivate = reader.GetInt32(7) != 0,
                TotalSize = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                SourceUri = GetNullableString(reader, 9)
            };
            torrents.Add(torrent.DatabaseId, torrent);
        }

        return torrents;
    }

    private static async ValueTask ReadTrackersAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, TorrentBuilder> torrents,
        bool includeCredentialKeys,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tpt.torrent_package_id, te.name, te.endpoint_template,
                    te.transport, te.client_family, te.requires_authentication,
                    tpt.tier, tpt.sort_order, te.credential_key
            FROM torrent_package_trackers tpt
            JOIN tracker_endpoints te ON te.id = tpt.tracker_endpoint_id
            WHERE te.is_enabled = 1
              AND (
                  $catalog_item_id IS NULL OR EXISTS (
                      SELECT 1
                      FROM media_sources ms
                      LEFT JOIN content_units cu ON cu.id = ms.content_unit_id
                      WHERE ms.torrent_package_id = tpt.torrent_package_id
                        AND (
                            ms.catalog_item_id = $catalog_item_id OR
                            cu.catalog_item_id = $catalog_item_id
                        )
                  )
              )
            ORDER BY tpt.tier, tpt.sort_order;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (torrents.TryGetValue(reader.GetString(0), out var torrent))
            {
                torrent.Trackers.Add(new CatalogTransferTracker
                {
                    Name = GetNullableString(reader, 1),
                    Endpoint = reader.GetString(2),
                    Transport = reader.GetString(3),
                    ClientFamily = reader.GetString(4),
                    RequiresAuthentication = reader.GetInt32(5) != 0,
                    Tier = reader.GetInt32(6),
                    SortOrder = reader.GetInt32(7),
                    CredentialKey = includeCredentialKeys
                        ? GetNullableString(reader, 8)
                        : null
                });
            }
        }
    }

    private static async ValueTask ReadSourcesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ItemBuilder> items,
        IReadOnlyDictionary<string, UnitBuilder> units,
        IReadOnlyDictionary<string, TorrentBuilder> torrents,
        string? catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT torrent_package_id, catalog_item_id, content_unit_id,
                   asset_role, source_layout, file_index, file_path, mime_type,
                   container, video_quality, language, size_bytes, sort_order
            FROM media_sources
            WHERE (
                $catalog_item_id IS NULL OR
                catalog_item_id = $catalog_item_id OR
                content_unit_id IN (
                    SELECT id
                    FROM content_units
                    WHERE catalog_item_id = $catalog_item_id
                )
            )
            ORDER BY sort_order, file_path;
            """;
        AddCatalogItemParameter(command, catalogItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!torrents.TryGetValue(reader.GetString(0), out var torrent))
            {
                continue;
            }

            var source = new CatalogTransferMediaSource
            {
                AssetRole = reader.GetString(3),
                SourceLayout = reader.GetString(4),
                FileIndex = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                FilePath = NullIfEmpty(reader.GetString(6)),
                MimeType = GetNullableString(reader, 7),
                Container = GetNullableString(reader, 8),
                VideoQuality = GetNullableString(reader, 9),
                Language = GetNullableString(reader, 10),
                SizeBytes = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                SortOrder = reader.GetInt32(12),
                Torrent = torrent.Build()
            };

            if (!reader.IsDBNull(1) && items.TryGetValue(reader.GetString(1), out var item))
            {
                item.Sources.Add(source);
            }
            else if (!reader.IsDBNull(2) && units.TryGetValue(reader.GetString(2), out var unit))
            {
                unit.Sources.Add(source);
            }
        }
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddCatalogItemParameter(
        SqliteCommand command,
        string? catalogItemId) =>
        command.Parameters.AddWithValue(
            "$catalog_item_id",
            catalogItemId is null ? DBNull.Value : catalogItemId);

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static string ShortSubtype(string subtypeCode)
    {
        var separator = subtypeCode.IndexOf('.');
        return separator < 0 ? subtypeCode : subtypeCode[(separator + 1)..];
    }

    private static string PublicationStatusToString(int value) =>
        Enum.IsDefined(typeof(PublicationStatus), value)
            ? ((PublicationStatus)value).ToString().ToLowerInvariant()
            : PublicationStatus.Unknown.ToString().ToLowerInvariant();

    private sealed class ItemBuilder
    {
        public required string DatabaseId { get; init; }
        public Guid? SyncId { get; init; }
        public UserSyncVersion? SyncVersion { get; init; }
        public required string MediaType { get; init; }
        public required string Subtype { get; init; }
        public required string Name { get; init; }
        public string? OriginalName { get; init; }
        public string? SortName { get; init; }
        public string? Description { get; init; }
        public string? ReleaseDate { get; init; }
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
        public List<string> Genres { get; } = [];
        public List<string> Categories { get; } = [];
        public List<CatalogTransferAlias> AlternativeNames { get; } = [];
        public Dictionary<string, ContributorBuilder> Contributors { get; } = new(StringComparer.Ordinal);
        public List<SeasonBuilder> Seasons { get; } = [];
        public List<UnitBuilder> Units { get; } = [];
        public List<CatalogTransferRight> Rights { get; } = [];
        public List<CatalogTransferMediaSource> Sources { get; } = [];

        public CatalogTransferItem Build() => new()
        {
            Id = SyncId,
            SyncVersion = SyncVersion,
            MediaType = MediaType,
            Subtype = Subtype,
            Name = Name,
            OriginalName = OriginalName,
            SortName = SortName,
            Description = Description,
            ReleaseDate = ReleaseDate,
            ReleaseYear = ReleaseYear,
            EndDate = EndDate,
            PublicationStatus = PublicationStatus,
            OriginalLanguage = OriginalLanguage,
            CountryCode = CountryCode,
            ContentRating = ContentRating,
            RuntimeSeconds = RuntimeSeconds,
            PageCount = PageCount,
            ThumbnailRef = ThumbnailRef,
            BackdropRef = BackdropRef,
            Genres = Genres.ToArray(),
            Categories = Categories.ToArray(),
            AlternativeNames = AlternativeNames.ToArray(),
            Contributors = Contributors.Values
                .OrderBy(static contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static contributor => contributor.Build())
                .ToArray(),
            Seasons = Seasons.OrderBy(static season => season.SortOrder).Select(static season => season.Build()).ToArray(),
            Units = Units.OrderBy(static unit => unit.SortOrder).Select(static unit => unit.Build()).ToArray(),
            DistributionRights = Rights.ToArray(),
            Sources = Sources.OrderBy(static source => source.SortOrder).ToArray()
        };
    }

    private sealed class ContributorBuilder
    {
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public string? Url { get; init; }
        public string? ThumbnailRef { get; init; }
        public List<CatalogTransferCredit> Credits { get; } = [];

        public CatalogTransferContributor Build() => new()
        {
            Name = Name,
            Kind = Kind,
            Url = Url,
            ThumbnailRef = ThumbnailRef,
            Credits = Credits.OrderBy(static credit => credit.SortOrder).ToArray()
        };
    }

    private sealed class SeasonBuilder
    {
        public required string DatabaseId { get; init; }
        public int Number { get; init; }
        public int SortOrder { get; init; }
        public string? DisplayName { get; init; }
        public string? ThumbnailRef { get; init; }
        public string? ReleaseDate { get; init; }
        public List<UnitBuilder> Units { get; } = [];

        public CatalogTransferSeason Build() => new()
        {
            Number = Number,
            SortOrder = SortOrder,
            DisplayName = DisplayName,
            ThumbnailRef = ThumbnailRef,
            ReleaseDate = ReleaseDate,
            Units = Units.OrderBy(static unit => unit.SortOrder).Select(static unit => unit.Build()).ToArray()
        };
    }

    private sealed class UnitBuilder
    {
        public required string DatabaseId { get; init; }
        public required string Kind { get; init; }
        public required string DisplayNumber { get; init; }
        public int SortOrder { get; init; }
        public string? Name { get; init; }
        public string? ThumbnailRef { get; init; }
        public int? DurationSeconds { get; init; }
        public int? PageCount { get; init; }
        public string? ReleaseDate { get; init; }
        public bool IsSpecial { get; init; }
        public List<CatalogTransferMediaSource> Sources { get; } = [];

        public CatalogTransferUnit Build() => new()
        {
            Kind = Kind,
            DisplayNumber = DisplayNumber,
            SortOrder = SortOrder,
            Name = Name,
            ThumbnailRef = ThumbnailRef,
            DurationSeconds = DurationSeconds,
            PageCount = PageCount,
            ReleaseDate = ReleaseDate,
            IsSpecial = IsSpecial,
            Sources = Sources.OrderBy(static source => source.SortOrder).ToArray()
        };
    }

    private sealed class TorrentBuilder
    {
        public required string DatabaseId { get; init; }
        public string? DisplayName { get; init; }
        public string Language { get; init; } = LanguageTag.Undetermined;
        public string? MagnetUri { get; init; }
        public string? TorrentMetadataBase64 { get; init; }
        public string? InfoHashV1 { get; init; }
        public string? InfoHashV2 { get; init; }
        public bool IsPrivate { get; init; }
        public long? TotalSize { get; init; }
        public string? SourceUri { get; init; }
        public List<CatalogTransferTracker> Trackers { get; } = [];

        public CatalogTransferTorrent Build() => new()
        {
            DisplayName = DisplayName,
            Language = Language,
            MagnetUri = MagnetUri,
            TorrentMetadataBase64 = TorrentMetadataBase64,
            InfoHashV1 = InfoHashV1,
            InfoHashV2 = InfoHashV2,
            IsPrivate = IsPrivate,
            TotalSize = TotalSize,
            SourceUri = SourceUri,
            Trackers = Trackers.OrderBy(static tracker => tracker.Tier).ThenBy(static tracker => tracker.SortOrder).ToArray()
        };
    }
}
