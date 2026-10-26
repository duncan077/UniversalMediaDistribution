using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Two devices hold their own view of one title: this one caught up from
/// episode 4, the account's snapshot has all six. Merging has to end with six.
/// </summary>
/// <remarks>
/// Choosing one view over the other loses more than episodes. A position
/// recorded against a dropped episode then points outside the catalog, and the
/// restore is refused whole — the account cannot sync at all until something
/// puts the episode back, which nothing does.
/// </remarks>
public sealed class SyncUnitUnionTests
{
    private const string Title =
        "\"Kimi wo Aisuru Ki wa Nai\" to Itta Jiki Koushaku-sama";

    [Fact]
    public void MergingKeepsEpisodesFromBothSides()
    {
        // Each side holds what the other lacks, so choosing between them can
        // only ever produce three of the six however the choice is made.
        var merged = CatalogTransferDocumentMerger.Merge(
            CreateCatalog("4", "5", "6"),
            CreateCatalog("1", "2", "3"));

        Assert.Equal(
            ["1", "2", "3", "4", "5", "6"],
            Assert.Single(merged.Items).Units
                .Select(static unit => unit.DisplayNumber)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RestoringKeepsEpisodesFromBothSides()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            CreateCatalog("4", "5", "6"));
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        await store.RestoreAsync(
            CreateDocument(["1", "2", "3"], progressOn: null));

        var details = await scope.Repository.GetDetailsAsync(
            (await scope.Repository.ListAsync(MediaType.Movie))[0].Id);
        Assert.NotNull(details);
        Assert.Equal(
            ["1", "2", "3", "4", "5", "6"],
            details.Units
                .Select(static unit => unit.DisplayNumber)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task APositionOnAnEpisodeThisDeviceLacksStillRestores()
    {
        // The reported failure, as it arrived: the snapshot's position named
        // episode 1, this device had only 4 to 6, and the merge dropped what
        // it did not choose — so the position pointed outside the catalog and
        // the whole restore was refused.
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            CreateCatalog("4", "5", "6"));
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        var result = await store.RestoreAsync(
            CreateDocument(["1", "2", "3"], progressOn: "1"));

        Assert.Equal(1, result.ProgressCount);
    }

    private static CatalogTransferDocument CreateCatalog(
        params string[] episodes) =>
        new()
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "series",
                    Name = Title,
                    Units = episodes
                        .Select(static (number, index) =>
                            new CatalogTransferUnit
                            {
                                Kind = "episode",
                                DisplayNumber = number,
                                SortOrder = index
                            })
                        .ToArray()
                }
            ]
        };

    private static UserSyncDocument CreateDocument(
        string[] episodes,
        string? progressOn) =>
        new()
        {
            DeviceId = new string('d', 32),
            DeviceName = "Test device",
            ExportedAt = DateTimeOffset.UtcNow,
            Catalog = CreateCatalog(episodes),
            Progress = progressOn is null
                ?
                []
                :
                [
                    new UserSyncProgressEntry
                    {
                        Content = new UserSyncContentIdentity
                        {
                            Item = new UserSyncCatalogItemIdentity
                            {
                                MediaType = "movie",
                                Name = Title
                            },
                            UnitKind = "episode",
                            DisplayNumber = progressOn
                        },
                        Position = 120,
                        PositionKind = "seconds",
                        UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1),
                        SyncVersion =
                            UserSyncDocumentMerger.CreateLegacyVersion(
                                new string('d', 32),
                                DateTimeOffset.UnixEpoch.AddDays(1),
                                "progress")
                    }
                ]
        };
}
