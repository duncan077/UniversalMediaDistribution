using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.UI.Tests;

internal static class TestSyncCatalog
{
    public static CatalogTransferDocument Document() => new()
    {
        SourceLabel = "Sync test catalog",
        Items =
        [
            new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = "Sky Archive",
                Description = "A freely distributed test manga.",
                Genres = ["Adventure"],
                Categories = ["Manga"],
                Contributors =
                [
                    new CatalogTransferContributor
                    {
                        Name = "Open Ink Collective",
                        Kind = "organization"
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
                                Torrent = new CatalogTransferTorrent
                                {
                                    DisplayName = "Sky Archive chapter 1",
                                    Language = "ja",
                                    InfoHashV1 =
                                        "0123456789ABCDEF0123456789ABCDEF01234567",
                                    MagnetUri =
                                        "magnet:?xt=urn:btih:0123456789ABCDEF" +
                                        "0123456789ABCDEF01234567&dn=sky-archive",
                                    TotalSize = 4096
                                }
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
                        LicenseUri =
                            "https://creativecommons.org/licenses/by/4.0/",
                        Attribution = "Open Ink Collective",
                        VerifiedAt = "2026-07-20T10:00:00Z"
                    }
                ]
            }
        ]
    };
}
