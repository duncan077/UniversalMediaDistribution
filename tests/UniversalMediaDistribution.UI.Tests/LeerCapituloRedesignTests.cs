using System.Net;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class LeerCapituloRedesignTests
{
    private static readonly Uri Root = new("https://reader.example/");
    private static ApiContentSearchItem Candidate => new("id1/hidden-logistics", new CatalogTransferItem
    { MediaType = "book", Subtype = "manga", Name = "Old title" });

    [Fact]
    public async Task CurrentSearchDetailsAndReaderWorkWithoutRetiredEndpointsOrScripts()
    {
        var handler = new CurrentSite();
        using var client = new HttpClient(handler);
        var provider = new LeerCapituloApiContentProvider(client);
        var result = Assert.Single(await provider.SearchAsync(Root, "Alternative & name"));
        Assert.Equal("Hidden Logistics", result.PreviewItem.Name);
        Assert.Equal("https://reader.example/covers/new.webp", result.PreviewItem.ThumbnailRef);
        Assert.Contains("q=Alternative%20%26%20name", handler.Requests[0].Query);
        var details = (await provider.LoadItemAsync(Root, Candidate)).CatalogItem;
        CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [details] });
        Assert.Equal("Hidden Logistics", details.Name);
        Assert.Equal("A new description.", details.Description);
        Assert.Equal("ongoing", details.PublicationStatus);
        Assert.Equal(["Manga", "Manhwa"], details.Categories);
        Assert.Equal(["Action"], details.Genres); // Not the sidebar or theme tags.
        Assert.Equal(["Alternative name", "다른 이름"], details.AlternativeNames.Select(x => x.Name));
        Assert.Equal("https://reader.example/covers/new.webp", details.ThumbnailRef);
        Assert.Equal(["72", "73"], details.Units.Select(x => x.DisplayNumber));
        Assert.Equal("Capitulo 73", details.Units[1].Name); // Excludes update date.

        var unit = new ApiContentUnitRequest(ContentUnitKind.Chapter, "73", null);
        var images = await provider.LoadUnitImagesAsync(Root, result, unit);
        Assert.NotNull(images);
        Assert.Equal(["https://images.example/first.webp", "https://images.example/second.webp"], images.Pages.Select(x => x.Uri.AbsoluteUri));
        Assert.Equal([0, 1], images.Pages.Select(x => x.Position));
        Assert.All(images.Pages, image => Assert.Equal(new Uri(Root, "leer/id1/hidden-logistics/73/"), image.Referrer));
        Assert.Contains(new Uri("https://images.example/"), provider.GetTrustedArtworkOrigins(Root));
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.Contains("search-autocomplete") || uri.AbsolutePath.EndsWith(".js"));
    }

    [Fact]
    public async Task ModernGenreSearchCombinesQueryAndSupportsContinuation()
    {
        var handler = new CurrentSite();
        using var client = new HttpClient(handler);
        var provider = new LeerCapituloApiContentProvider(client);
        var first = await provider.SearchAsync(Root, new ApiContentSearchRequest("Alternative", genre: ApiContentGenre.Action));
        Assert.Single(first.Items);
        Assert.Equal("2", first.ContinuationToken);
        Assert.Contains("genre=action", handler.Requests[^1].Query);
        Assert.Contains("q=Alternative", handler.Requests[^1].Query);
        var next = await provider.SearchAsync(Root, new ApiContentSearchRequest("Alternative", genre: ApiContentGenre.Action, continuationToken: first.ContinuationToken));
        Assert.Single(next.Items);
        Assert.Null(next.ContinuationToken);
        Assert.Contains("page=2", handler.Requests[^1].Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<img data-index='1' src='https://images.example/one.webp'>")]
    [InlineData("<img data-index='0' src='https://images.example/one.webp'><img data-index='0' src='https://images.example/two.webp'>")]
    [InlineData("<img data-index='2000' src='https://images.example/one.webp'>")]
    [InlineData("<img src='https://images.example/one.webp'>")]
    [InlineData("<img data-index='0' src='http://192.168.1.1/secret'>")]
    [InlineData("<img data-index='0' src='http://[::1]/secret'>")]
    [InlineData("<img data-index='0' src='javascript:alert(1)'>")]
    public async Task ReaderRejectsUnsafeIncompleteOrDuplicateImages(string images)
    {
        using var client = new HttpClient(new CurrentSite { ReaderImages = images });
        var provider = new LeerCapituloApiContentProvider(client);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await provider.LoadUnitImagesAsync(
            Root, Candidate, new ApiContentUnitRequest(ContentUnitKind.Chapter, "73", null)));
    }

    [Fact]
    public async Task EmptySearchDoesNotFallBackToRetiredAutocomplete()
    {
        var handler = new CurrentSite { CatalogCards = "<p>No results</p>" };
        using var client = new HttpClient(handler);
        Assert.Empty(await new LeerCapituloApiContentProvider(client).SearchAsync(Root, "Nothing"));
        Assert.Single(handler.Requests);
    }

    private sealed class CurrentSite : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string ReaderImages { get; init; } = """
            <img data-index="1" data-src="https://images.example/second.webp" src="/placeholder.png">
            <img data-index="0" data-src="" src="https://images.example/first.webp">
            """;
        public string CatalogCards { get; init; } = """
            <article class="lc-card">
              <a class="lc-card-cover" href="/manga/id1/hidden-logistics/"><img src="/covers/new.webp" alt=""><span>Manhwa</span></a>
              <div class="lc-card-body"><a class="lc-card-name" href="/manga/id1/hidden-logistics/">Hidden Logistics</a></div>
            </article>
            """;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var html = uri.AbsolutePath switch
            {
                "/" => "<a href='/manga/?genre=action'>Action</a>",
                "/manga/" => CatalogCards + (uri.Query.Contains("page=2") ? "" : "<a href='/manga/?q=Alternative&amp;genre=action&amp;page=2'>2</a>"),
                "/manga/id1/hidden-logistics/" => """
                    <aside><a href="/manga/?genre=romance">Romance</a></aside>
                    <article class="lc-panel p-3">
                      <div class="lc-cover-lg"><img src="/covers/new.webp"></div>
                      <h1 class="h3 mb-1">Hidden Logistics</h1>
                      <p class="small lc-muted mb-2">Alternative name · 다른 이름</p>
                      <a href="/manga/?genre=action">Action</a><a href="/manga/?theme=video-games">Video Games</a>
                      <ul class="lc-facts"><li><span>Estado</span><a href="/manga/?status=ongoing">Ongoing</a></li>
                        <li><span>Tipo</span><a href="/manga/?type=manhwa">Manhwa</a></li></ul>
                    </article>
                    <section id="sinopsis"><h2>Sinopsis</h2><div><p>A new description.</p></div></section>
                    <div id="chapterList">
                      <a class="lc-chapter-row" href="/leer/id1/hidden-logistics/73/"><span class="n">Capitulo 73</span><span class="d">2026-09-26</span></a>
                      <a class="lc-chapter-row" href="/leer/id1/hidden-logistics/72/"><span class="n">Capitulo 72</span><span class="d">2026-09-22</span></a>
                    </div>
                    """,
                "/leer/id1/hidden-logistics/73/" => "<img src='/ad.jpg' data-index='0'><main id='lcPages'>" + ReaderImages + "</main><img src='/footer.jpg'>",
                _ => throw new InvalidOperationException("Unexpected route: " + uri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html), RequestMessage = request });
        }
    }
}
