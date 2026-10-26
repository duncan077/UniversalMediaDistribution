using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ProviderCanonicalDetailTests
{
    private static readonly Uri Root = new("https://reader.example/");
    private static readonly ApiContentSearchItem Old = new("old-id/aman-s-animal-tales", new()
    { MediaType = "book", Subtype = "manga", Name = "Aman's Animal Tales" });
    private const string CanonicalRoute = "/manga/new-id/aman-s-animal-tales/";
    private const string Detail = """
        <article class="lc-panel"><h1>Aman's Animal Tales</h1><p class="lc-muted">Animal Tales</p></article>
        <a href="/leer/new-id/aman-s-animal-tales/14/">Capitulo 14</a>
        """;

    [Fact]
    public void SeriesFlixMarksTheOldDomainAsRetiredButPreservesCustomMirrors()
    {
        var descriptor = BuiltInApiContentProviderDescriptors.SeriesFlix;
        Assert.Equal("https://seriesflixhd.pw/", descriptor.DefaultBaseUrl);
        Assert.True(descriptor.IsRetiredBaseUrl("https://seriesflixhd.win/"));
        Assert.True(descriptor.IsRetiredBaseUrl("https://www.seriesflixhd.win/"));
        Assert.False(descriptor.IsRetiredBaseUrl("https://my-mirror.example/"));
    }

    [Fact]
    public async Task CanonicalIdRedirectKeepsChaptersAndReaderAndAvoidsRepeatingTheOldRoute()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.Contains("old-id", StringComparison.Ordinal)
            ? Redirect(CanonicalRoute) : Text(request.RequestUri.AbsolutePath.StartsWith("/leer/", StringComparison.Ordinal)
                ? """<main id="lcPages"><img data-index="0" src="https://images.example/page.webp"></main>""" : Detail));
        using var http = new HttpClient(handler);
        var provider = new LeerCapituloApiContentProvider(http);
        var item = await provider.LoadItemAsync(Root, Old);
        Assert.Equal("Aman's Animal Tales", item.CatalogItem.Name);
        Assert.Equal("14", Assert.Single(item.CatalogItem.Units).DisplayNumber);
        var unit = new ApiContentUnitRequest(ContentUnitKind.Chapter, "14", null);
        var images = await provider.LoadUnitImagesAsync(Root, Old, unit);
        Assert.Equal(new Uri(Root, "leer/new-id/aman-s-animal-tales/14/"), Assert.Single(images!.Pages).Referrer);
        Assert.Single(provider.GetKnownUnitRoutes(Root, Old, unit));
        handler.Requests.Clear();
        await provider.LoadItemAsync(Root, Old);
        Assert.Equal(CanonicalRoute, Assert.Single(handler.Requests).AbsolutePath);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("http://169.254.169.254/private")]
    [InlineData("http://[::1]/private")]
    [InlineData("https://other-public.example/manga/id/slug/")]
    public async Task RedirectsCannotSendRequestsToPrivateOrForeignOrigins(string location)
    {
        var handler = new Handler(_ => Redirect(location));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LeerCapituloApiContentProvider(http).LoadItemAsync(Root, Old).AsTask());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RedirectLoopsAndRedirectsToHomepageDoNotBecomeSuccessfulTitleImports()
    {
        var handler = new Handler(_ => Redirect("/manga/old-id/aman-s-animal-tales/"));
        using var looping = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LeerCapituloApiContentProvider(looping).LoadItemAsync(Root, Old).AsTask());
        Assert.Single(handler.Requests);
        using var home = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/" ? Text(Detail) : Redirect("/")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new LeerCapituloApiContentProvider(home).LoadItemAsync(Root, Old).AsTask());
    }

    [Fact]
    public async Task RedirectChainIsBoundedAndCancellationStillPropagates()
    {
        var handler = new Handler(request => Redirect(request.RequestUri!.AbsolutePath + "next/"));
        using var chain = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LeerCapituloApiContentProvider(chain).LoadItemAsync(Root, Old).AsTask());
        Assert.Equal(4, handler.Requests.Count);
        using var cancellation = new CancellationTokenSource();
        using var cancelled = new HttpClient(new Handler(_ => { cancellation.Cancel(); return Redirect(CanonicalRoute); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LeerCapituloApiContentProvider(cancelled).LoadItemAsync(Root, Old, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task LiveLeerCapituloFollowsTheReportedAmanMergeWithoutLoadingReaderPages()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_REPORTED_DETAILS") != "1") return;
        using var http = CatalogCrawlerHttpClient.Create();
        IApiCatalogProvider provider = new LeerCapituloApiContentProvider(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var item = await provider.LoadCatalogItemAsync(new(provider.Descriptor.DefaultBaseUrl),
            new("j5fxoekrps/aman-s-animal-tales", Old.PreviewItem), timeout.Token);
        Assert.Equal(Old.PreviewItem.Name, item.Name);
        Assert.NotEmpty(item.Units);
        Assert.Contains(item.Units, unit => unit.DisplayNumber == "14");
        CatalogTransferValidator.Validate(new() { Items = [item] });
    }

    [Fact]
    public async Task LiveSeriesFlixLoadsTheWalkingDeadFullCatalogWithoutPlayers()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_REPORTED_DETAILS") != "1") return;
        using var http = CatalogCrawlerHttpClient.Create();
        IApiCatalogProvider provider = new SeriesFlixApiContentProvider(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var item = await provider.LoadCatalogItemAsync(new(provider.Descriptor.DefaultBaseUrl),
            new("serie/the-walking-dead-zjvz/", new() { Name = "Serie The Walking Dead", MediaType = "movie", Subtype = "series" }), timeout.Token);
        CatalogTransferValidator.Validate(new() { Items = [item] });
        Assert.Equal(11, item.Seasons.Count);
        Assert.True(item.Seasons.Sum(s => s.Units.Count) > 150);
    }

    private static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.MovedPermanently) { Headers = { Location = new(target, UriKind.RelativeOrAbsolute) } };
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/html") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
