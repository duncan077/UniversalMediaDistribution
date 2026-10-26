using System.Net;
using System.Net.Http.Json;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class BookOnlineSearchTests
{
    [Fact]
    public async Task OpeningEachSameNamedSearchResultLoadsTheSelectedFormat()
    {
        await using var scope = TestDatabaseScope.Create();
        var document = new CatalogTransferDocument
        {
            Items = new[] { "novel", "manga" }.Select((subtype, index) => new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = subtype,
                Name = "Same title",
                Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = (index + 1).ToString(), SortOrder = 0 }]
            }).ToArray()
        };
        using var httpClient = new HttpClient(new Handler(document));
        var settings = new SqliteContentSourceProviderSettingsStore(scope.Database);
        await settings.SaveAsync(new ContentSourceProviderSettings
        {
            Providers = [new ContentSourceProviderConfiguration
            {
                Id = "test-books", DisplayName = "Books", Kind = ContentSourceProviderKind.TorrentCatalog,
                IsEnabled = true, BaseUrl = "https://catalog.example/books.json"
            }]
        });
        var service = new ContentSourceCatalogService(new CatalogBrowseService(scope.Repository),
            new CatalogJsonTransferService(scope.Repository), settings, httpClient, Array.Empty<IApiContentProvider>());
        var search = await service.SearchAsync("Same title", MediaType.Book);
        Assert.Empty(search.Failures);
        Assert.Equal(2, search.Results.Count);
        Assert.Equal(2, search.Results.Select(result => result.ExternalId).Distinct().Count());
        foreach (var result in search.Results.Reverse())
        {
            var loaded = await service.LoadOnlineItemAsync(result);
            Assert.Equal(result.Card.SubtypeCode, loaded.Details.Item.SubtypeCode);
            Assert.Equal(result.Card.SubtypeCode == "book.novel" ? "1" : "2",
                Assert.Single(loaded.Details.Units).Identity.DisplayNumber);
        }
        Assert.Equal(2, (await scope.Repository.ListAsync()).Count);
    }

    private sealed class Handler(CatalogTransferDocument document) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(document)
            });
    }
}
