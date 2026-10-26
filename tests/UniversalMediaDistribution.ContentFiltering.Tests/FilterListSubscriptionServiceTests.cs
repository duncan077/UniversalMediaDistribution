using System.Net;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

public sealed class FilterListSubscriptionServiceTests : IDisposable
{
    private const string Seed = "||seed-ads.example^";
    private const string Document = "https://player.video-host.example/embed";

    private static readonly Uri ListUrl =
        new("https://lists.example/easylist.txt");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"umd-filters-{Guid.NewGuid():N}");

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

    private static IReadOnlyList<FilterListSubscription> Catalog { get; } =
    [
        new("test-list", "Test list", ListUrl, FilteringMode.Standard)
    ];

    private (FilterListSubscriptionService Service, ContentFilterEngine Engine,
        FakeHandler Handler) Create(
        FilteringMode mode = FilteringMode.Standard,
        DeviceTier tier = DeviceTier.Standard,
        FakeHandler? handler = null,
        FakeClock? clock = null)
    {
        var engine = new ContentFilterEngine();
        var store = new FilterRulesetStore(_directory);
        handler ??= new FakeHandler();
        var service = new FilterListSubscriptionService(
            engine,
            store,
            new HttpClient(handler),
            new FilterSubscriptionOptions
            {
                Mode = mode,
                Tier = tier,
                Catalog = Catalog,
                SeedRuleset = Seed
            },
            clock ?? new FakeClock());
        return (service, engine, handler);
    }

    [Fact]
    public async Task AFirstRefreshDownloadsCompilesAndApplies()
    {
        var (service, engine, handler) = Create();
        handler.Body = "||downloaded-ads.example^";

        var result = await service.RefreshAsync();

        Assert.True(result.RulesetChanged);
        Assert.Equal(1, result.ListsFetched);
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://downloaded-ads.example/x.js", Document));
        // The seed is always compiled alongside whatever was downloaded.
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://seed-ads.example/x.js", Document));
    }

    [Fact]
    public async Task ASecondRefreshInsideTheIntervalDoesNotFetchAgain()
    {
        var (service, _, handler) = Create();
        handler.Body = "||downloaded-ads.example^";

        await service.RefreshAsync();
        var result = await service.RefreshAsync();

        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, result.ListsUnchanged);
        Assert.False(result.RulesetChanged);
    }

    [Fact]
    public async Task RefreshedRulesStayFileBackedAndReplaceThePreviousGeneration()
    {
        var clock = new FakeClock();
        var (service, engine, handler) = Create(clock: clock);
        handler.Body = "||first-ads.example^";
        await service.RefreshAsync();
        var previous = engine.Ruleset;
        Assert.True(previous.IsMemoryMapped);

        clock.Advance(TimeSpan.FromDays(30));
        handler.Body = "||replacement-ads.example^";
        await service.RefreshAsync();

        Assert.True(engine.Ruleset.IsMemoryMapped);
        Assert.NotSame(previous, engine.Ruleset);
        Assert.Equal(FilterAction.Allow, engine.Act("https://first-ads.example/x.js", Document));
        Assert.Equal(FilterAction.Block, engine.Act("https://replacement-ads.example/x.js", Document));
        Assert.Equal(FilterAction.Block, engine.Act("https://seed-ads.example/x.js", Document));
    }

    [Fact]
    public async Task ANotModifiedResponseKeepsTheCachedListAndSkipsCompiling()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler
        {
            Body = "||downloaded-ads.example^",
            ETag = "\"v1\""
        };
        var (service, engine, _) = Create(handler: handler, clock: clock);

        await service.RefreshAsync();
        clock.Advance(TimeSpan.FromDays(30));
        handler.Status = HttpStatusCode.NotModified;

        var result = await service.RefreshAsync();

        Assert.Equal("\"v1\"", handler.LastIfNoneMatch);
        Assert.False(result.RulesetChanged);
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://downloaded-ads.example/x.js", Document));
    }

    [Fact]
    public async Task AFailedListLeavesThePreviousRulesetInPlace()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler { Body = "||downloaded-ads.example^" };
        var (service, engine, _) = Create(handler: handler, clock: clock);

        await service.RefreshAsync();
        clock.Advance(TimeSpan.FromDays(30));
        handler.Status = HttpStatusCode.InternalServerError;

        var result = await service.RefreshAsync();

        Assert.Equal(1, result.ListsFailed);
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://downloaded-ads.example/x.js", Document));
    }

    [Fact]
    public async Task ANetworkFailureOnAFirstRunStillLeavesTheSeedUsable()
    {
        var handler = new FakeHandler { Throw = true };
        var (service, engine, _) = Create(handler: handler);

        service.ApplySeedOnly();
        var result = await service.RefreshAsync();

        Assert.Equal(1, result.ListsFailed);
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://seed-ads.example/x.js", Document));
    }

    [Fact]
    public async Task AnOversizedResponseIsRefused()
    {
        var handler = new FakeHandler
        {
            Body = new string('x', 1024) + "\n||downloaded-ads.example^"
        };
        var engine = new ContentFilterEngine();
        var service = new FilterListSubscriptionService(
            engine,
            new FilterRulesetStore(_directory),
            new HttpClient(handler),
            new FilterSubscriptionOptions
            {
                Catalog = Catalog,
                SeedRuleset = Seed,
                MaximumListBytes = 64
            });

        var result = await service.RefreshAsync();

        Assert.Equal(1, result.ListsFailed);
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://downloaded-ads.example/x.js", Document));
    }

    [Fact]
    public async Task ACachedRulesetIsAdoptedWithoutReparsing()
    {
        var (service, _, handler) = Create();
        handler.Body = "||downloaded-ads.example^";
        await service.RefreshAsync();

        var (second, engine, secondHandler) = Create();

        Assert.True(second.TryApplyCached());
        Assert.Equal(0, secondHandler.Requests);
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://downloaded-ads.example/x.js", Document));
    }

    [Fact]
    public void NothingCachedMeansNothingToAdopt()
    {
        var (service, _, _) = Create();

        Assert.False(service.TryApplyCached());
    }

    [Fact]
    public async Task BasicModeCompilesTheSeedAndNeverReachesTheNetwork()
    {
        var (service, engine, handler) = Create(FilteringMode.Basic);

        var result = await service.RefreshAsync();

        Assert.Equal(0, handler.Requests);
        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://seed-ads.example/x.js", Document));
        Assert.True(result.RulesetChanged);
    }

    [Fact]
    public async Task OffModeFiltersNothing()
    {
        var (service, engine, handler) = Create(FilteringMode.Off);

        await service.RefreshAsync();

        Assert.Equal(0, handler.Requests);
        Assert.Equal(
            FilterAction.Allow,
            engine.Act("https://seed-ads.example/x.js", Document));
    }

    [Fact]
    public void AConstrainedDeviceCannotBeAskedForMoreThanItCanCarry()
    {
        var (service, _, _) = Create(FilteringMode.Complete, DeviceTier.Constrained);

        Assert.Equal(FilteringMode.Standard, service.EffectiveMode);
    }

    [Fact]
    public async Task RawListsAreCachedCompressedSoAFormatChangeCostsNoDownload()
    {
        var (service, _, handler) = Create();
        handler.Body = "||downloaded-ads.example^";
        await service.RefreshAsync();

        var store = new FilterRulesetStore(_directory);
        var cached = store.ReadList("test-list");

        Assert.Equal("||downloaded-ads.example^", cached);
        var raw = Directory.GetFiles(
            Path.Combine(_directory, "lists"),
            "*.gz");
        Assert.Single(raw);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public string Body { get; set; } = string.Empty;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string? ETag { get; set; }

        public bool Throw { get; set; }

        public int Requests { get; private set; }

        public string? LastIfNoneMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            LastIfNoneMatch = request.Headers.TryGetValues(
                "If-None-Match",
                out var values)
                ? string.Join(',', values)
                : null;

            if (Throw)
            {
                throw new HttpRequestException("offline");
            }

            var response = new HttpResponseMessage(Status);
            if (Status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(Body);
                if (ETag is not null)
                {
                    response.Headers.TryAddWithoutValidation("ETag", ETag);
                }
            }

            return Task.FromResult(response);
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now =
            new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
