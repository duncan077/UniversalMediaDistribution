using System.Net;
using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;
using UniversalMediaDistribution.UI.Filtering;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ContentFilterCoordinatorTests : IDisposable
{
    private const string Seed = "||seed-ads.example^";
    private const string Document = "https://player.video-host.example/embed";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"umd-coordinator-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    /// <summary>
    /// Every coordinator under test gets its own engine. The production
    /// default is the shared one behind the embedded player, and a test that
    /// swapped its ruleset would quietly change what every other test sees.
    /// </summary>
    private ContentFilterCoordinator Create(
        ContentFilterEngine engine,
        IContentFilterSettingsStore? settings = null,
        bool withStorage = true,
        bool canRefresh = true,
        FakeHandler? handler = null) =>
        new(
            DeviceTier.Standard,
            settings ?? new InMemoryContentFilterSettingsStore(),
            withStorage ? _directory : null,
            new HttpClient(handler ?? new FakeHandler()),
            () => canRefresh,
            engine: engine,
            seedRuleset: Seed);

    private static FilterAction Act(ContentFilterEngine engine, string url) =>
        engine.Match(new FilterRequest(
            url.AsSpan(),
            Document.AsSpan(),
            ResourceType.Script)).Action;

    [Fact]
    public async Task StartupAppliesTheSeedWhenNothingHasBeenDownloaded()
    {
        var engine = new ContentFilterEngine();
        var coordinator = Create(engine);

        await coordinator.InitializeAsync();

        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://seed-ads.example/x.js"));
    }

    [Fact]
    public async Task AStoredPreferenceIsAdoptedOnStartup()
    {
        var settings = new InMemoryContentFilterSettingsStore();
        await settings.SaveAsync(new ContentFilterSettings
        {
            Mode = FilteringMode.Off
        });
        var engine = new ContentFilterEngine();

        var coordinator = Create(engine, settings);
        await coordinator.InitializeAsync();

        Assert.Equal(FilteringMode.Off, coordinator.Mode);
        Assert.Equal(
            FilterAction.Allow,
            Act(engine, "https://seed-ads.example/x.js"));
    }

    [Fact]
    public async Task ChangingTheModeIsPersistedAndApplied()
    {
        var settings = new InMemoryContentFilterSettingsStore();
        var engine = new ContentFilterEngine();
        var coordinator = Create(engine, settings);
        await coordinator.InitializeAsync();

        await coordinator.SetModeAsync(FilteringMode.Off);

        Assert.Equal(FilteringMode.Off, (await settings.LoadAsync()).Mode);
        Assert.Equal(
            FilterAction.Allow,
            Act(engine, "https://seed-ads.example/x.js"));
    }

    [Fact]
    public async Task ARefreshDownloadsAndAppliesTheSubscribedLists()
    {
        var engine = new ContentFilterEngine();
        var handler = new FakeHandler { Body = "||downloaded-ads.example^" };
        var coordinator = Create(engine, handler: handler);
        await coordinator.InitializeAsync();

        var outcome = await coordinator.RefreshAsync(force: true);

        Assert.Equal(ContentFilterRefreshStatus.Completed, outcome.Status);
        Assert.True(handler.Requests > 0);
        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://downloaded-ads.example/x.js"));
        // The seed is never dropped in favour of what was downloaded.
        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://seed-ads.example/x.js"));
        Assert.NotNull(coordinator.LastRefreshUtc);
    }

    [Fact]
    public async Task ARefreshIsPostponedWhenTheHostSaysTheMomentIsWrong()
    {
        var engine = new ContentFilterEngine();
        var handler = new FakeHandler { Body = "||downloaded-ads.example^" };
        var coordinator = Create(engine, canRefresh: false, handler: handler);
        await coordinator.InitializeAsync();

        var outcome = await coordinator.RefreshAsync(force: true);

        // Nothing was fetched, and nothing was lost: the seed still applies.
        Assert.Equal(ContentFilterRefreshStatus.Deferred, outcome.Status);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://seed-ads.example/x.js"));
    }

    [Fact]
    public async Task TheBuiltInLevelNeverReachesTheNetwork()
    {
        var engine = new ContentFilterEngine();
        var handler = new FakeHandler { Body = "||downloaded-ads.example^" };
        var coordinator = Create(engine, handler: handler);
        await coordinator.InitializeAsync();

        var outcome = await coordinator.SetModeAsync(FilteringMode.Basic);

        Assert.Equal(ContentFilterRefreshStatus.NotSubscribed, outcome.Status);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://seed-ads.example/x.js"));
    }

    [Fact]
    public async Task AHostWithNoStorageStillGetsTheSeed()
    {
        var engine = new ContentFilterEngine();
        var coordinator = Create(engine, withStorage: false);
        await coordinator.InitializeAsync();

        Assert.False(coordinator.CanDownload);
        Assert.Equal(
            ContentFilterRefreshStatus.NoStorage,
            (await coordinator.RefreshAsync()).Status);
        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://seed-ads.example/x.js"));
    }

    [Fact]
    public async Task ADownloadFailureLeavesThePreviousRulesInForce()
    {
        var engine = new ContentFilterEngine();
        var handler = new FakeHandler { Status = HttpStatusCode.NotFound };
        var coordinator = Create(engine, handler: handler);
        await coordinator.InitializeAsync();

        var outcome = await coordinator.RefreshAsync(force: true);

        Assert.Equal(ContentFilterRefreshStatus.Completed, outcome.Status);
        Assert.True(outcome.ListsFailed > 0);
        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://seed-ads.example/x.js"));
    }

    [Fact]
    public async Task ACompiledRulesetIsReusedByTheNextRun()
    {
        var handler = new FakeHandler { Body = "||downloaded-ads.example^" };
        var first = Create(new ContentFilterEngine(), handler: handler);
        await first.InitializeAsync();
        await first.RefreshAsync(force: true);

        var engine = new ContentFilterEngine();
        var second = Create(engine, handler: new FakeHandler());
        await second.InitializeAsync();

        // Startup is a file read and a header check, not a parse, and it goes
        // nowhere near the network.
        Assert.Equal(
            FilterAction.Block,
            Act(engine, "https://downloaded-ads.example/x.js"));
    }

    [Fact]
    public void AConstrainedDeviceCapsTheRequestedLevel()
    {
        var coordinator = new ContentFilterCoordinator(
            DeviceTier.Constrained,
            new InMemoryContentFilterSettingsStore(),
            seedRuleset: Seed,
            engine: new ContentFilterEngine());

        Assert.Equal(DeviceTier.Constrained, coordinator.Tier);
        Assert.Equal(FilteringMode.Standard, coordinator.EffectiveMode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public string Body { get; set; } = string.Empty;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(Status);
            if (Status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(Body);
            }

            return Task.FromResult(response);
        }
    }
}
