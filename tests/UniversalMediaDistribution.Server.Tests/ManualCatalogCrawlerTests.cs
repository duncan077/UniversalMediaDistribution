using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class ManualCatalogCrawlerTests
{
    [Theory]
    [InlineData("one", 1)]
    [InlineData(null, 2)]
    public async Task RunsRequestedProvidersAndPersistsHistory(string? selected, int count)
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var one = new FixtureProvider("one");
        var two = new FixtureProvider("two");
        using var service = new ManualCatalogCrawler(database, [new(one), new(two)], () => new());

        var response = service.Start(new(selected));
        Assert.Equal("accepted", response.Status);
        Assert.NotNull(response.RunId);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(count, one.Details.Count + two.Details.Count);
        if (selected is not null) Assert.Empty(two.Details);
        Assert.Equal(count, new SqliteOnlineCatalogStore(scope.Path).Page(null, 10, CancellationToken.None).Document.Items.Count);
        var run = Assert.Single(Snapshot(database).Runs);
        Assert.Equal(response.RunId, run.Id);
        Assert.Equal("completed", run.Status);
        Assert.Equal(count, run.TitlesUpdated);
        Assert.False(Snapshot(database).CrawlerRunning);
    }

    [Fact]
    public async Task SharedLeasePreventsManualAndCronOverlapAndShutdownCancelsRun()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FixtureProvider("one") { Entered = entered, Release = release.Task };
        using var service = new ManualCatalogCrawler(database, [new(provider)], () => new());
        using var otherService = new ManualCatalogCrawler(database, [new(provider)], () => new());

        var accepted = service.Start(new("one"));
        Assert.Equal("accepted", accepted.Status);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Snapshot(database).CrawlerRunning);
        Assert.Equal("already_running", service.Start(new()).Status);
        Assert.Equal("already_running", otherService.Start(new()).Status);
        Assert.Equal("already_running", (await new CatalogCrawler(database, [new(provider)], new()).RunAsync(CancellationToken.None)).Status);

        await service.StopAsync(CancellationToken.None);
        Assert.Equal("cancelled", Assert.Single(Snapshot(database).Runs).Status);
        Assert.False(Snapshot(database).CrawlerRunning);
        Assert.Equal("stopping", service.Start(new()).Status);
        var store = new CatalogCrawlerStore(database);
        Assert.True(store.TryAcquireLease("cron", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.Equal("already_running", otherService.Start(new()).Status);
        store.ReleaseLease("cron");
    }

    [Fact]
    public async Task RejectsUnknownDisabledAndUnconfiguredProvidersWithoutStarting()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var enabled = new FixtureProvider("enabled");
        var disabled = new FixtureProvider("disabled");
        var unconfigured = new FixtureProvider("unconfigured") { BaseUrl = "" };
        using var service = new ManualCatalogCrawler(database, [new(enabled), new(disabled), new(unconfigured)],
            () => new() { DisabledProviders = new HashSet<string> { "disabled" } });
        Assert.Equal("invalid_provider", service.Start(new("missing")).Status);
        Assert.Equal("disabled_provider", service.Start(new("disabled")).Status);
        Assert.Equal("configuration_required", service.Start(new("unconfigured")).Status);
        Assert.Empty(Snapshot(database).Runs);
        Assert.Equal("accepted", service.Start(new()).Status);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(enabled.Details);
        Assert.Empty(disabled.Details);
        Assert.Empty(unconfigured.Details);
    }

    [Fact]
    public void InvalidServerConfigurationIsReportedWithoutAcquiringLease()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        using var service = new ManualCatalogCrawler(database, [new(new FixtureProvider("one"))],
            () => throw new ArgumentException("Invalid crawler configuration."));
        Assert.Equal("invalid_configuration", service.Start(new()).Status);
        Assert.False(Snapshot(database).CrawlerRunning);
        Assert.Empty(Snapshot(database).Runs);
    }

    [Fact]
    public async Task ExplicitRetryBypassesOrdinaryFailuresButNotProviderOrTitleThrottling()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var store = new CatalogCrawlerStore(database);
        var normal = new FixtureProvider("normal");
        var throttled = new FixtureProvider("throttled");
        var now = DateTimeOffset.UtcNow;
        foreach (var provider in new[] { normal, throttled })
        {
            store.State(provider.Descriptor.Id);
            var entry = provider.Entry;
            store.Enqueue(provider.Descriptor.Id, [entry], now, updates: false, discovery: true);
            store.TitleFailed(provider.Descriptor.Id, new(entry.ExternalId, entry, 0), "network", now);
            store.ProviderFailed(provider.Descriptor.Id, provider == normal ? "network" : "throttled", 0, now);
        }
        var limitedTitle = new ApiContentSearchItem("limited", normal.Entry.PreviewItem);
        store.Enqueue("normal", [limitedTitle], now, updates: true);
        store.TitleFailed("normal", new("limited", limitedTitle, 0), "throttled", now);
        using var service = new ManualCatalogCrawler(database, [new(normal), new(throttled)], () => new());

        Assert.Equal("accepted", service.Start(new()).Status);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(normal.Details);
        Assert.Empty(throttled.Details);
        Assert.Equal("accepted", service.Start(new(RetryFailures: true)).Status);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("one", Assert.Single(normal.Details));
        Assert.Empty(throttled.Details);
        Assert.Empty(store.Due("normal", DateTimeOffset.UtcNow, 10, retryFailures: true));
        Assert.Equal("throttled", store.State("throttled").FailureKind);
    }

    private static ServerDiagnosticSnapshot Snapshot(ServerDatabase database) =>
        new ServerDiagnosticsStore(database).Snapshot([], new HashSet<string>(), new(DateTimeOffset.UtcNow, 0, 0, 0, 0));

    private sealed class FixtureProvider(string id) : IApiCatalogProvider
    {
        public string BaseUrl { get; init; } = "https://provider.example/";
        public ApiContentProviderDescriptor Descriptor => new(id, id, BaseUrl);
        public string CatalogDiscoveryDescription => "Fixture";
        public TaskCompletionSource? Entered { get; init; }
        public Task? Release { get; init; }
        public List<string> Details { get; } = [];
        public ApiContentSearchItem Entry => new("one", new CatalogTransferItem
        {
            Name = id + " fixture",
            MediaType = "book",
            Subtype = "manga",
            Units = [new() { Kind = "chapter", DisplayNumber = "1" }]
        });
        public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
        {
            Entered?.TrySetResult();
            if (Release is not null) await Release.WaitAsync(cancellationToken);
            return new([Entry], null);
        }
        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(Uri baseUri, string query, MediaType? mediaType = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ApiContentItem> LoadItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default)
        {
            Details.Add(item.ExternalId);
            return ValueTask.FromResult(new ApiContentItem(item.PreviewItem));
        }
    }
}
