using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class OnlineCatalogMetadataTests
{
    [Fact]
    public void DetailPageHidesHistoricCatalogCreditAndKeepsRealContributorsWithoutMutatingData()
    {
        var details = CatalogDetailsMapper.ToDetails(new CatalogTransferItem
        {
            Name = "A manga",
            MediaType = "book",
            Subtype = "manga",
            Contributors =
            [
                new() { Name = "UMD catalog API", Kind = "organization", Credits = [new() { Role = "Distributor" }] },
                new() { Name = "Catálogo online", Kind = "organization", Url = "https://server.example/api/v1/catalog/", Credits = [new() { Role = "Distributor" }] },
                new() { Name = "LeerCapitulo", Kind = "organization", Url = "https://www.leercapitulo.co/", Credits = [new() { Role = "Distributor" }] },
                new() { Name = "Writer", Kind = "person", Credits = [new() { Role = "Author" }] }
            ]
        }, Guid.CreateVersion7());
        var viewModel = new CatalogDetailsViewModel(details, new TorrentMediaSessionService(engine: null),
            openAsync: static _ => Task.CompletedTask);
        Assert.Equal(["LeerCapitulo", "Writer"], viewModel.Contributors.Select(contributor => contributor.Name));
        Assert.Equal(4, details.Item.Contributors.Count);
        var card = new CatalogCard(details.Item.Id, MediaType.Book, "book.manga", "A manga", string.Empty,
            null, [], [], [], details.Item.Contributors);
        using var cardViewModel = new CatalogCardViewModel(card, new TorrentMediaSessionService(engine: null),
            trustedRemoteOrigins: null, openAsync: static _ => Task.CompletedTask, isFavorite: false, progress: null,
            toggleFavoriteAsync: static (_, _) => Task.CompletedTask, continueAsync: static _ => Task.CompletedTask);
        Assert.Contains("LeerCapitulo", cardViewModel.ProviderText);
        Assert.DoesNotContain("UMD", cardViewModel.ProviderText);
        Assert.DoesNotContain("Catálogo online", cardViewModel.ProviderText);
    }

    [Fact]
    public void OrdinaryAuthorsAndPublishersAreNotTreatedAsSyntheticCatalogCredits()
    {
        Assert.False(CatalogMetadataSource.IsSyntheticDistributor(new CatalogContributor("UMD catalog API", credits: [new("Author")])));
        Assert.False(CatalogMetadataSource.IsSyntheticDistributor(new CatalogContributor("Publisher", "organization",
            "https://publisher.example/", credits: [new("Publisher")])));
        Assert.False(CatalogMetadataSource.IsSyntheticDistributor(new CatalogContributor("Catalog researcher", "organization",
            "https://publisher.example/api/v1/catalog/", credits: [new("Researcher")])));
        Assert.True(BuiltInApiContentProviderDescriptors.LeerCapitulo.ProvidesUnitContent);
        Assert.False(BuiltInApiContentProviderDescriptors.UmdJson.ProvidesUnitContent);
    }
}
