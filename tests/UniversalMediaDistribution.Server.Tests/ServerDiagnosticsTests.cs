using System.Net;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class ServerDiagnosticsTests
{
    [Fact]
    public void ValidationFailuresKeepFieldRulesAndTitleWithoutLeakingUrls()
    {
        var exception = new CatalogTransferValidationException(
        [
            "items[0].units[0].displayNumber is required.",
            "items[0].releaseYear is invalid: https://provider.example/private?key=secret-rule",
            "items[0].seasons contains duplicate season number 1.",
            "fourth rule"
        ]);
        var message = ServerDiagnosticsStore.DescribeFailure(exception,
            "Fixture https://provider.example/private?key=secret-title\r\nTitle");
        Assert.Contains("Fixture", message);
        Assert.Contains("items[0].units[0].displayNumber is required.", message);
        Assert.Contains("duplicate season number 1", message);
        Assert.Contains("1 more", message);
        Assert.DoesNotContain("fourth rule", message);
        Assert.DoesNotContain("https://", message);
        Assert.DoesNotContain("secret-", message);
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain('\n', message);
        Assert.InRange(message.Length, 1, 1024);
        Assert.InRange(ServerDiagnosticsStore.DescribeFailure(
            new CatalogTransferValidationException([new string('x', 5000)]), new string('y', 5000)).Length, 1, 1024);
    }

    [Fact]
    public async Task CrawlerPersistsRejectedTitleAndActualValidationRule()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var reports = new List<string>();
        var result = await new CatalogCrawler(database, [new(new FailingProvider("validation"))],
            new() { MinimumTitleInterval = TimeSpan.Zero }, reports.Add).RunAsync(CancellationToken.None);
        Assert.True(result.Failures > 0);
        var store = new ServerDiagnosticsStore(database);
        var failure = Assert.Single(store.ReadLogs("fake", "error", null, 20).Items, item => item.Stage == "detail");
        Assert.Contains("Fixture manga", failure.Message);
        Assert.Contains("items[0].units[0].displayNumber is required.", failure.Message);
        Assert.Contains(reports, report => report.Contains(failure.Message, StringComparison.Ordinal));
        var snapshot = store.Snapshot([new ApiContentProviderDescriptor("fake", "Fake")],
            new HashSet<string>(), new(DateTimeOffset.UtcNow, 0, 0, 0, 0));
        Assert.Equal(0, snapshot.Catalog.Titles);
        Assert.Equal(1, Assert.Single(snapshot.Providers).FailedTitles);
    }

    [Fact]
    public void LogsSurviveRestartAndFilterAndPageWithoutRepeatingEvents()
    {
        using var scope = TestDirectory.Create();
        var store = new ServerDiagnosticsStore(new ServerDatabase(scope.Path));
        store.Write("info", "server", "lifecycle", "started", "Started");
        store.Write("error", "crawler", "updates", "parse_or_storage_error", "Missing section", "olympusxyz", "run-1");
        store.Write("error", "crawler", "discovery", "network", "HTTP 520", "doramasflix", "run-1", 520);
        store.Write("warning", "crawler", "provider", "partial", "Partial", "olympusxyz", "run-1");
        store.Write("error", "http", "request", "http_error", "HTTP 500");

        store = new ServerDiagnosticsStore(new ServerDatabase(scope.Path));
        var first = store.ReadLogs(null, null, null, 2);
        var second = store.ReadLogs(null, null, first.Before, 2);
        Assert.NotNull(first.Before);
        Assert.Null(second.Before);
        Assert.Equal(3, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());
        Assert.Equal(["http_error", "network", "parse_or_storage_error"],
            first.Items.Concat(second.Items).Select(item => item.Status));
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal("error", item.Level));
        Assert.Empty(store.ReadLogs(null, "info", null, 10).Items);
        Assert.Empty(store.ReadLogs(null, "warning", null, 10).Items);
        var error = Assert.Single(store.ReadLogs("olympusxyz", "error", null, 10).Items);
        Assert.Equal("updates", error.Stage);
        Assert.Equal("run-1", error.RunId);
        Assert.Equal(520, Assert.Single(store.ReadLogs("doramasflix", "error", null, 10).Items).HttpStatus);
        Assert.Empty(store.ReadLogs("missing", null, null, 10).Items);
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(201, null, null)]
    [InlineData(10, 0L, null)]
    [InlineData(10, null, "invalid")]
    public void RejectsUnboundedLogRequests(int limit, long? before, string? level)
    {
        using var scope = TestDirectory.Create();
        var store = new ServerDiagnosticsStore(new ServerDatabase(scope.Path));
        Assert.Throws<ArgumentException>(() => store.ReadLogs(null, level, before, limit));
    }

    [Fact]
    public void LogRetentionIsBoundedAndFetchedUrlsAreRedacted()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n < $count)
            INSERT INTO server_events(occurred_at,level,source,stage,status,message)
            SELECT $now,'error','crawler','detail','failed','Fixture error' FROM numbers;
            """;
        command.Parameters.AddWithValue("$count", ServerDiagnosticsStore.MaximumEvents + 10);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        var store = new ServerDiagnosticsStore(database);
        store.Write("error", "server", "request", "old", "Expired", occurredAt: DateTimeOffset.UtcNow.AddHours(-25));
        store.Write("error", "crawler", "updates", "parse_or_storage_error",
            "Could not read https://provider.example/secret?access_token=do-not-store\r\nNext line", "fake");
        command.CommandText = "SELECT COUNT(*) FROM server_events;";
        Assert.InRange((long)command.ExecuteScalar()!, 1, ServerDiagnosticsStore.MaximumEvents);
        command.CommandText = "SELECT COUNT(*) FROM server_events WHERE status='old';";
        Assert.Equal(0L, command.ExecuteScalar());
        var entry = Assert.Single(store.ReadLogs("fake", null, null, 10).Items);
        Assert.DoesNotContain("do-not-store", entry.Message);
        Assert.DoesNotContain("https://", entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
    }

    [Fact]
    public void SnapshotIncludesTitleFailuresAndDetectsInterruptedRunsWithoutChangingState()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var crawl = new CatalogCrawlerStore(database);
        var now = DateTimeOffset.UtcNow;
        crawl.State("fake");
        var item = new ApiContentSearchItem("one", Title);
        crawl.Enqueue("fake", [item, new("two", Title)], now, updates: false, discovery: true);
        var saved = new SqliteOnlineCatalogStore(scope.Path).MergeScraped("fake", "one", Title, CancellationToken.None);
        crawl.TitleSucceeded("fake", "one", saved.ItemId, now);
        crawl.TitleFailed("fake", new("two", item, 0), "network", now);
        crawl.ProviderAttempted("fake", now);
        crawl.ProviderSucceeded("fake", now);
        crawl.StartRun("active", now);
        Assert.True(crawl.TryAcquireLease("active", now, TimeSpan.FromMinutes(1)));
        var store = new ServerDiagnosticsStore(new ServerDatabase(scope.Path));
        var descriptors = new[]
        {
            new ApiContentProviderDescriptor("fake", "Fixture", "https://provider.example/"),
            new ApiContentProviderDescriptor("disabled", "Disabled"),
            new ApiContentProviderDescriptor("missing", "Not started")
        };
        var runtime = new RuntimeDiagnostic(now, 0, 0, 0, 0);
        var snapshot = store.Snapshot(descriptors, new HashSet<string> { "disabled" }, runtime);
        Assert.Equal(1, snapshot.Catalog.Titles);
        Assert.True(snapshot.CrawlerRunning);
        var provider = Assert.Single(snapshot.Providers, provider => provider.Id == "fake");
        Assert.Equal("attention", provider.Status);
        Assert.Equal(2, provider.Discovered);
        Assert.Equal(1, provider.Loaded);
        Assert.Equal(1, provider.FailedTitles);
        Assert.True(provider.BackfillComplete);
        Assert.Equal("disabled", Assert.Single(snapshot.Providers, provider => provider.Id == "disabled").Status);
        Assert.Equal("not_run", Assert.Single(snapshot.Providers, provider => provider.Id == "missing").Status);
        Assert.Equal("running", Assert.Single(snapshot.Runs).Status);
        crawl.ReleaseLease("active");
        snapshot = store.Snapshot(descriptors, new HashSet<string>(), runtime);
        Assert.False(snapshot.CrawlerRunning);
        Assert.Equal("interrupted", Assert.Single(snapshot.Runs).Status);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM crawler_providers;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData("updates")]
    [InlineData("discovery")]
    [InlineData("detail")]
    public async Task CrawlerPersistsFailedStageAndHttpStatusForDashboard(string stage)
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var result = await new CatalogCrawler(database, [new(new FailingProvider(stage))], new()).RunAsync(CancellationToken.None);
        Assert.True(result.Failures > 0);
        var store = new ServerDiagnosticsStore(new ServerDatabase(scope.Path));
        var failure = Assert.Single(store.ReadLogs("fake", "error", null, 20).Items, item => item.Stage == stage);
        Assert.Equal(502, failure.HttpStatus);
        Assert.False(string.IsNullOrWhiteSpace(failure.RunId));
        Assert.DoesNotContain("secret-token", failure.Message);
        Assert.All(store.ReadLogs(null, null, null, 20).Items, item => Assert.Equal("error", item.Level));
        var snapshot = store.Snapshot([new ApiContentProviderDescriptor("fake", "Fake")],
            new HashSet<string>(), new(DateTimeOffset.UtcNow, 0, 0, 0, 0));
        Assert.Equal("partial", Assert.Single(snapshot.Runs).Status);
        var json = JsonSerializer.Serialize(snapshot,
            ServerJsonSerializerContext.Default.ServerDiagnosticSnapshot);
        Assert.Contains("\"failedTitles\":", json);
        Assert.Contains("\"providers\":", json);
    }

    private static readonly CatalogTransferItem Title = new()
    {
        MediaType = "book",
        Subtype = "manga",
        Name = "Fixture manga",
        Units = [new() { Kind = "chapter", DisplayNumber = "1" }]
    };

    private sealed class FailingProvider(string stage) : IApiCatalogProvider
    {
        public ApiContentProviderDescriptor Descriptor => new("fake", "Fake", "https://provider.example/");
        public bool SupportsDailyUpdates => true;
        public string CatalogDiscoveryDescription => "Fixture";
        private void Fail(string current)
        {
            if (current == stage) throw new HttpRequestException("https://provider.example/?key=secret-token", null, HttpStatusCode.BadGateway);
        }
        public ValueTask<IReadOnlyList<ApiContentSearchItem>> LoadDailyUpdatesAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            Fail("updates");
            return ValueTask.FromResult<IReadOnlyList<ApiContentSearchItem>>([new("one", Title)]);
        }
        public ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
        {
            Fail("discovery");
            return ValueTask.FromResult(new ApiContentSearchPage([new("one", Title)], null));
        }
        public ValueTask<CatalogTransferItem> LoadCatalogItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default)
        {
            Fail("detail");
            if (stage == "validation")
                return ValueTask.FromResult(Title with { Units = [new() { Kind = "chapter", DisplayNumber = "" }] });
            return ValueTask.FromResult(Title);
        }
        public ValueTask<ApiContentItem> LoadItemAsync(Uri baseUri, ApiContentSearchItem searchItem, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ApiContentItem(Title));
        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(Uri baseUri, string query, MediaType? mediaType = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ApiContentSearchItem>>([]);
    }
}
