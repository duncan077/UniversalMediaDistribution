using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;
using UniversalMediaDistribution.Torrenting.Ingestion;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class CatalogPersistenceTests
{
    [Fact]
    public async Task EmptyDatabaseStartsWithoutDemoTitles()
    {
        await using var scope = TestDatabaseScope.Create();

        Assert.Empty(await scope.Repository.ListAsync());
    }

    [Fact]
    public async Task CatalogDetails_ExposeSeasonsUnitsSourcesAndStableIdentities()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.RichCatalog());
        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));

        var details = Assert.IsType<CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(item.Id));
        var unit = Assert.Single(details.Units);
        var source = Assert.Single(unit.Sources);

        Assert.Equal(details.Item.Id, unit.Identity.CatalogItemId);
        Assert.Contains("unit:chapter", unit.Identity.StableKey, StringComparison.Ordinal);
        Assert.Equal(CatalogMediaSourceLayout.ImageSequence, source.Layout);
        Assert.True(source.Torrent.IsPrivate);
        Assert.Equal("ja", source.Torrent.Language);
        var tracker = Assert.Single(source.Torrent.Trackers);
        Assert.Equal("wss", tracker.Transport);
        Assert.Equal("private-webtorrent", tracker.CredentialKey);
        Assert.Equal(["Comics", "Manga"], details.Item.Categories);
        Assert.Equal(2, details.Item.AlternativeNames.Count);
        var contributor = Assert.Single(details.Item.Contributors);
        Assert.Equal("Open Ink Collective", contributor.Name);
        Assert.Equal(
            "https://example.test/open-ink",
            contributor.Url?.AbsoluteUri.TrimEnd('/'));
        Assert.Contains(
            details.Item.AlternativeNames,
            static alternativeName =>
                alternativeName.Name == "The Sky Archive" &&
                alternativeName.Language == "en");
    }

    [Fact]
    public async Task MediaProgressAndReaderMode_PersistForCatalogUnit()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.RichCatalog());
        var item = Assert.Single(await scope.Repository.ListAsync(MediaType.Book));
        var details = Assert.IsType<CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(item.Id));
        var unit = Assert.Single(details.Units);
        var expectedProgress = new MediaProgressState(
            position: 1,
            MediaPositionKind.Page,
            isCompleted: false,
            DateTimeOffset.UtcNow);
        var store = new SqliteUserMediaStateStore(scope.Database);

        await store.SaveProgressAsync(unit.Identity, expectedProgress);
        await store.SaveReaderDisplayModeAsync(details.Identity, ReaderDisplayMode.Continuous);

        var reloadedStore = new SqliteUserMediaStateStore(scope.Database);
        var actualProgress = await reloadedStore.LoadProgressAsync(unit.Identity);
        var actualMode = await reloadedStore.LoadReaderDisplayModeAsync(details.Identity);

        Assert.NotNull(actualProgress);
        Assert.Equal(expectedProgress.Position, actualProgress.Position);
        Assert.Equal(MediaPositionKind.Page, actualProgress.PositionKind);
        Assert.Equal(ReaderDisplayMode.Continuous, actualMode);
        var summary = Assert.Single(
            await reloadedStore.LoadCatalogProgressAsync());
        Assert.Equal(item.Id, summary.CatalogItemId);
        Assert.Equal(unit.DisplayNumber, summary.Content.DisplayNumber);
        Assert.Equal(ContentUnitKind.Chapter, summary.Content.UnitKind);
        Assert.Equal(expectedProgress.Position, summary.Progress.Position);
    }

    [Fact]
    public async Task CatalogProgressReturnsSeenStateForEveryUnitInOneTitle()
    {
        await using var scope = TestDatabaseScope.Create();
        var document = TestCatalogDocuments.RichCatalog();
        var manga = document.Items[0];
        manga.Units =
        [
            .. manga.Units,
            new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = "2",
                SortOrder = 1,
                Name = "Second Light"
            }
        ];
        await scope.Repository.MergeCatalogAsync(document);
        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));
        var details = Assert.IsType<CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(item.Id));
        Assert.Equal(2, details.Units.Count);
        var store = new SqliteUserMediaStateStore(scope.Database);
        foreach (var unit in details.Units)
        {
            await store.SaveProgressAsync(
                unit.Identity,
                new MediaProgressState(
                    0,
                    MediaPositionKind.Page,
                    isCompleted: true,
                    DateTimeOffset.UtcNow));
        }

        var progress = (await store.LoadCatalogProgressAsync())
            .Where(summary =>
                summary.CatalogItemId == item.Id)
            .ToArray();

        Assert.Equal(2, progress.Length);
        Assert.All(
            progress,
            static summary =>
                Assert.True(summary.Progress.IsCompleted));
    }

    [Fact]
    public async Task MagnetOpenPreferences_PersistInCurrentSchema()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteMagnetOpenPreferencesStore(scope.Database);
        var expected = new MagnetOpenPreferences(
            OpenAutomatically: true,
            ImportInfoJson: false,
            TreatExternalMagnetsAsPrivate: true);

        await store.SaveAsync(expected);
        var actual = await new SqliteMagnetOpenPreferencesStore(scope.Database).LoadAsync();

        Assert.Equal(expected, actual);
        Assert.Equal(15L, await ExecuteScalarAsync(scope.DatabasePath, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task MetadataProviderSettings_PersistInCurrentSchema()
    {
        await using var scope = TestDatabaseScope.Create();
        var expected = new MetadataProviderSettings
        {
            TmdbApiKey = "tmdb-key",
            MyAnimeListClientId = "mal-client-id",
            PreferredLanguage = "es-AR"
        };

        await new SqliteMetadataProviderSettingsStore(scope.Database)
            .SaveAsync(expected);
        var actual = await new SqliteMetadataProviderSettingsStore(scope.Database)
            .LoadAsync();

        Assert.Equal(expected, actual);
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM application_settings
                WHERE key = 'metadata_provider_settings';
                """));
    }

    [Fact]
    public async Task ContentSourceProviderSettings_PersistInCurrentSchema()
    {
        await using var scope = TestDatabaseScope.Create();
        var expected = new ContentSourceProviderSettings
        {
            Providers =
            [
                new ContentSourceProviderConfiguration
                {
                    Id = "torrent.community",
                    DisplayName = "Community catalog",
                    Kind = ContentSourceProviderKind.TorrentCatalog,
                    IsEnabled = true,
                    BaseUrl = "https://catalog.example/catalog.json"
                },
                new ContentSourceProviderConfiguration
                {
                    Id = "reader-api",
                    DisplayName = "Reader API",
                    Kind = ContentSourceProviderKind.Api,
                    IsEnabled = false,
                    BaseUrl = "https://reader.example/api"
                }
            ]
        };

        await new SqliteContentSourceProviderSettingsStore(scope.Database)
            .SaveAsync(expected);
        var actual = await new SqliteContentSourceProviderSettingsStore(
                scope.Database)
            .LoadAsync();

        Assert.Equal(2, actual.Providers.Count);
        Assert.Collection(
            actual.Providers,
            provider =>
            {
                Assert.Equal("torrent.community", provider.Id);
                Assert.True(provider.IsEnabled);
                Assert.Equal(
                    "https://catalog.example/catalog.json",
                    provider.BaseUrl);
            },
            provider =>
            {
                Assert.Equal("reader-api", provider.Id);
                Assert.False(provider.IsEnabled);
                Assert.Equal(ContentSourceProviderKind.Api, provider.Kind);
            });
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM application_settings
                WHERE key = 'content_source_provider_settings';
                """));
    }

    [Fact]
    public async Task ContentSourceDailyUpdateDate_PersistsPerProvider()
    {
        await using var scope = TestDatabaseScope.Create();
        var expected = new DateOnly(2026, 7, 23);
        var store = new SqliteContentSourceDailyUpdateStore(
            scope.Database);

        Assert.Null(
            await store.LoadLastSuccessfulDateAsync(
                "reader-api:ABC123"));
        await store.SaveLastSuccessfulDateAsync(
            "reader-api:ABC123",
            expected);

        var actual = await new SqliteContentSourceDailyUpdateStore(
                scope.Database)
            .LoadLastSuccessfulDateAsync("reader-api:ABC123");

        Assert.Equal(expected, actual);
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM application_settings
                WHERE key =
                    'content_source_daily_update.reader-api:ABC123';
                """));
    }

    [Fact]
    public async Task FavoriteContributors_PersistSeparatelyFromCatalogData()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.RichCatalog());
        var item = Assert.Single(await scope.Repository.ListAsync(MediaType.Book));
        var contributor = Assert.Single(item.Contributors);
        var store = new SqliteUserMediaStateStore(scope.Database);

        await store.SetFavoriteContributorAsync(
            contributor.StableKey,
            isFavorite: true);

        var favoriteKeys = await new SqliteUserMediaStateStore(scope.Database)
            .LoadFavoriteContributorKeysAsync();
        var exported = await scope.Repository.ExportCatalogAsync();

        Assert.Contains(contributor.StableKey, favoriteKeys);
        Assert.DoesNotContain(
            "favorite",
            JsonSerializer.Serialize(exported),
            StringComparison.OrdinalIgnoreCase);

        await store.SetFavoriteContributorAsync(
            contributor.StableKey,
            isFavorite: false);
        Assert.Empty(await store.LoadFavoriteContributorKeysAsync());
    }

    [Fact]
    public async Task FavoriteCatalogItems_UseLibraryEntriesAndCanBeRemoved()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));
        var store = new SqliteUserMediaStateStore(scope.Database);

        await store.SetFavoriteCatalogItemAsync(
            item.Id,
            isFavorite: true);

        Assert.Equal(
            [item.Id],
            await new SqliteUserMediaStateStore(scope.Database)
                .LoadFavoriteCatalogItemIdsAsync());

        await store.SetFavoriteCatalogItemAsync(
            item.Id,
            isFavorite: false);

        Assert.Empty(await store.LoadFavoriteCatalogItemIdsAsync());
    }

    [Fact]
    public async Task UserMediaLists_PersistMixedMediaMembershipOutsideCatalogExport()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.RichCatalog());
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("List movie"));
        var items = await scope.Repository.ListAsync();
        var book = Assert.Single(
            items,
            static item => item.MediaType is MediaType.Book);
        var movie = Assert.Single(
            items,
            static item => item.Name == "List movie");
        var store = new SqliteUserMediaStateStore(scope.Database);

        var created = await store.CreateUserListAsync("  Weekend   Mix  ");
        await store.SetUserListMembershipAsync(
            created.Id,
            book.Id,
            isMember: true);
        await store.SetUserListMembershipAsync(
            created.Id,
            movie.Id,
            isMember: true);

        var persisted = Assert.Single(
            await new SqliteUserMediaStateStore(scope.Database)
                .LoadUserListsAsync());
        var exported = await scope.Repository.ExportCatalogAsync();

        Assert.Equal("Weekend Mix", persisted.Name);
        Assert.Equal(2, persisted.ItemCount);
        Assert.Contains(book.Id, persisted.CatalogItemIds);
        Assert.Contains(movie.Id, persisted.CatalogItemIds);
        Assert.DoesNotContain(
            "Weekend Mix",
            JsonSerializer.Serialize(exported),
            StringComparison.Ordinal);

        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("List movie"));
        persisted = Assert.Single(await store.LoadUserListsAsync());
        Assert.Contains(movie.Id, persisted.CatalogItemIds);

        var renamed = await store.RenameUserListAsync(
            created.Id,
            "Everything later");
        Assert.Equal("Everything later", renamed.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateUserListAsync(
                    " everything   LATER ")
                .AsTask());

        await store.SetUserListMembershipAsync(
            created.Id,
            book.Id,
            isMember: false);
        persisted = Assert.Single(await store.LoadUserListsAsync());
        Assert.Equal([movie.Id], persisted.CatalogItemIds);

        await store.DeleteUserListAsync(created.Id);
        Assert.Empty(await store.LoadUserListsAsync());
        Assert.Equal(
            0L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                "SELECT COUNT(*) FROM user_media_list_items;"));
    }

    [Fact]
    public async Task MetadataUpdate_PreservesIdentityTaxonomyAndContentGraph()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.RichCatalog());
        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));
        var before = Assert.IsType<CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(item.Id));

        var after = await scope.Repository.UpdateMetadataAsync(
            new CatalogMetadataUpdate(
                item.Id,
                "Sky Archive Updated",
                "Provider description",
                "https://cdn.myanimelist.net/images/manga/cover.webp",
                [
                    new CatalogAlternativeName(
                        "Sora Archive",
                        "ja",
                        "original")
                ]));

        Assert.Equal(before.Item.Id, after.Item.Id);
        Assert.Equal("Sky Archive Updated", after.Item.Name);
        Assert.Equal("Provider description", after.Item.Description);
        Assert.Equal(before.Item.Genres, after.Item.Genres);
        Assert.Equal(before.Item.Categories, after.Item.Categories);
        Assert.Equal(before.Identity, after.Identity);
        var beforeUnit = Assert.Single(before.Units);
        var afterUnit = Assert.Single(after.Units);
        Assert.Equal(beforeUnit.Identity, afterUnit.Identity);
        Assert.Equal(beforeUnit.Name, afterUnit.Name);
        Assert.Equal(beforeUnit.ThumbnailRef, afterUnit.ThumbnailRef);
        Assert.Equal(beforeUnit.PageCount, afterUnit.PageCount);
        var beforeSource = Assert.Single(beforeUnit.Sources);
        var afterSource = Assert.Single(afterUnit.Sources);
        Assert.Equal(beforeSource.AssetRole, afterSource.AssetRole);
        Assert.Equal(beforeSource.Layout, afterSource.Layout);
        Assert.Equal(beforeSource.FilePath, afterSource.FilePath);
        Assert.Equal(beforeSource.Torrent.InfoHashV1, afterSource.Torrent.InfoHashV1);
        Assert.Equal(
            Assert.Single(beforeSource.Torrent.Trackers),
            Assert.Single(afterSource.Torrent.Trackers));
        Assert.Empty(after.Seasons);
        var beforeRight = Assert.Single(before.DistributionRights);
        var afterRight = Assert.Single(after.DistributionRights);
        Assert.Equal(beforeRight.SourceUri, afterRight.SourceUri);
        Assert.Equal(beforeRight.LicenseName, afterRight.LicenseName);
        Assert.Equal(
            "Sora Archive",
            Assert.Single(after.Item.AlternativeNames).Name);
    }

    [Fact]
    public async Task MetadataRenameConflict_IsAtomic()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("First movie"));
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("Second movie"));
        var second = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie),
            static item => item.Name == "Second movie");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.Repository.UpdateMetadataAsync(
                new CatalogMetadataUpdate(
                    second.Id,
                    "First movie",
                    "Changed description",
                    "https://image.tmdb.org/t/p/w500/conflict.jpg",
                    [new CatalogAlternativeName("Changed alias")])).AsTask());

        var unchanged = Assert.IsType<CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(second.Id));
        Assert.Equal("Second movie", unchanged.Item.Name);
        Assert.Equal("Description for Second movie.", unchanged.Item.Description);
        Assert.Null(unchanged.Item.ThumbnailRef);
        Assert.Empty(unchanged.Item.AlternativeNames);
    }

    [Fact]
    public async Task RichCatalog_RoundTripsThroughSqliteAndIdFreeJson()
    {
        await using var scope = TestDatabaseScope.Create();
        var transfer = new CatalogJsonTransferService(scope.Repository);

        var result = await transfer.ImportAsync(TestCatalogDocuments.RichCatalog(), "integration test");

        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);

        await using var json = new MemoryStream();
        await transfer.ExportAsync(json);
        json.Position = 0;

        using (var parsed = await JsonDocument.ParseAsync(json))
        {
            AssertHasNoDatabaseIds(parsed.RootElement);
            Assert.DoesNotContain(
                "credentialKey",
                parsed.RootElement.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "private-webtorrent",
                parsed.RootElement.GetRawText(),
                StringComparison.Ordinal);
        }

        json.Position = 0;
        var exported = await transfer.ReadAsync(json);
        var manga = Assert.Single(exported.Items, static item => item.Name == "Sky Archive");
        Assert.Equal("manga", manga.Subtype);
        Assert.Equal(2, manga.Genres.Count);
        Assert.Equal(["Comics", "Manga"], manga.Categories);
        Assert.Equal(2, manga.AlternativeNames.Count);
        Assert.Null(manga.Aliases);
        Assert.True(Assert.Single(Assert.Single(manga.Units).Sources).Torrent.IsPrivate);
        Assert.Equal(
            "ja",
            Assert.Single(Assert.Single(manga.Units).Sources).Torrent.Language);
        Assert.Equal(
            "https://example.test/open-ink",
            Assert.Single(manga.Contributors).Url?.TrimEnd('/'));

        var tracker = Assert.Single(Assert.Single(Assert.Single(manga.Units).Sources).Torrent.Trackers);
        Assert.Equal("wss", tracker.Transport);
        Assert.True(tracker.RequiresAuthentication);

        var series = Assert.Single(exported.Items, static item => item.Name == "Lantern Circuit");
        Assert.Equal("Boot Sequence", Assert.Single(Assert.Single(series.Seasons).Units).Name);
    }

    [Fact]
    public async Task LegacyAliases_ImportAndExportAsLocalizedAlternativeNames()
    {
        await using var scope = TestDatabaseScope.Create();
        var legacyItem = TestCatalogDocuments.SingleMovie("Legacy title").Items[0] with
        {
            Aliases =
            [
                new CatalogTransferAlias
                {
                    Name = "Legacy alternate",
                    AliasType = "other"
                }
            ]
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = [legacyItem]
        });

        var item = Assert.Single(await scope.Repository.ListAsync(MediaType.Movie));
        var alternativeName = Assert.Single(item.AlternativeNames);
        Assert.Equal("Legacy alternate", alternativeName.Name);
        Assert.Equal("und", alternativeName.Language);

        var exported = await scope.Repository.ExportCatalogAsync();
        var exportedItem = Assert.Single(exported.Items);
        Assert.Null(exportedItem.Aliases);
        Assert.Equal(
            "Legacy alternate",
            Assert.Single(exportedItem.AlternativeNames).Name);
    }

    [Fact]
    public async Task LegacyTorrentWithoutLanguage_ImportsAndExportsAsUndetermined()
    {
        await using var scope = TestDatabaseScope.Create();
        var transfer = new CatalogJsonTransferService(scope.Repository);
        var json = """
            {
              "format": "universal-media-distribution.catalog",
              "version": 1,
              "items": [
                {
                  "mediaType": "movie",
                  "subtype": "movie",
                  "name": "Legacy language",
                  "sources": [
                    {
                      "assetRole": "primary",
                      "sourceLayout": "single_file",
                      "fileIndex": 0,
                      "torrent": {
                        "infoHashV1": "0123456789ABCDEF0123456789ABCDEF01234567"
                      }
                    }
                  ]
                }
              ]
            }
            """;
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var document = await transfer.ReadAsync(source);
        Assert.Equal(
            "und",
            Assert.Single(Assert.Single(document.Items).Sources).Torrent.Language);

        await transfer.ImportAsync(document);
        var item = Assert.Single(await scope.Repository.ListAsync(MediaType.Movie));
        var details = Assert.IsType<CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(item.Id));
        Assert.Equal("und", Assert.Single(details.Sources).Torrent.Language);

        var exported = await scope.Repository.ExportCatalogAsync();
        Assert.Equal(
            "und",
            Assert.Single(Assert.Single(exported.Items).Sources).Torrent.Language);
    }

    [Fact]
    public async Task CurrentMigration_PreservesLegacyAliasesAndAddsPrivacyTables()
    {
        await using var scope = TestDatabaseScope.Create();
        var legacyItem = TestCatalogDocuments.SingleMovie("Migrated title").Items[0] with
        {
            AlternativeNames =
            [
                new CatalogTransferAlias
                {
                    Name = "Migrated alternate",
                    Language = "und",
                    AliasType = "other"
                }
            ]
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = [legacyItem]
        });
        await ExecuteNonQueryAsync(
            scope.DatabasePath,
            """
            UPDATE catalog_alternative_names SET language = '';
            DROP TABLE catalog_item_categories;
            DROP TABLE categories;
            ALTER TABLE catalog_alternative_names RENAME TO catalog_aliases;
            ALTER TABLE contributors DROP COLUMN url;
            ALTER TABLE torrent_packages DROP COLUMN language;
            DROP TABLE favorite_contributors;
            DROP TABLE user_media_list_items;
            DROP TABLE user_media_lists;
            UPDATE schema_info SET value = '2' WHERE key = 'schema_version';
            PRAGMA user_version = 2;
            """);

        var migratedRepository = new SqliteCatalogRepository(
            new SqliteCatalogDatabase(scope.DatabasePath));
        var item = Assert.Single(await migratedRepository.ListAsync(MediaType.Movie));

        var alternativeName = Assert.Single(item.AlternativeNames);
        Assert.Equal("Migrated alternate", alternativeName.Name);
        Assert.Equal("und", alternativeName.Language);
        Assert.Equal(15L, await ExecuteScalarAsync(scope.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'catalog_alternative_names';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'catalog_item_categories';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('tracker_endpoints')
                WHERE name = 'credential_key';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('contributors')
                WHERE name = 'url';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('torrent_packages')
                WHERE name = 'language';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'favorite_contributors';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'user_media_lists';
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarAsync(
                scope.DatabasePath,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'user_media_list_items';
                """));
    }

    [Fact]
    public async Task CatalogUpdate_ReplacesCategoriesGenresAndAlternativeNames()
    {
        await using var scope = TestDatabaseScope.Create();
        var initialItem = TestCatalogDocuments.SingleMovie("Replace taxonomy").Items[0] with
        {
            Genres = ["Drama", "Fantasy"],
            Categories = ["Feature Film", "Independent"],
            AlternativeNames =
            [
                new CatalogTransferAlias
                {
                    Name = "First alternate",
                    Language = "en",
                    AliasType = "short"
                },
                new CatalogTransferAlias
                {
                    Name = "Segundo título",
                    Language = "es",
                    AliasType = "translation"
                }
            ]
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = [initialItem]
        });

        var updatedItem = initialItem with
        {
            Genres = ["Documentary"],
            Categories = ["Short Film"],
            AlternativeNames =
            [
                new CatalogTransferAlias
                {
                    Name = "Replacement title",
                    Language = "en",
                    AliasType = "other"
                }
            ]
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = [updatedItem]
        });

        var item = Assert.Single(await scope.Repository.ListAsync(MediaType.Movie));
        Assert.Equal(["Documentary"], item.Genres);
        Assert.Equal(["Short Film"], item.Categories);
        Assert.Equal(
            ["Replacement title"],
            item.AlternativeNames.Select(static alternativeName => alternativeName.Name));
    }

    [Fact]
    public async Task Import_WithExistingAlternativeTitle_ConvergesIdAndUpdatesThumbnail()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "anime_movie",
                    Name = "Spirited Away",
                    ThumbnailRef = "https://images.example/old.jpg",
                    AlternativeNames =
                    [
                        new CatalogTransferAlias
                        {
                            Name = "Sen to Chihiro no Kamikakushi",
                            AliasType = "original"
                        }
                    ]
                }
            ]
        });
        var original = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));

        var result = await scope.Repository.MergeCatalogAsync(
            new CatalogTransferDocument
            {
                Items =
                [
                    new CatalogTransferItem
                    {
                        MediaType = "movie",
                        Subtype = "anime_movie",
                        Name = "Sen to Chihiro no Kamikakushi",
                        ThumbnailRef = "https://images.example/new.jpg",
                        AlternativeNames =
                        [
                            new CatalogTransferAlias
                            {
                                Name = "El viaje de Chihiro",
                                AliasType = "localized"
                            }
                        ]
                    }
                ]
            });

        var updated = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("Spirited Away", updated.Name);
        Assert.Equal("https://images.example/new.jpg", updated.ThumbnailRef);
        Assert.Contains(
            updated.AlternativeNames,
            static alias => alias.Name == "Sen to Chihiro no Kamikakushi");
        Assert.Contains(
            updated.AlternativeNames,
            static alias => alias.Name == "El viaje de Chihiro");
    }
    [Fact]
    public async Task Import_WithEquivalentName_UpdatesExistingItemAndPreservesUserData()
    {
        await using var scope = TestDatabaseScope.Create();
        var original = new CatalogTransferDocument
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = "Sky Archive",
                    Description = "Original description"
                }
            ]
        };
        await scope.Repository.MergeCatalogAsync(original);
        var originalItem = Assert.Single(await scope.Repository.ListAsync());

        await ExecuteNonQueryAsync(
            scope.DatabasePath,
            """
            INSERT INTO library_entries (catalog_item_id, is_favorite, added_at)
            VALUES ($item_id, 1, $added_at);
            """,
            ("$item_id", originalItem.Id.ToString("D")),
            ("$added_at", DateTimeOffset.UtcNow.ToString("O")));

        var update = new CatalogTransferDocument
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = "  sky   archive  ",
                    Description = "Updated description"
                }
            ]
        };

        var result = await scope.Repository.MergeCatalogAsync(update);
        var updatedItem = Assert.Single(await scope.Repository.ListAsync());

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(originalItem.Id, updatedItem.Id);
        Assert.Equal("Updated description", updatedItem.Description);
        Assert.Equal(1L, await ExecuteScalarAsync(
            scope.DatabasePath,
            "SELECT COUNT(*) FROM library_entries WHERE catalog_item_id = $item_id;",
            ("$item_id", originalItem.Id.ToString("D"))));
    }

    [Fact]
    public async Task Import_IsAtomicWhenAStoredItemFails()
    {
        await using var scope = TestDatabaseScope.Create();
        _ = await scope.Repository.ListAsync();

        await ExecuteNonQueryAsync(
            scope.DatabasePath,
            """
            CREATE TRIGGER reject_second_test_item
            BEFORE INSERT ON catalog_items
            WHEN NEW.name = 'Rejected Movie'
            BEGIN
                SELECT RAISE(ABORT, 'intentional test rejection');
            END;
            """);

        var document = new CatalogTransferDocument
        {
            Items =
            [
                TestCatalogDocuments.SingleMovie("Accepted Movie").Items[0],
                TestCatalogDocuments.SingleMovie("Rejected Movie").Items[0]
            ]
        };

        await Assert.ThrowsAsync<SqliteException>(
            () => scope.Repository.MergeCatalogAsync(document).AsTask());

        Assert.Empty(await scope.Repository.ListAsync());
        Assert.Equal(0L, await ExecuteScalarAsync(
            scope.DatabasePath,
            "SELECT COUNT(*) FROM catalog_import_history;"));
    }

    [Fact]
    public async Task BackupRestore_RestoresFullDatabaseAndKeepsSafetyCopy()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.SingleMovie("Before Backup"));
        var beforeBackupItem = Assert.Single(await scope.Repository.ListAsync());
        await ExecuteNonQueryAsync(
            scope.DatabasePath,
            """
            INSERT INTO library_entries (catalog_item_id, is_favorite, added_at)
            VALUES ($item_id, 1, $added_at);
            """,
            ("$item_id", beforeBackupItem.Id.ToString("D")),
            ("$added_at", DateTimeOffset.UtcNow.ToString("O")));
        var userState = new SqliteUserMediaStateStore(scope.Database);
        var watchLater = await userState.CreateUserListAsync("Watch later");
        await userState.SetUserListMembershipAsync(
            watchLater.Id,
            beforeBackupItem.Id,
            isMember: true);

        await using var backup = new MemoryStream();
        await scope.BackupService.ExportAsync(backup);

        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.SingleMovie("After Backup"));
        await userState.DeleteUserListAsync(watchLater.Id);
        Assert.Equal(2, (await scope.Repository.ListAsync()).Count);

        backup.Position = 0;
        var restore = await scope.BackupService.RestoreAsync(backup);
        var restoredItems = await scope.Repository.ListAsync();

        var restoredItem = Assert.Single(restoredItems);
        Assert.Equal("Before Backup", restoredItem.Name);
        Assert.Equal(beforeBackupItem.Id, restoredItem.Id);
        Assert.Equal(1L, await ExecuteScalarAsync(
            scope.DatabasePath,
            "SELECT COUNT(*) FROM library_entries WHERE catalog_item_id = $item_id AND is_favorite = 1;",
            ("$item_id", beforeBackupItem.Id.ToString("D"))));
        var restoredList = Assert.Single(
            await new SqliteUserMediaStateStore(scope.Database)
                .LoadUserListsAsync());
        Assert.Equal("Watch later", restoredList.Name);
        Assert.True(restoredList.Contains(beforeBackupItem.Id));
        Assert.True(File.Exists(restore.SafetyBackupPath));
    }

    [Fact]
    public async Task BackupRestore_RejectsBrokenRelationshipsBeforeReplacingCurrentData()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.RichCatalog());
        var originalItems = await scope.Repository.ListAsync();

        var invalidBackupPath = Path.Combine(scope.DirectoryPath, "invalid.umdbackup");
        await scope.BackupService.CreateBackupAsync(invalidBackupPath);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = invalidBackupPath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = false,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM catalog_items WHERE name = 'Sky Archive';";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => scope.BackupService.RestoreBackupAsync(invalidBackupPath).AsTask());

        var unchangedItems = await scope.Repository.ListAsync();
        Assert.Equal(
            originalItems.Select(static item => item.Id).Order(),
            unchangedItems.Select(static item => item.Id).Order());
    }

    [Fact]
    public async Task JsonImport_RejectsDatabaseIdsAndNullRequiredValues()
    {
        await using var scope = TestDatabaseScope.Create();
        var transfer = new CatalogJsonTransferService(scope.Repository);
        var json = """
            {
              "format": "universal-media-distribution.catalog",
              "version": 1,
              "items": [
                {
                  "id": "external-id",
                  "mediaType": null,
                  "subtype": "movie",
                  "name": "Unknown"
                }
              ]
            }
            """;
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var idException = await Assert.ThrowsAsync<CatalogTransferValidationException>(
            () => transfer.ReadAsync(source).AsTask());

        Assert.Contains(
            idException.Errors,
            static error => error.Contains("Invalid JSON", StringComparison.Ordinal));

        var nullJson = """
            {
              "format": "universal-media-distribution.catalog",
              "version": 1,
              "items": [
                {
                  "mediaType": null,
                  "subtype": "movie",
                  "name": "Unknown"
                }
              ]
            }
            """;
        await using var nullSource = new MemoryStream(Encoding.UTF8.GetBytes(nullJson));
        var nullException = await Assert.ThrowsAsync<CatalogTransferValidationException>(
            () => transfer.ReadAsync(nullSource).AsTask());
        Assert.Contains(
            nullException.Errors,
            static error => error.Contains("mediaType is required", StringComparison.Ordinal));
        Assert.Empty(await scope.Repository.ListAsync());
    }

    [Fact]
    public async Task JsonPreview_SeparatesCreatesFromNameBasedUpdates()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(TestCatalogDocuments.SingleMovie("Existing Movie"));
        var transfer = new CatalogJsonTransferService(scope.Repository);
        var incoming = new CatalogTransferDocument
        {
            Items =
            [
                TestCatalogDocuments.SingleMovie(" existing   movie ").Items[0],
                TestCatalogDocuments.SingleMovie("New Movie").Items[0]
            ]
        };

        var preview = await transfer.PreviewAsync(incoming);

        Assert.Equal(1, preview.CreatedCount);
        Assert.Equal(1, preview.UpdatedCount);
        Assert.Contains("New Movie", preview.Creates);
        Assert.Contains(" existing   movie ", preview.Updates);
    }

    private static void AssertHasNoDatabaseIds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.NotEqual("id", property.Name);
                AssertHasNoDatabaseIds(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                AssertHasNoDatabaseIds(value);
            }
        }
    }

    private static async Task ExecuteNonQueryAsync(
        string databasePath,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(
        string databasePath,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static SqliteConnection CreateConnection(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
}
