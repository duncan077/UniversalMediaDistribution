using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Metadata.Tests;

public sealed class CatalogMetadataRefreshServiceTests
{
    [Fact]
    public async Task Refresh_MergesAliasesAndPreservesCatalogIdentityAndContent()
    {
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = "Local title",
                    Description = "Local description",
                    ThumbnailRef = "local.webp",
                    AlternativeNames =
                    [
                        new CatalogTransferAlias
                        {
                            Name = "Existing alias",
                            Language = "en",
                            AliasType = "alternative"
                        }
                    ],
                    Units =
                    [
                        new CatalogTransferUnit
                        {
                            Kind = "chapter",
                            DisplayNumber = "1",
                            Name = "Chapter one"
                        }
                    ]
                }
            ]
        });
        var before = Assert.IsType<CatalogItemDetails>(
            await repository.GetDetailsAsync(
                Assert.Single(await repository.ListAsync()).Id));
        var provider = new StubMetadataProvider(new CatalogMetadataSnapshot(
            "Test provider",
            "Provider title",
            "Provider description",
            "https://cdn.myanimelist.net/provider.webp",
            [new CatalogAlternativeName("Provider alias", "ja", "original")]));
        var service = new CatalogMetadataRefreshService(
            repository,
            new InMemoryMetadataProviderSettingsStore(),
            [provider]);

        var result = await service.RefreshAsync(before.Item.Id);

        Assert.Equal(before.Item.Id, result.Details.Item.Id);
        Assert.Equal(before.Identity.StableKey, result.Details.Identity.StableKey);
        Assert.Equal("Provider title", result.Details.Item.Name);
        Assert.Equal("Provider description", result.Details.Item.Description);
        Assert.Equal(
            "https://cdn.myanimelist.net/provider.webp",
            result.Details.Item.ThumbnailRef);
        Assert.Equal("Chapter one", Assert.Single(result.Details.Units).Name);
        Assert.Contains(
            result.Details.Item.AlternativeNames,
            static name => name.Name == "Local title" && name.Kind == "previous");
        Assert.Contains(
            result.Details.Item.AlternativeNames,
            static name => name.Name == "Existing alias");
        Assert.Contains(
            result.Details.Item.AlternativeNames,
            static name => name.Name == "Provider alias");
    }

    private sealed class StubMetadataProvider(CatalogMetadataSnapshot snapshot) :
        ICatalogMetadataProvider
    {
        public string Name => snapshot.ProviderName;

        public bool CanRefresh(CatalogItemDetails details) =>
            details.Item.SubtypeCode == "book.manga";

        public ValueTask<CatalogMetadataSnapshot> FetchAsync(
            CatalogItemDetails details,
            MetadataProviderSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshot);
        }
    }
}
