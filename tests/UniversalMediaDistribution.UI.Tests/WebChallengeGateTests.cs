using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// A host that answers a browser check instead of content is not refusing the
/// request, it is asking who is asking. These pin when that is retried through
/// a browser and — just as importantly — when it is not.
/// </summary>
[Collection("Web challenge gate")]
public sealed class WebChallengeGateTests : IDisposable
{
    private static readonly Uri Endpoint =
        new("https://www.leercapitulo.co/manga-list.html");

    public void Dispose() => WebChallengeGate.Reset();

    [Fact]
    public async Task ChallengedCoverReturnsImageBytesInsteadOfBrowserWrapperText()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 128, 255];
        var solver = new StubSolver(null)
        {
            ProvidesDocument = true,
            Document = new WebChallengeDocument(Convert.ToBase64String(png), "image/png", true)
        };
        WebChallengeGate.Configure(solver);
        using var client = new HttpClient(new ChallengeHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.leercapitulo.co/covers/test.jpg");

        using var response = await WebChallengeGate.SendImageAsync(client, request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, solver.DocumentCalls);
        Assert.Equal(0, solver.Calls);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
        using var second = await WebChallengeGate.SendImageAsync(client, secondRequest, CancellationToken.None);
        // Images belong in the bounded artwork cache, not the page dictionary.
        Assert.Equal(2, solver.DocumentCalls);
    }

    [Fact]
    public async Task PlainForbiddenCoverDoesNotInvokeBrowser()
    {
        var solver = new StubSolver(null) { ProvidesDocument = true };
        WebChallengeGate.Configure(solver);
        using var client = new HttpClient(new ChallengeHandler { PlainForbidden = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.leercapitulo.co/covers/test.jpg");
        using var response = await WebChallengeGate.SendImageAsync(client, request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, solver.DocumentCalls);
        Assert.Equal(0, solver.Calls);
    }

    [Theory]
    [InlineData("text/html", "aGVsbG8=")]
    [InlineData("image/svg+xml", "aGVsbG8=")]
    [InlineData("image/png", "invalid!")]
    [InlineData("image/png", "")]
    public void InvalidBinaryReportsCannotBecomeImages(string type, string body)
    {
        Assert.Throws<InvalidDataException>(() =>
            new WebChallengeDocument(body, type, true).CreateContent());
    }

    [Fact]
    public void OversizedImageReportIsRejectedBeforeDecoding()
    {
        var body = new string('A', ((WebChallengeDocument.MaximumImageBytes + 2) / 3) * 4 + 4);
        Assert.Throws<InvalidDataException>(() =>
            new WebChallengeDocument(body, "image/png", true).CreateContent());
        Assert.Null(WebChallengeDocument.ParseReport(
            "{\"contentType\":\"image/png\",\"isBase64\":true,\"body\":\"" + body + "\"}"));
    }

    [Fact]
    public async Task AChallengeIsSolvedOnceAndReplayedOnTheRetry()
    {
        var solver = new StubSolver(new WebChallengeClearance(
            "www.leercapitulo.co",
            "cf_clearance=abc123",
            BrowserUserAgent));
        var handler = new ChallengeHandler();
        WebChallengeGate.Configure(solver);
        using var httpClient = new HttpClient(handler);

        using var response = await SendAsync(httpClient);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, solver.Calls);
        Assert.Equal(2, handler.Requests.Count);

        // The first attempt goes out as the provider wrote it; the retry
        // carries the clearance, and the user agent that earned it.
        Assert.Null(handler.Requests[0].Cookie);
        Assert.Equal("cf_clearance=abc123", handler.Requests[1].Cookie);
        Assert.Equal(BrowserUserAgent, handler.Requests[1].UserAgent);
    }

    [Fact]
    public async Task LaterRequestsToTheSameHostCarryTheClearanceUpFront()
    {
        var solver = new StubSolver(new WebChallengeClearance(
            "www.leercapitulo.co",
            "cf_clearance=abc123",
            BrowserUserAgent));
        var handler = new ChallengeHandler();
        WebChallengeGate.Configure(solver);
        using var httpClient = new HttpClient(handler);

        using var first = await SendAsync(httpClient);
        using var second = await SendAsync(httpClient);

        // Solved once, not once per request.
        Assert.Equal(1, solver.Calls);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("cf_clearance=abc123", handler.Requests[^1].Cookie);
    }

    [Fact]
    public async Task APlainForbiddenIsNotAChallengeAndStartsNoBrowser()
    {
        // A route the host genuinely refuses must not spin up a browser on
        // every request.
        var solver = new StubSolver(null);
        var handler = new ChallengeHandler { PlainForbidden = true };
        WebChallengeGate.Configure(solver);
        using var httpClient = new HttpClient(handler);

        using var response = await SendAsync(httpClient);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, solver.Calls);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AHostThatIssuesNoCookieIsNotAskedForOneTwice()
    {
        // Asking costs a full timeout and cannot succeed: a host that admits
        // a browser without challenging it has no clearance to give. This is
        // not a backoff on the content — see the test below.
        var solver = new StubSolver(null);
        var handler = new ChallengeHandler();
        WebChallengeGate.Configure(solver);
        using var httpClient = new HttpClient(handler);

        using var first = await SendAsync(httpClient);
        using var second = await SendAsync(httpClient);

        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(1, solver.Calls);
    }

    [Fact]
    public async Task ThePageIsFetchedOnEveryRequestEvenAfterOneFails()
    {
        // The distinction that matters: skipping the cookie step must never
        // become "this host is written off". Every request still tries to
        // load the page, so a retry the user asks for is a real one.
        var solver = new StubSolver(null)
        {
            ProvidesDocument = true,
            DocumentFailures = 2
        };
        var handler = new ChallengeHandler();
        WebChallengeGate.Configure(solver);
        using var httpClient = new HttpClient(handler);

        using var first = await SendAsync(httpClient);
        using var second = await SendAsync(httpClient);
        using var third = await SendAsync(httpClient);

        // Two failures, then a success — the third is not suppressed by them.
        Assert.Equal(3, solver.DocumentCalls);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(
            "<html>from the browser</html>",
            await third.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APageIsNotLoadedTwiceForTheSameAction()
    {
        // A provider reads the same page to identify a title and again to open
        // a unit. Driving a browser twice for that is time the user waits for
        // nothing.
        var solver = new StubSolver(null) { ProvidesDocument = true };
        var handler = new ChallengeHandler();
        WebChallengeGate.Configure(solver);
        using var httpClient = new HttpClient(handler);

        using var first = await SendAsync(httpClient);
        using var second = await SendAsync(httpClient);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, solver.DocumentCalls);
    }

    [Fact]
    public async Task WithNoSolverTheChallengeIsSurfacedUnchanged()
    {
        var handler = new ChallengeHandler();
        WebChallengeGate.Configure(solver: null);
        using var httpClient = new HttpClient(handler);

        using var response = await SendAsync(httpClient);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient httpClient)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd(ProviderUserAgent);
        return ApiProviderHttp.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseContentRead,
            CancellationToken.None);
    }

    private const string ProviderUserAgent =
        "Mozilla/5.0 (compatible; UniversalMediaDistribution/1.0)";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    private sealed class StubSolver(WebChallengeClearance? clearance)
        : IWebChallengeSolver
    {
        public int Calls { get; private set; }

        public int DocumentCalls { get; private set; }

        /// <summary>
        /// Whether this solver can load pages at all. Off by default so the
        /// clearance tests exercise that path: the page is tried first, and a
        /// solver that supplies one never reaches the cookie step.
        /// </summary>
        public bool ProvidesDocument { get; init; }

        public WebChallengeDocument? Document { get; init; }

        /// <summary>
        /// How many document loads fail before one succeeds, for checking that
        /// a failure does not stop the next attempt.
        /// </summary>
        public int DocumentFailures { get; init; }

        public ValueTask<WebChallengeClearance?> SolveAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(clearance);
        }

        public ValueTask<WebChallengeDocument?> FetchDocumentAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            DocumentCalls++;
            return ValueTask.FromResult(
                !ProvidesDocument || DocumentCalls <= DocumentFailures
                    ? null
                    : Document ?? new WebChallengeDocument(
                        "<html>from the browser</html>",
                        "text/html"));
        }
    }

    private sealed record CapturedRequest(string? Cookie, string? UserAgent);

    /// <summary>
    /// Answers the Cloudflare interstitial until a request arrives carrying
    /// clearance, which is how the real host behaves.
    /// </summary>
    private sealed class ChallengeHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public bool PlainForbidden { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var cookie = request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join("; ", values)
                : null;
            Requests.Add(new CapturedRequest(
                cookie,
                request.Headers.UserAgent.ToString()));

            if (!PlainForbidden && cookie?.Contains(
                    "cf_clearance",
                    StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "<html>real content</html>",
                        Encoding.UTF8,
                        "text/html")
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "<title>Just a moment...</title>",
                    Encoding.UTF8,
                    "text/html")
            };
            response.Headers.TryAddWithoutValidation("server", "cloudflare");
            if (!PlainForbidden)
            {
                response.Headers.TryAddWithoutValidation(
                    "cf-mitigated",
                    "challenge");
            }

            return Task.FromResult(response);
        }
    }
}
