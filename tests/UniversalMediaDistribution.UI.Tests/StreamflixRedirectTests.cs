using System.Collections.Concurrent;
using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// These catalogues move domain and leave the old one redirecting. Everything
/// the pages then carry sits on the new origin, and the same-origin checks
/// would discard all of it — the provider would return nothing at all rather
/// than fail visibly.
/// </summary>
public sealed class StreamflixRedirectTests
{
    private static readonly Uri ConfiguredUri = new("https://old.example/");
    private static readonly Uri MovedUri = new("https://new.example/");
    private static readonly Uri ThirdUri = new("https://third.example/");

    [Fact]
    public async Task ResultsSurviveACrossDomainRedirect()
    {
        var handler = new RedirectingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new SeriesFlixApiContentProvider(httpClient);

        var results = await provider.SearchAsync(ConfiguredUri, "fire");

        // Before the move was honoured, every card here was thrown away
        // because its link pointed at the new origin.
        var item = Assert.Single(results);
        Assert.Equal("Fire Country", item.PreviewItem.Name);
    }

    [Fact]
    public async Task TheMoveIsHonouredOnTheFirstSearchNotTheSecond()
    {
        var handler = new RedirectingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new SeriesFlixApiContentProvider(httpClient);

        var first = await provider.SearchAsync(ConfiguredUri, "fire");

        Assert.NotEmpty(first);
    }

    [Fact]
    public async Task ArtworkOnTheNewOriginBecomesTrusted()
    {
        var handler = new RedirectingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new SeriesFlixApiContentProvider(httpClient);

        // Untrusted artwork origins are blocked, so a move would otherwise
        // strip every poster while the titles themselves loaded.
        Assert.DoesNotContain(
            provider.GetTrustedArtworkOrigins(ConfiguredUri),
            origin => origin.Host == MovedUri.Host);

        await provider.SearchAsync(ConfiguredUri, "fire");

        Assert.Contains(
            provider.GetTrustedArtworkOrigins(ConfiguredUri),
            origin => origin.Host == MovedUri.Host);
    }

    [Fact]
    public async Task LaterRequestsGoStraightToTheNewOrigin()
    {
        var handler = new RedirectingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new SeriesFlixApiContentProvider(httpClient);

        await provider.SearchAsync(ConfiguredUri, "fire");
        handler.Requests.Clear();
        await provider.SearchAsync(ConfiguredUri, "fire");

        // One redirect is the whole cost of a move.
        Assert.All(
            handler.Requests,
            uri => Assert.Equal(MovedUri.Host, uri.Host));
    }

    [Fact]
    public async Task ASiteThatHasNotMovedIsLeftAlone()
    {
        var handler = new RedirectingHandler { Redirect = false };
        using var httpClient = new HttpClient(handler);
        var provider = new SeriesFlixApiContentProvider(httpClient);

        await provider.SearchAsync(ConfiguredUri, "fire");

        Assert.All(
            handler.Requests,
            uri => Assert.Equal(ConfiguredUri.Host, uri.Host));
        Assert.DoesNotContain(
            provider.GetTrustedArtworkOrigins(ConfiguredUri),
            origin => origin.Host == MovedUri.Host);
    }

    [Fact]
    public async Task AnUnsafeFirstRedirectIsRejected()
    {
        var handler = new RedirectingHandler
        {
            RedirectTarget = new Uri("http://127.0.0.1/private")
        };
        using var httpClient = new HttpClient(handler);
        var provider = new SeriesFlixApiContentProvider(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await provider.SearchAsync(ConfiguredUri, "fire"));

        Assert.Contains(
            "unsafe",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ASecondRedirectIsRejected()
    {
        var handler = new RedirectingHandler
        {
            RedirectAgain = true
        };
        using var httpClient = new HttpClient(handler);
        var provider = new SeriesFlixApiContentProvider(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await provider.SearchAsync(ConfiguredUri, "fire"));

        Assert.Contains(
            "excessive",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class RedirectingHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requests { get; } = [];

        public bool Redirect { get; init; } = true;

        public bool RedirectAgain { get; init; }

        public Uri RedirectTarget { get; init; } = MovedUri;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requested = request.RequestUri ??
                throw new InvalidOperationException("Missing request URI.");
            Requests.Add(requested);

            if (Redirect && requested.Host == ConfiguredUri.Host)
            {
                return Task.FromResult(
                    RedirectResponse(
                        request,
                        new Uri(RedirectTarget, requested.PathAndQuery)));
            }

            if (RedirectAgain && requested.Host == MovedUri.Host)
            {
                return Task.FromResult(
                    RedirectResponse(
                        request,
                        new Uri(ThirdUri, requested.PathAndQuery)));
            }

            var cardOrigin = requested.GetLeftPart(UriPartial.Authority);
            var html = $$"""
                <main>
                  <article class="item">
                    <a href="{{cardOrigin}}/serie/fire-country">
                      <img src="{{cardOrigin}}/img/fire.webp"
                           alt="Fire Country">
                      <h2>Fire Country</h2>
                    </a>
                  </article>
                </main>
                """;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    requested),
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html))
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage RedirectResponse(
            HttpRequestMessage request,
            Uri location)
        {
            var response = new HttpResponseMessage(
                HttpStatusCode.MovedPermanently)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    request.RequestUri)
            };
            response.Headers.Location = location;
            return response;
        }
    }
}
