using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

internal static class TestCatalogDocuments
{
    public static CatalogTransferDocument RichCatalog() => new()
    {
        SourceLabel = "Test catalog",
        Items =
        [
            new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = "Sky Archive",
                OriginalName = "Sora Archive",
                SortName = "Archive, Sky",
                Description = "A freely distributed test manga.",
                ReleaseDate = "2026-01-10",
                PublicationStatus = "ongoing",
                OriginalLanguage = "ja",
                CountryCode = "JP",
                ContentRating = "Teen",
                PageCount = 32,
                ThumbnailRef = "art/sky-archive-cover.webp",
                BackdropRef = "art/sky-archive-backdrop.webp",
                Genres = ["Adventure", "Science Fiction"],
                Categories = ["Comics", "Manga"],
                AlternativeNames =
                [
                    new CatalogTransferAlias
                    {
                        Name = "The Sky Archive",
                        Language = "en",
                        AliasType = "translated"
                    },
                    new CatalogTransferAlias
                    {
                        Name = "Archivio del Cielo",
                        Language = "it",
                        AliasType = "translation"
                    }
                ],
                Contributors =
                [
                    new CatalogTransferContributor
                    {
                        Name = "Open Ink Collective",
                        Kind = "organization",
                        Url = "https://example.test/open-ink",
                        ThumbnailRef = "people/open-ink.webp",
                        Credits =
                        [
                            new CatalogTransferCredit
                            {
                                Role = "Writer",
                                SortOrder = 0
                            }
                        ]
                    }
                ],
                Units =
                [
                    new CatalogTransferUnit
                    {
                        Kind = "chapter",
                        DisplayNumber = "1",
                        SortOrder = 0,
                        Name = "First Light",
                        ThumbnailRef = "art/sky-archive-chapter-1.webp",
                        PageCount = 2,
                        Sources =
                        [
                            new CatalogTransferMediaSource
                            {
                                AssetRole = "primary",
                                SourceLayout = "image_sequence",
                                FilePath = "chapter-1/page_0.jpg",
                                MimeType = "image/jpeg",
                                SizeBytes = 4096,
                                Torrent = PrivateWebTorrent("Sky Archive chapter 1")
                            }
                        ]
                    }
                ],
                DistributionRights =
                [
                    new CatalogTransferRight
                    {
                        SourceName = "Open Ink Collective",
                        SourceUri = "https://example.test/sky-archive",
                        LicenseName = "Creative Commons Attribution 4.0",
                        LicenseUri = "https://creativecommons.org/licenses/by/4.0/",
                        Attribution = "Open Ink Collective",
                        VerifiedAt = "2026-07-20T10:00:00Z"
                    }
                ]
            },
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_series",
                Name = "Lantern Circuit",
                Description = "A test series with a season and episode.",
                PublicationStatus = "completed",
                ThumbnailRef = "art/lantern-circuit.webp",
                Genres = ["Animation"],
                Categories = ["Series", "Anime"],
                Seasons =
                [
                    new CatalogTransferSeason
                    {
                        Number = 1,
                        SortOrder = 0,
                        DisplayName = "Season 1",
                        ThumbnailRef = "art/lantern-circuit-season-1.webp",
                        Units =
                        [
                            new CatalogTransferUnit
                            {
                                Kind = "episode",
                                DisplayNumber = "1",
                                SortOrder = 0,
                                Name = "Boot Sequence",
                                DurationSeconds = 1440,
                                ThumbnailRef = "art/lantern-circuit-episode-1.webp"
                            }
                        ]
                    }
                ]
            }
        ]
    };

    public static CatalogTransferDocument SingleMovie(string name) => new()
    {
        Items =
        [
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "movie",
                Name = name,
                Description = $"Description for {name}."
            }
        ]
    };

    private static CatalogTransferTorrent PrivateWebTorrent(string displayName) => new()
    {
        DisplayName = displayName,
        Language = "ja",
        InfoHashV1 = "0123456789ABCDEF0123456789ABCDEF01234567",
        MagnetUri = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=sky-archive",
        IsPrivate = true,
        TotalSize = 4096,
        SourceUri = "https://example.test/sky-archive/chapter-1.torrent",
        Trackers =
        [
            new CatalogTransferTracker
            {
                Name = "Private WebTorrent tracker",
                Endpoint = "wss://tracker.example.test/announce",
                Transport = "wss",
                ClientFamily = "browser",
                RequiresAuthentication = true,
                CredentialKey = "private-webtorrent",
                Tier = 0,
                SortOrder = 0
            }
        ]
    };
}
