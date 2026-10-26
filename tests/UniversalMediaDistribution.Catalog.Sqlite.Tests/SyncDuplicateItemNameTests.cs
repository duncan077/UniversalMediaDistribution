using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// A snapshot is merged from several devices that each keyed the same title by
/// their own id, so two spellings reaching one normalized name is ordinary.
/// Refusing it strands the account: every sync fails and nothing can repair it
/// from the client.
/// </summary>
public sealed class SyncDuplicateItemNameTests
{
    [Fact]
    public void ASnapshotWithTwoSpellingsOfOneTitleIsAccepted()
    {
        var document = CreateDocument(
            "Guia de supervivencia del extra de la academia",
            "Guia  de  supervivencia  del  extra  de  la  academia");

        UserSyncDocumentValidator.Validate(document);
    }

    [Fact]
    public void AHandWrittenCatalogStillRejectsTheSameTitleTwice()
    {
        // The rule earns its keep on an import: a document listing one title
        // twice is a mistake the author wants told about.
        var catalog = CreateDocument("Duplicated", "duplicated").Catalog;

        var exception = Assert.Throws<CatalogTransferValidationException>(
            () => CatalogTransferValidator.Validate(catalog));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("duplicates catalog item"));
    }

    [Fact]
    public void TheFailingRuleReachesTheMessage()
    {
        var catalog = CreateDocument("Duplicated", "duplicated").Catalog;

        var exception = Assert.Throws<CatalogTransferValidationException>(
            () => CatalogTransferValidator.Validate(catalog));

        Assert.Contains("duplicates catalog item", exception.Message);
    }

    [Fact]
    public void MergingASnapshotCollapsesTheTwoSpellingsIntoOneItem()
    {
        // The whole restore path in miniature: what the server holds is merged
        // with what this device holds, and the result is what gets imported.
        var remote = CreateDocument(
            "Guia de supervivencia del extra de la academia",
            "Guia  de  supervivencia  del  extra  de  la  academia").Catalog;
        var local = CreateDocument("Something else").Catalog;

        var merged = CatalogTransferDocumentMerger.Merge(local, remote);

        Assert.Equal(2, merged.Items.Count);

        // Strict by design: the merge is only correct if its output would
        // survive the rule its inputs were excused from.
        CatalogTransferValidator.Validate(merged);
    }

    private static UserSyncDocument CreateDocument(params string[] names) =>
        new()
        {
            DeviceId = new string('d', 32),
            DeviceName = "Test device",
            ExportedAt = DateTimeOffset.UtcNow,
            Catalog = new CatalogTransferDocument
            {
                Items = names
                    .Select(static name => new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = name
                    })
                    .ToArray()
            }
        };
}
