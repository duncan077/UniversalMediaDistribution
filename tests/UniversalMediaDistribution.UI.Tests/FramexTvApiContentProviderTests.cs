using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class FramexTvApiContentProviderTests
{
    private static readonly Uri Root = new("https://framex.example/");
    private static readonly ApiContentSearchItem Movie = new("movie/27205", new() { Name = "Inception", MediaType = "movie", Subtype = "movie" });
    private static readonly ApiContentSearchItem Series = new("tv/1399", new() { Name = "Game of Thrones", MediaType = "movie", Subtype = "series" });
    private static readonly ApiContentUnitRequest Episode = new(ContentUnitKind.Episode, "1", 1);

    [Fact]
    public async Task SearchKeepsMediaIdentityArtworkAndUnconsumedResults()
    {
        using var http = new HttpClient(new Handler(_ => Json(Search)));
        var provider = new FramexTvApiContentProvider(http);
        var first = await provider.SearchAsync(Root, new ApiContentSearchRequest("Inception", MediaType.Movie, pageSize: 1));
        Assert.Equal("movie/27205", Assert.Single(first.Items).ExternalId);
        Assert.Equal("1:1", first.ContinuationToken);
        var second = await provider.SearchAsync(Root, new ApiContentSearchRequest("Inception", MediaType.Movie, continuationToken: first.ContinuationToken, pageSize: 1));
        Assert.Equal("tv/27205", Assert.Single(second.Items).ExternalId);
        Assert.Equal("2:0", second.ContinuationToken);
        Assert.Contains(new Uri("https://image.tmdb.org/"), provider.GetTrustedArtworkOrigins(Root));
        Assert.Empty(await provider.SearchAsync(Root, "ignored", MediaType.Book));
    }

    [Fact]
    public async Task MovieMetadataHasExactlyOneEpisodeAndDoesNotFetchVideos()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request => { requests.Add(request.RequestUri!); return Json(MovieDetail); }));
        var item = (await new FramexTvApiContentProvider(http).LoadItemAsync(Root, Movie)).CatalogItem;
        CatalogTransferValidator.Validate(new() { Items = [item] });
        Assert.Equal("Inception", item.Name);
        Assert.Equal(2010, item.ReleaseYear);
        Assert.Equal(8880, item.RuntimeSeconds);
        Assert.Equal("1", Assert.Single(item.Units).DisplayNumber);
        Assert.Equal("https://image.tmdb.org/t/p/w500/cover.jpg", item.ThumbnailRef);
        Assert.Single(item.Genres);
        Assert.Single(requests);
        Assert.Contains("/api/details?", requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task EpisodeDiscoveryLoadsOnlyRequestedSeasonAndValidatesItsIdentity()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("details", StringComparison.Ordinal)
            ? Json(TvDetail) : Json(Season));
        using var http = new HttpClient(handler);
        var provider = new FramexTvApiContentProvider(http);
        var item = await provider.LoadItemForUnitAsync(Root, Series, Episode);
        var season = Assert.Single(item.CatalogItem.Seasons);
        Assert.Equal(1, season.Number);
        Assert.Equal("Winter Is Coming", Assert.Single(season.Units).Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r.Query.Contains("season=2", StringComparison.Ordinal));
        CatalogTransferValidator.Validate(new() { Items = [item.CatalogItem] });
    }

    [Fact]
    public async Task FullCatalogImportFetchesAllSeasonsButNoStreamEndpoints()
    {
        using var http = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("details", StringComparison.Ordinal)
            ? TvDetail : request.RequestUri.Query.EndsWith("season=2", StringComparison.Ordinal) ? Season.Replace("\"season_number\":1", "\"season_number\":2", StringComparison.Ordinal) : Season)));
        var item = await new FramexTvApiContentProvider(http).LoadItemAsync(Root, Series);
        Assert.Equal(2, item.CatalogItem.Seasons.Count);
        Assert.Empty(item.VideoSets);
    }

    [Fact]
    public async Task SpecialsCoercedToSeasonOneDoNotBlockTheFullTitleOrBecomeSeasonZero()
    {
        var detail = TvDetail.Replace("\"seasons\":[", "\"seasons\":[{\"season_number\":0,\"name\":\"Specials\"},", StringComparison.Ordinal);
        var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("details", StringComparison.Ordinal)
            ? detail : request.RequestUri.Query.EndsWith("season=2", StringComparison.Ordinal)
                ? Season.Replace("\"season_number\":1", "\"season_number\":2", StringComparison.Ordinal) : Season));
        using var http = new HttpClient(handler);
        var item = await new FramexTvApiContentProvider(http).LoadItemAsync(Root, Series);
        Assert.Equal([1, 2], item.CatalogItem.Seasons.Select(s => s.Number));
        Assert.All(item.CatalogItem.Seasons, s => Assert.Equal("Winter Is Coming", Assert.Single(s.Units).Name));
        CatalogTransferValidator.Validate(new() { Items = [item.CatalogItem] });
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ExplicitSpecialNeverUsesSeasonOneOrOpensItsPlaybackEndpoint()
    {
        var detail = TvDetail.Replace("\"seasons\":[", "\"seasons\":[{\"season_number\":0},", StringComparison.Ordinal);
        var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("details", StringComparison.Ordinal) ? detail : Season));
        using var http = new HttpClient(handler);
        var provider = new FramexTvApiContentProvider(http);
        var special = new ApiContentUnitRequest(ContentUnitKind.Episode, "1", 0);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadItemForUnitAsync(Root, Series, special).AsTask());
        Assert.Contains("season 1 instead of requested season 0", error.Message);
        var count = handler.Requests.Count;
        Assert.Null(await provider.LoadUnitVideosAsync(Root, Series, special));
        Assert.Equal(count, handler.Requests.Count);
    }

    [Fact]
    public async Task ARegularSeasonMismatchStillRejectsAFullCatalogImport()
    {
        using var http = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("details", StringComparison.Ordinal)
            ? TvDetail : Season)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new FramexTvApiContentProvider(http).LoadItemAsync(Root, Series).AsTask());
        Assert.Contains("season 1 instead of requested season 2", error.Message);
    }

    [Fact]
    public async Task WrongTitleAndSeasonAreRejectedInsteadOfMixingEpisodes()
    {
        using var wrongTitle = new HttpClient(new Handler(_ => Json(MovieDetail.Replace("27205", "999", StringComparison.Ordinal))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FramexTvApiContentProvider(wrongTitle).LoadItemAsync(Root, Movie).AsTask());
        using var wrongSeason = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("details", StringComparison.Ordinal)
            ? TvDetail : Season.Replace("\"season_number\":1", "\"season_number\":2", StringComparison.Ordinal))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FramexTvApiContentProvider(wrongSeason).LoadItemForUnitAsync(Root, Series, Episode).AsTask());
    }

    [Fact]
    public async Task CapturesDirectStreamsAndSubtitlesWithHeadersAndRejectsPrivateUrls()
    {
        using var http = new HttpClient(new Handler(_ => Json(Streams)));
        var set = await new FramexTvApiContentProvider(http).LoadUnitVideosAsync(Root, Movie, new(ContentUnitKind.Episode, "1", null));
        var source = Assert.Single(set!.Sources);
        Assert.False(source.IsEmbeddedWeb);
        Assert.Equal("application/vnd.apple.mpegurl", source.MimeType);
        Assert.Equal(new Uri("https://player.example/"), source.HttpOrigin);
        Assert.Equal(new Uri("https://player.example/watch"), source.Referrer);
        Assert.Equal(2, source.Subtitles.Count);
        Assert.Equal("es", source.Subtitles[1].Language);
        var variant = ApiVideoServerVariantPlanner.CreateVariant(source);
        Assert.Equal(source.Subtitles.Select(s => s.Source), variant.Subtitles!.Select(s => s.Source));
        Assert.True(variant.Subtitles![0].IsDefault);
    }

    [Fact]
    public async Task MissingStreamsOfferFramexEmbedAndIndependentSubtitlesForInteractiveCapture()
    {
        using var http = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("subtitles", StringComparison.Ordinal)
            ? """{"success":true,"subtitles":[{"url":"https://subs.example/es.vtt","lang":"es"}]}"""
            : """{"success":false,"sources":[],"error":"No working sources"}""")));
        var set = await new FramexTvApiContentProvider(http).LoadUnitVideosAsync(new("https://framextv.xyz/"), Series, Episode);
        var source = Assert.Single(set!.Sources);
        Assert.True(source.IsEmbeddedWeb);
        Assert.Equal("https://framextv.xyz/embed/tv/1399/1/1", source.PlaybackUri.AbsoluteUri);
        Assert.True(VideoChallengeCapture.SupportsInteractiveCapture(source.PlaybackUri));
        Assert.Equal("es", Assert.Single(source.Subtitles).Language);
    }

    [Fact]
    public async Task SubtitleEndpointFailureDoesNotDiscardAWorkingVideo()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("subtitles", StringComparison.Ordinal)
            ? new(HttpStatusCode.ServiceUnavailable)
            : Json("""{"success":true,"sources":[{"url":"https://video.example/movie.mp4","type":"mp4"}]}""")));
        var set = await new FramexTvApiContentProvider(http).LoadUnitVideosAsync(Root, Movie, new(ContentUnitKind.Episode, "1", null));
        Assert.False(Assert.Single(set!.Sources).IsEmbeddedWeb);
    }

    [Fact]
    public async Task CancellationAndResponseSizeLimitsApply()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new Handler(_ => Json(Search)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FramexTvApiContentProvider(http).SearchAsync(Root, "title", cancellationToken: cancellation.Token).AsTask());
        using var large = new HttpClient(new Handler(_ => Json(new string('x', 4 * 1024 * 1024 + 1))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FramexTvApiContentProvider(large).SearchAsync(Root, "title").AsTask());
    }

    [Fact]
    public async Task DiscoveryRetainsMovieTvPaginationAndDoesNotFetchPlayer()
    {
        var handler = new Handler(_ => Json(Search.Replace("\"totalPages\":2", "\"totalPages\":1", StringComparison.Ordinal)));
        using var http = new HttpClient(handler);
        var page = await new FramexTvApiContentProvider(http).ReadCatalogPageAsync(Root, null);
        Assert.Equal("tv:1", page.ContinuationToken);
        var end = await new FramexTvApiContentProvider(http).ReadCatalogPageAsync(Root, page.ContinuationToken);
        Assert.Null(end.ContinuationToken);
        Assert.All(handler.Requests, uri => Assert.Equal("/api/discover", uri.AbsolutePath));
    }

    [Fact]
    public async Task KnownBackendFailoverIsBoundedAndDoesNotRetryRateLimiting()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.IdnHost == "api.framextv.tech"
            ? new(HttpStatusCode.BadGateway) : Json(Search)));
        Assert.NotEmpty(await new FramexTvApiContentProvider(http).SearchAsync(new("https://framextv.xyz/"), "Inception"));
        var handler = new Handler(_ => new(HttpStatusCode.TooManyRequests));
        using var throttled = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new FramexTvApiContentProvider(throttled).SearchAsync(new("https://framextv.xyz/"), "Inception").AsTask());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("movie/../../secret")]
    [InlineData("https://evil.example/movie/1")]
    [InlineData("tv/0")]
    public async Task InvalidExternalIdentitiesNeverIssueARequest(string identity)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Must not fetch"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new FramexTvApiContentProvider(http).LoadItemAsync(Root, new(identity, Movie.PreviewItem)).AsTask());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LiveFramexImportsWatchWhatHappensWithoutRelabelingSeasonOneAsSpecials()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_FRAMEXTV_SEASONS") != "1") return;
        using var http = CatalogCrawlerHttpClient.Create();
        var provider = new FramexTvApiContentProvider(http);
        var candidate = new ApiContentSearchItem("tv/22980", Series.PreviewItem with { Name = "Watch What Happens Live with Andy Cohen" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var item = await provider.LoadItemAsync(new(provider.Descriptor.DefaultBaseUrl), candidate, timeout.Token);
        Assert.Equal(candidate.PreviewItem.Name, item.CatalogItem.Name);
        CatalogTransferValidator.Validate(new() { Items = [item.CatalogItem] });
        Assert.DoesNotContain(item.CatalogItem.Seasons, season => season.Number == 0);
        Assert.Equal("Pilot: Danielle Staub", item.CatalogItem.Seasons.Single(s => s.Number == 1).Units.Single(u => u.DisplayNumber == "1").Name);
        Assert.Contains(item.CatalogItem.Seasons, season => season.Number == 2);
        Assert.True(item.CatalogItem.Seasons.Sum(s => s.Units.Count) > 2000);
        Assert.Empty(item.VideoSets);
    }

    [Fact]
    public async Task LiveFramexMovieProvidesNativeVideoAndSubtitleTracks()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_FRAMEXTV") != "1") return;
        using var http = CatalogCrawlerHttpClient.Create();
        var provider = new FramexTvApiContentProvider(http);
        var root = new Uri(provider.Descriptor.DefaultBaseUrl);
        var search = await provider.SearchAsync(root, "Fight Club");
        var item = Assert.Single(search, i => i.ExternalId == "movie/550");
        Assert.Equal("Fight Club", (await provider.LoadItemAsync(root, item)).CatalogItem.Name);
        var set = await provider.LoadUnitVideosAsync(root, item, new(ContentUnitKind.Episode, "1", null));
        Assert.Contains(set!.Sources, s => !s.IsEmbeddedWeb);
        Assert.Contains(set.Sources.SelectMany(s => s.Subtitles), track => track.Language == "es");
        var native = set.Sources.First(s => !s.IsEmbeddedWeb);
        Assert.True(await VideoChallengeMediaProbe.VerifyAsync(http, native, CancellationToken.None), "The native URL must expose an HLS manifest and reachable media data.");
        var subtitle = native.Subtitles.First(t => t.Language == "es");
        var text = await TmoProviderHttp.ReadStringAsync(http, subtitle.Source, root, 1024 * 1024,
            "text/vtt, text/plain, */*", "FramexTV subtitle probe", CancellationToken.None);
        Assert.Contains("-->", text);
    }

    private const string Search = """
        {"success":true,"page":1,"totalPages":2,"results":[
          {"id":"27205","type":"movie","title":"Inception","year":"2010"},
          {"id":"27205","type":"tv","title":"Another series"}]}
        """;
    private const string MovieDetail = """
        {"success":true,"id":"27205","type":"movie","title":"Inception","year":"2010","runtime":148,
         "poster":"https://image.tmdb.org/t/p/w500/cover.jpg","release_date":"2010-07-15","genres":["Action","action"]}
        """;
    private const string TvDetail = """
        {"success":true,"id":"1399","type":"tv","title":"Game of Thrones","seasons":[
          {"season_number":1,"name":"Season 1"},{"season_number":2,"name":"Season 2"}]}
        """;
    private const string Season = """
        {"success":true,"season_number":1,"episodes":[{"number":1,"season_number":1,"name":"Winter Is Coming","air_date":"2011-04-17"}]}
        """;
    private const string Streams = """
        {"success":true,"provider":"lavahound","headers":{"Referer":"https://player.example/watch","Origin":"https://player.example"},
         "sources":[{"url":"https://cdn.example/master.m3u8","quality":"4K / Auto","type":"hls"},
           {"url":"https://cdn.example/master.m3u8","type":"hls"},{"url":"http://127.0.0.1/private.mp4","type":"mp4"}],
         "subtitles":[{"url":"https://subs.example/en.vtt","label":"English","lang":"en","isDefault":true},
           {"url":"https://subs.example/es.srt","label":"Español","lang":"es"},
           {"url":"https://subs.example/es.srt","lang":"es"},{"url":"http://192.168.1.1/private.srt","lang":"en"}]}
        """;
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
