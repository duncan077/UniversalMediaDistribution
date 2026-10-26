using System.Net;
using System.Text;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ZonaTmoBookIsolationTests
{
    private const string Title = "¿No es mucho mejor ser una mujer malvada?";
    private const string MangaId = "manhwa/23158/no-es-mucho-mejor-ser-una-mujer-malvada-1";
    private const string NovelId = "novel/191/no-es-mucho-mejor-ser-una-mujer-malvada";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SameNamedNovelNeverSuppliesMangaChapters(bool rememberedNovel, bool refreshTitle)
    {
        var local = new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = "manga",
            Name = Title,
            Units = Enumerable.Range(1, 105).Select(number => new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = number.ToString(),
                SortOrder = number - 1
            }).ToArray()
        };
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument { Items = [local] });
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single((await browse.GetOverviewAsync(MediaType.Book)).Items);
        var details = Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(card.Id));
        var bindings = new InMemoryApiTitleBindingStore();
        using var http = new HttpClient(new Handler());
        var provider = new ZonaTmoApiContentProvider(http);
        if (rememberedNovel)
            await bindings.SaveAsync(new ApiTitleBinding(card.Id, provider.Descriptor.Id, NovelId, Title));
        var settings = new InMemoryContentSourceProviderSettingsStore(new ContentSourceProviderSettings
        {
            Providers = [new ContentSourceProviderConfiguration
            {
                Id = provider.Descriptor.Id, DisplayName = provider.Descriptor.DisplayName,
                Kind = ContentSourceProviderKind.Api, IsEnabled = true, BaseUrl = "https://zonatmo.example/"
            }]
        });
        var service = new ContentSourceCatalogService(browse, new CatalogJsonTransferService(repository),
            settings, http, [provider], titleBindingStore: bindings);

        if (refreshTitle)
        {
            var refreshed = await service.RefreshApiItemAsync(details);
            Assert.NotNull(refreshed);
            Assert.Equal(105, refreshed.Details.Units.Count);
            Assert.Equal("book.manga", refreshed.Details.Item.SubtypeCode);
            Assert.Equal(MangaId, (await bindings.LoadAsync(card.Id))[provider.Descriptor.Id].ExternalId);
        }
        else
        {
            var candidates = await service.FindApiImageCandidatesForUnitAsync(details, details.Units[0]);
            var candidate = Assert.Single(candidates);
            Assert.Equal(MangaId, candidate.SearchItem.ExternalId);
            Assert.Equal("manga", candidate.ApiItem.CatalogItem.Subtype);
            Assert.Equal(97, candidate.ApiItem.CatalogItem.Units.Count);
            var chapter102 = Assert.Single(details.Units, unit => unit.DisplayNumber == "102");
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.FindApiImageCandidatesForUnitAsync(details, chapter102));
        }

        var saved = Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(card.Id));
        Assert.Equal(105, saved.Units.Count);
        Assert.DoesNotContain(saved.Units, unit => unit.DisplayNumber == "185");
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var content = path == "/biblioteca"
                ? Card(NovelId, "NOVEL") + Card(MangaId, "MANHWA")
                : path == "/library/" + NovelId ? Detail("NOVEL", 185)
                : path == "/library/" + MangaId ? Detail("MANHWA", 97) : string.Empty;
            return Task.FromResult(new HttpResponseMessage(content.Length == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content, Encoding.UTF8, "text/html")
            });
        }

        private static string Card(string id, string type) => $"""
            <div class="element col-6">
              <a href="/library/{id}">
                <h4 class="text-truncate" title="{Title}">{Title}</h4>
                <span class="book-type">{type}</span>
              </a>
            </div>
            """;

        private static string Detail(string type, int chapterCount) => $"""
            <h1 class="element-title">{Title}</h1>
            <h1 class="book-type">{type}</h1>
            <ul>{string.Concat(Enumerable.Range(1, chapterCount).Select(number => $"""
              <li class="list-group-item upload-link" data-chapter-number="{number}">
                <span class="chapter-number" data-number="{number}">Capítulo {number}</span>
                <a class="btn" href="/view_uploads/{number}">Leer online</a>
              </li>
              """))}</ul>
            """;
    }
}
