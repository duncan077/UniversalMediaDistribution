using System.Diagnostics;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class CatalogCrawlerCadenceTests
{
    [Fact]
    public async Task ProviderSpecificIntervalOverridesFallbackWithoutSlowingOtherWorkers()
    {
        using var scope = TestDirectory.Create();
        var fast = new FixtureProvider("fast", 2);
        var slow = new FixtureProvider("slow", 2);
        var result = await new CatalogCrawler(new(scope.Path), [new(fast), new(slow)], new()
        {
            ProviderIntervals = new Dictionary<string, TimeSpan>
            {
                ["fast"] = TimeSpan.FromSeconds(1),
                ["slow"] = TimeSpan.FromSeconds(3)
            }
        }).RunAsync(CancellationToken.None);
        Assert.Equal(4, result.TitlesUpdated);
        var fastDelay = Stopwatch.GetElapsedTime(fast.Starts[0].Timestamp, fast.Starts[1].Timestamp);
        var slowDelay = Stopwatch.GetElapsedTime(slow.Starts[0].Timestamp, slow.Starts[1].Timestamp);
        Assert.True(fastDelay >= TimeSpan.FromSeconds(0.99));
        Assert.True(fastDelay < TimeSpan.FromSeconds(1.9));
        Assert.True(slowDelay >= TimeSpan.FromSeconds(2.99));
    }

    [Fact]
    public async Task DrainsMoreThanTwentyTitlesAcrossBoundedBatchesWithoutRepeatingFailures()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var provider = new FixtureProvider("bulk", 221)
        {
            OnDetail = (item, _) => item.ExternalId == "000"
                ? Task.FromException(new InvalidDataException("Broken fixture title.")) : Task.CompletedTask
        };
        var result = await new CatalogCrawler(database, [new(provider)], new()
        {
            MinimumTitleInterval = TimeSpan.Zero,
            RetryFailures = true
        }).RunAsync(CancellationToken.None);

        Assert.Equal("partial", result.Status);
        Assert.Equal(220, result.TitlesUpdated);
        Assert.Equal(1, result.Failures);
        Assert.Equal(221, provider.Starts.Count);
        Assert.Equal(221, provider.Starts.Select(start => start.Id).Distinct().Count());
        Assert.Equal(1, Assert.Single(new CatalogCrawlerStore(database).Due("bulk", DateTimeOffset.UtcNow, 100, retryFailures: true)).Failures);
        var failure = Assert.Single(new ServerDiagnosticsStore(database).ReadLogs("bulk", null, null, 100).Items);
        Assert.Equal("detail", failure.Stage);
        Assert.Contains("Broken fixture title.", failure.Message);
    }

    [Fact]
    public async Task TitleStartsAreAtLeastTwoSecondsApartAndNeverOverlapWithinProvider()
    {
        using var scope = TestDirectory.Create();
        var provider = new FixtureProvider("paced", 3);
        Assert.Equal(TimeSpan.FromSeconds(2), new CatalogCrawlerOptions().MinimumTitleInterval);
        var result = await new CatalogCrawler(new(scope.Path), [new(provider)], new()).RunAsync(CancellationToken.None);

        Assert.Equal(3, result.TitlesUpdated);
        for (var index = 1; index < provider.Starts.Count; index++)
            Assert.True(Stopwatch.GetElapsedTime(provider.Starts[index - 1].Timestamp, provider.Starts[index].Timestamp)
                >= TimeSpan.FromSeconds(1.99), "A provider started titles faster than its two-second cadence.");
        Assert.Equal(1, provider.MaximumConcurrentDetails);
    }

    [Fact]
    public async Task SlowProviderDoesNotPreventOtherProvidersFromAdvancing()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FixtureProvider("slow", 2)
        {
            OnDetail = async (_, token) => { slowStarted.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        var fast = new FixtureProvider("fast", 2)
        {
            OnDetail = (_, _) => { fastStarted.TrySetResult(); return Task.CompletedTask; }
        };
        var running = new CatalogCrawler(database, [new(slow), new(fast)], new()
        {
            MinimumTitleInterval = TimeSpan.Zero
        }).RunAsync(CancellationToken.None);
        try
        {
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fastStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
            Assert.Equal(2, fast.Starts.Count);
        }
        finally { release.TrySetResult(); }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, result.TitlesUpdated);
        Assert.Equal(0, result.Failures);
        var diagnostics = new ServerDiagnosticsStore(database);
        Assert.Empty(diagnostics.ReadLogs(null, null, null, 100).Items);
        var snapshot = diagnostics.Snapshot([slow.Descriptor, fast.Descriptor], new HashSet<string>(),
            new(DateTimeOffset.UtcNow, 0, 0, 0, 0));
        foreach (var id in new[] { "slow", "fast" })
            Assert.Equal(2, Assert.Single(snapshot.Providers, provider => provider.Id == id).Loaded);
    }

    [Fact]
    public async Task TimeBudgetCancelsCadenceWaitAndNextRunResumesRemainingTitle()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var provider = new FixtureProvider("budget", 2);
        var first = await new CatalogCrawler(database, [new(provider)], new()
        {
            MaximumRunTime = TimeSpan.FromSeconds(1)
        }).RunAsync(CancellationToken.None);
        Assert.Equal("budget_exhausted", first.Status);
        Assert.Equal(1, first.TitlesUpdated);
        Assert.Single(provider.Starts);
        Assert.Single(new CatalogCrawlerStore(database).Due("budget", DateTimeOffset.UtcNow, 100));
        var next = await new CatalogCrawler(database, [new(provider)], new()
        {
            MinimumTitleInterval = TimeSpan.Zero
        }).RunAsync(CancellationToken.None);
        Assert.Equal("completed", next.Status);
        Assert.Equal(1, next.TitlesUpdated);
        Assert.Equal(2, provider.Starts.Select(start => start.Id).Distinct().Count());
        Assert.Empty(new CatalogCrawlerStore(database).Due("budget", DateTimeOffset.UtcNow, 100));
    }

    [Fact]
    public async Task ThrottlingStopsOnlyThatProviderAndPreservesRetryAfter()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var throttled = new FixtureProvider("throttled", 3)
        {
            OnDetail = (_, _) => Task.FromException(new CatalogProviderThrottledException(TimeSpan.FromHours(3)))
        };
        var healthy = new FixtureProvider("healthy", 3);
        var result = await new CatalogCrawler(database, [new(throttled), new(healthy)], new()
        {
            MinimumTitleInterval = TimeSpan.Zero,
            RetryFailures = true
        }).RunAsync(CancellationToken.None);
        Assert.Equal("partial", result.Status);
        Assert.Equal(3, result.TitlesUpdated);
        Assert.Single(throttled.Starts);
        Assert.Equal(3, healthy.Starts.Count);
        var state = new CatalogCrawlerStore(database).State("throttled");
        Assert.Equal("throttled", state.FailureKind);
        Assert.True(state.RetryAt > DateTimeOffset.UtcNow.AddHours(2));
    }

    private sealed class FixtureProvider(string id, int count) : IApiCatalogProvider
    {
        public ApiContentProviderDescriptor Descriptor => new(id, id, "https://provider.example/");
        public string CatalogDiscoveryDescription => "Fixture";
        public Func<ApiContentSearchItem, CancellationToken, Task>? OnDetail { get; init; }
        public List<(string Id, long Timestamp)> Starts { get; } = [];
        public int MaximumConcurrentDetails { get; private set; }
        private int _active;

        public ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ApiContentSearchPage(Enumerable.Range(0, count).Select(index => new ApiContentSearchItem(
                index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), new CatalogTransferItem
                {
                    Name = $"{id} fixture {index}",
                    MediaType = "book",
                    Subtype = "manga",
                    Units = [new() { Kind = "chapter", DisplayNumber = "1" }]
                })), null));

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(Uri baseUri, string query, MediaType? mediaType = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async ValueTask<ApiContentItem> LoadItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default)
        {
            Starts.Add((item.ExternalId, Stopwatch.GetTimestamp()));
            MaximumConcurrentDetails = Math.Max(MaximumConcurrentDetails, ++_active);
            try
            {
                if (OnDetail is not null) await OnDetail(item, cancellationToken);
                return new(item.PreviewItem);
            }
            finally { _active--; }
        }
    }
}
