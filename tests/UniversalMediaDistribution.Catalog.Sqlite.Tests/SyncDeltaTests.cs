using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Stage 3's engine: the record-level difference between two documents, and
/// the smallest partial document that carries chosen records and still passes
/// validation on arrival.
/// </summary>
public sealed class SyncDeltaTests
{
    private const string Title = "Guia de supervivencia del extra";
    private static readonly string Device = new('d', 32);

    [Fact]
    public void IdenticalDocumentsHaveNoDifference()
    {
        var document = CreateDocument();

        Assert.Empty(
            UserSyncDocumentDelta.ComputeChangedKeys(document, document));
    }

    [Fact]
    public void ANewPositionIsExactlyOneChangedRecord()
    {
        var before = CreateDocument();
        var after = before with
        {
            Progress = [CreateProgress("2")]
        };

        var changed = UserSyncDocumentDelta.ComputeChangedKeys(before, after);

        var key = Assert.Single(changed);
        Assert.StartsWith("progress:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditedItemIsExactlyOneChangedRecord()
    {
        var before = CreateDocument();
        var after = before with
        {
            Catalog = new CatalogTransferDocument
            {
                Items =
                [
                    before.Catalog.Items[0] with
                    {
                        Description = "Now it has a synopsis."
                    }
                ]
            }
        };

        var changed = UserSyncDocumentDelta.ComputeChangedKeys(before, after);

        var key = Assert.Single(changed);
        Assert.StartsWith("item:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialCarriesTheTitleItsRecordsPointAt()
    {
        var document = CreateDocument() with
        {
            Progress = [CreateProgress("1")]
        };
        var keys = document.Progress
            .Select(static entry =>
                "progress:" +
                UserSyncDocumentValidator.ValidateContentIdentity(
                    entry.Content))
            .ToHashSet(StringComparer.Ordinal);

        var partial = UserSyncDocumentDelta.Extract(document, keys);

        Assert.Single(partial.Progress);
        Assert.Equal(Title, Assert.Single(partial.Catalog.Items).Name);

        // The whole point: a partial must stand alone.
        UserSyncDocumentValidator.Validate(partial);
    }

    private static UserSyncDocument CreateDocument() =>
        new()
        {
            DeviceId = Device,
            DeviceName = "Delta device",
            ExportedAt = DateTimeOffset.UnixEpoch.AddDays(1),
            Catalog = new CatalogTransferDocument
            {
                Items =
                [
                    new CatalogTransferItem
                    {
                        Id = CatalogStableId.Create(
                            Domain.Catalog.MediaType.Book,
                            Title),
                        MediaType = "book",
                        Subtype = "manga",
                        Name = Title,
                        Units =
                        [
                            new CatalogTransferUnit
                            {
                                Kind = "chapter",
                                DisplayNumber = "1",
                                SortOrder = 0
                            },
                            new CatalogTransferUnit
                            {
                                Kind = "chapter",
                                DisplayNumber = "2",
                                SortOrder = 1
                            }
                        ]
                    }
                ]
            }
        };

    private static UserSyncProgressEntry CreateProgress(
        string displayNumber) =>
        new()
        {
            Content = new UserSyncContentIdentity
            {
                Item = new UserSyncCatalogItemIdentity
                {
                    MediaType = "book",
                    Name = Title
                },
                UnitKind = "chapter",
                DisplayNumber = displayNumber
            },
            Position = 4,
            PositionKind = "page",
            UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1),
            SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                Device,
                DateTimeOffset.UnixEpoch.AddDays(1),
                "progress:" + displayNumber)
        };
}
