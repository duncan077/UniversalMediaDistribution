using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogCrawlerHttpPacingTests
{
    [Fact]
    public async Task ConfiguredIntervalCoversBackendHostsAndIsRestoredAfterScope()
    {
        var recording = new RecordingHandler();
        using var http = new HttpClient(new CatalogCrawlerHttpClient.PacedHandler(TimeSpan.FromSeconds(2)) { InnerHandler = recording });
        using (CatalogCrawlerHttpClient.UseProviderInterval(TimeSpan.FromSeconds(1)))
        {
            using var first = await http.GetAsync("https://frontend.example/one");
            using var second = await http.GetAsync("https://backend.example/two");
        }
        using var fallbackFirst = await http.GetAsync("https://fallback.example/one");
        using var fallbackSecond = await http.GetAsync("https://fallback.example/two");
        var starts = recording.Starts.ToArray();
        Assert.True(Stopwatch.GetElapsedTime(starts[0].Timestamp, starts[1].Timestamp) >= TimeSpan.FromSeconds(0.99));
        Assert.True(Stopwatch.GetElapsedTime(starts[0].Timestamp, starts[1].Timestamp) < TimeSpan.FromSeconds(1.9));
        Assert.True(Stopwatch.GetElapsedTime(starts[2].Timestamp, starts[3].Timestamp) >= TimeSpan.FromSeconds(1.99));
    }

    [Fact]
    public async Task ConcurrentProviderWorkersKeepTheirOwnConfiguredCadence()
    {
        var recording = new RecordingHandler();
        using var http = new HttpClient(new CatalogCrawlerHttpClient.PacedHandler(TimeSpan.FromSeconds(2)) { InnerHandler = recording });
        await Task.WhenAll(Worker("fast", 1), Worker("slow", 3));
        var fast = recording.Starts.Where(start => start.Uri.Host.StartsWith("fast", StringComparison.Ordinal)).ToArray();
        var slow = recording.Starts.Where(start => start.Uri.Host.StartsWith("slow", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, fast.Length);
        Assert.Equal(2, slow.Length);
        Assert.True(Stopwatch.GetElapsedTime(fast[0].Timestamp, fast[1].Timestamp) < TimeSpan.FromSeconds(1.9));
        Assert.True(Stopwatch.GetElapsedTime(slow[0].Timestamp, slow[1].Timestamp) >= TimeSpan.FromSeconds(2.99));

        async Task Worker(string prefix, int seconds)
        {
            using var pacing = CatalogCrawlerHttpClient.UseProviderInterval(TimeSpan.FromSeconds(seconds));
            using var first = await http.GetAsync($"https://{prefix}-frontend.example/one");
            using var second = await http.GetAsync($"https://{prefix}-backend.example/two");
        }
    }

    [Fact]
    public async Task CancelledWaitDoesNotSendRequestOrLeaveQueueLocked()
    {
        var recording = new RecordingHandler();
        using var http = new HttpClient(new CatalogCrawlerHttpClient.PacedHandler(TimeSpan.FromSeconds(2)) { InnerHandler = recording });
        using var pacing = CatalogCrawlerHttpClient.UseProviderInterval(TimeSpan.FromSeconds(1));
        using var first = await http.GetAsync("https://frontend.example/one");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync("https://backend.example/cancelled", cancelled.Token));
        using var third = await http.GetAsync("https://backend.example/three").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, recording.Starts.Count);
        Assert.DoesNotContain(recording.Starts, start => start.Uri.AbsolutePath == "/cancelled");
    }

    [Fact]
    public async Task ConfiguredIntervalNeverOverridesRetryAfter()
    {
        var recording = new RecordingHandler { Status = HttpStatusCode.TooManyRequests };
        using var http = new HttpClient(new CatalogCrawlerHttpClient.PacedHandler(TimeSpan.FromSeconds(2)) { InnerHandler = recording });
        using var pacing = CatalogCrawlerHttpClient.UseProviderInterval(TimeSpan.FromSeconds(1));
        var exception = await Assert.ThrowsAsync<CatalogProviderThrottledException>(() => http.GetAsync("https://frontend.example/one"));
        Assert.Equal(TimeSpan.FromMinutes(7), exception.RetryAfter);
        Assert.Single(recording.Starts);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal ConcurrentQueue<(Uri Uri, long Timestamp)> Starts { get; } = new();
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts.Enqueue((request.RequestUri!, Stopwatch.GetTimestamp()));
            var response = new HttpResponseMessage(Status) { RequestMessage = request, Content = new StringContent("fixture") };
            if (Status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.FromMinutes(7));
            return Task.FromResult(response);
        }
    }
}
