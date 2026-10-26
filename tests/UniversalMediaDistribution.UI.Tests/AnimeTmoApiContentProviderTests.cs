using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class AnimeTmoApiContentProviderTests
{
    private static readonly Uri BaseUri =
        new("https://animetmo.example/");

    [Fact]
    public async Task CatalogDetailAndZillaPlaybackFollowCapturedRoutes()
    {
        var handler = new AnimeTmoHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new AnimeTmoApiContentProvider(httpClient);

        var firstPage = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 1988,
                pageSize: 1));

        var result = Assert.Single(firstPage.Items);
        Assert.Equal("64700/akira", result.ExternalId);
        Assert.Equal("Akira", result.PreviewItem.Name);
        Assert.Equal("anime_movie", result.PreviewItem.Subtype);
        Assert.Equal(1988, result.PreviewItem.ReleaseYear);
        Assert.Equal("Finalizado", result.PreviewItem.PublicationStatus);
        Assert.Equal(["accion", "ciencia-ficcion"], result.PreviewItem.Genres);
        Assert.Equal("1", firstPage.ContinuationToken);

        var secondPage = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 1988,
                pageSize: 1,
                continuationToken: firstPage.ContinuationToken));
        Assert.Equal("64701/akira-remaster", Assert.Single(secondPage.Items).ExternalId);
        Assert.Null(secondPage.ContinuationToken);

        var updates = await provider.LoadDailyUpdatesAsync(BaseUri);
        Assert.Equal("64700/akira", updates[0].ExternalId);
        Assert.Equal(1, handler.CatalogRequestCount);

        var item = await provider.LoadItemAsync(BaseUri, result);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal("Akira", item.CatalogItem.Name);
        Assert.Equal("anime_movie", item.CatalogItem.Subtype);
        Assert.Equal(1988, item.CatalogItem.ReleaseYear);
        Assert.Equal("A biker fights a secret experiment.", item.CatalogItem.Description);
        Assert.Equal("Finalizado", item.CatalogItem.PublicationStatus);
        Assert.Contains("Acción", item.CatalogItem.Genres);
        Assert.Contains(
            item.CatalogItem.AlternativeNames,
            static alias => alias.Name == "Akira 1988");
        Assert.Equal(
            ["1", "2"],
            item.CatalogItem.Units.Select(static unit => unit.DisplayNumber));

        var unit = new ApiContentUnitRequest(
            ContentUnitKind.Episode,
            "1",
            seasonNumber: null);
        var route = Assert.Single(provider.GetKnownUnitRoutes(
            BaseUri,
            result,
            unit));
        Assert.Equal("/ver/akira/1", route.Route);

        var videos = await provider.LoadUnitVideosAsync(
            BaseUri,
            result,
            unit);
        Assert.NotNull(videos);
        Assert.Collection(
            videos.Sources,
            hls =>
            {
                Assert.Equal("HLS · SUB", hls.Label);
                Assert.Equal(
                    "https://player.zilla-networks.com/m3u8/0123456789abcdef0123456789abcdef",
                    hls.PlaybackUri.AbsoluteUri);
                Assert.Equal(
                    "https://player.zilla-networks.com/play/0123456789abcdef0123456789abcdef",
                    hls.Referrer?.AbsoluteUri);
                Assert.Equal(
                    "https://player.zilla-networks.com/",
                    hls.HttpOrigin?.AbsoluteUri);
                Assert.Equal(
                    "application/vnd.apple.mpegurl",
                    hls.MimeType);
                Assert.Equal(ApiContentVideoSourceKind.DirectMedia, hls.Kind);
            },
            web =>
            {
                Assert.Equal("VOE · LAT", web.Label);
                Assert.Equal(ApiContentVideoSourceKind.EmbeddedWeb, web.Kind);
                Assert.Equal("https://voe.sx/e/abcdef", web.PlaybackUri.AbsoluteUri);
                Assert.Equal(
                    "https://animetmo.example/ver/akira/1",
                    web.Referrer?.AbsoluteUri);
            });

        var episodeRequest = Assert.Single(
            handler.Requests,
            static request => request.Uri.AbsolutePath == "/ver/akira/1");
        Assert.Equal(
            "https://animetmo.example/anime/akira/",
            episodeRequest.Referrer?.AbsoluteUri);
        Assert.Contains(
            provider.GetTrustedArtworkOrigins(BaseUri),
            static origin => origin.AbsoluteUri ==
                "https://cdn.animetmo.example/");
    }

    [Fact]
    public async Task NonAnimeMediaTypeDoesNotMakeARequest()
    {
        var handler = new AnimeTmoHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new AnimeTmoApiContentProvider(httpClient);

        var results = await provider.SearchAsync(
            BaseUri,
            "Akira",
            MediaType.Book);

        Assert.Empty(results);
        Assert.Empty(handler.Requests);
    }

    private sealed class AnimeTmoHandler : HttpMessageHandler
    {
        public int CatalogRequestCount { get; private set; }

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            Requests.Add(new CapturedRequest(uri, request.Headers.Referrer));

            // The provider verifies that an HLS playlist's segments can
            // actually be read before offering it to the native player, so a
            // fixture that stops at the episode page would describe a stream
            // no player could start.
            if (uri.AbsolutePath.StartsWith("/segs/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.PartialContent)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "x",
                        Encoding.UTF8,
                        "video/mp4")
                });
            }

            var content = uri.AbsolutePath switch
            {
                "/wp-content/uploads/az-catalog.json" => CatalogJson,
                "/anime/akira/" => DetailHtml,
                "/ver/akira/1" => EpisodeHtml,
                "/m3u8/0123456789abcdef0123456789abcdef" => PlaylistText,
                _ => null
            };
            if (content is null)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            }

            if (uri.AbsolutePath == "/wp-content/uploads/az-catalog.json")
            {
                CatalogRequestCount++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                        ? "application/json"
                        : "text/html")
            });
        }

        private const string CatalogJson =
            """
            [
              {
                "id": 64700,
                "title": "Akira",
                "slug": "akira",
                "url": "https://animetmo.example/anime/akira/",
                "cover": "https://cdn.animetmo.example/covers/akira.webp",
                "year": "1988",
                "type_name": "Película",
                "type_slug": "pelicula",
                "stat_name": "Finalizado",
                "genres": ["accion", "ciencia-ficcion"]
              },
              {
                "id": 64701,
                "title": "Akira Remaster",
                "slug": "akira-remaster",
                "cover": "/wp-content/uploads/akira-remaster.webp",
                "year": "1988",
                "type_name": "Película",
                "type_slug": "pelicula",
                "stat_name": "Finalizado",
                "genres": ["accion"]
              },
              {
                "id": 64702,
                "title": "Video Senshi Laserion",
                "slug": "video-senshi-laserion",
                "cover": "/wp-content/uploads/laserion.webp",
                "year": "1984",
                "type_name": "TV",
                "type_slug": "tv",
                "stat_name": "Finalizado",
                "genres": ["mecha", "shounen"]
              }
            ]
            """;

        private const string DetailHtml =
            """
            <!doctype html>
            <meta property="og:image" content="https://cdn.animetmo.example/backdrops/akira.webp">
            <h1 class="book-type badge">Película</h1>
            <h1 class="element-title">Akira</h1>
            <img class="book-thumbnail" src="https://cdn.animetmo.example/covers/akira-large.webp">
            <div class="manga-meta-bar"><span>Publicado</span> 1988</div>
            <p class="element-description" id="anime-synopsis">
              A biker fights a <strong>secret experiment</strong>.
            </p>
            <a href="/biblioteca?genre=accion">Acción</a>
            <a href="/biblioteca?genre=ciencia-ficcion">Ciencia ficción</a>
            <span class="book-status">
              <span class="status-dot"></span>
              Finalizado
            </span>
            <div class="synonyms-wrap">
              <span class="syn-badge">Akira; Akira 1988</span>
            </div>
            <a class="az-ep-card" data-episode-number="2" href="/ver/akira/2">Episodio 2</a>
            <a class="az-ep-card" data-episode-number="1" href="/ver/akira/1">Episodio 1</a>
            """;

        private const string EpisodeHtml =
            """
            <!doctype html>
            <div class="az-servers-box">
              <button class="az-srv-btn active"
                      data-url="https://player.zilla-networks.com/play/0123456789abcdef0123456789abcdef"
                      data-type="SUB">HLS</button>
              <button class="az-srv-btn"
                      data-url="https://voe.sx/e/abcdef"
                      data-type="LAT">VOE</button>
            </div>
            """;

        private const string PlaylistText =
            """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-PLAYLIST-TYPE:VOD
            #EXT-X-MAP:URI="https://player.zilla-networks.com/segs/0123456789abcdef0123456789abcdef/init.html"
            #EXTINF:10.0,
            https://player.zilla-networks.com/segs/0123456789abcdef0123456789abcdef/000.html
            #EXT-X-ENDLIST
            """;
    }

    private sealed record CapturedRequest(Uri Uri, Uri? Referrer);
}
