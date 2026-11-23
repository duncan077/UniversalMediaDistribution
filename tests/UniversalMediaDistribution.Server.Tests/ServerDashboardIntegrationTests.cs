using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class ServerDashboardIntegrationTests(ITestOutputHelper output)
{
    private const string AdminToken = "isolated-dashboard-test-admin-token-2026";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DashboardAssetsAndDiagnosticsArePackagedAndAdministrativeDataRequiresToken(bool configured)
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var crawler = new CatalogCrawlerStore(database);
        crawler.State("olympusxyz");
        crawler.ProviderAttempted("olympusxyz", DateTimeOffset.UtcNow);
        crawler.ProviderFailed("olympusxyz", "network", 0, DateTimeOffset.UtcNow);
        new ServerDiagnosticsStore(database).Write("error", "crawler", "updates", "network", "HTTP 502.", "olympusxyz", "fixture", 502);
        new SqliteOnlineCatalogStore(scope.Path).MergeScraped("olympusxyz", "one", new CatalogTransferItem
        {
            Name = "Dashboard fixture",
            MediaType = "book",
            Subtype = "manga",
            Units = [new() { Kind = "chapter", DisplayNumber = "1" }]
        }, CancellationToken.None);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var root = new Uri($"http://127.0.0.1:{port}/");
        using var process = StartServer(root, scope.Path, configured);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = root, Timeout = TimeSpan.FromSeconds(5) };
            await Ready(process, client);
            foreach (var path in new[] { "admin", "admin/", "admin/style.css", "admin/app.js" })
            {
                using var asset = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
                Assert.True(asset.Headers.CacheControl?.NoStore);
                Assert.Contains("frame-ancestors 'none'", Assert.Single(asset.Headers.GetValues("Content-Security-Policy")));
                Assert.DoesNotContain(AdminToken, await asset.Content.ReadAsStringAsync());
            }
            foreach (var path in new[] { "api/v1/admin/diagnostics", "api/v1/admin/diagnostics/logs" })
            {
                using var unauthorized = await client.GetAsync(path);
                Assert.Equal(configured ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, unauthorized.StatusCode);
                Assert.DoesNotContain("Dashboard fixture", await unauthorized.Content.ReadAsStringAsync());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ordinary-user-session-token");
                using var wrong = await client.GetAsync(path);
                Assert.Equal(configured ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, wrong.StatusCode);
                client.DefaultRequestHeaders.Authorization = null;
            }
            using var deniedCrawl = await client.PostAsJsonAsync("api/v1/admin/diagnostics/crawl", new { });
            Assert.Equal(configured ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, deniedCrawl.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ordinary-user-session-token");
            using var wrongCrawl = await client.PostAsJsonAsync("api/v1/admin/diagnostics/crawl", new { });
            Assert.Equal(configured ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, wrongCrawl.StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            using var deniedInterval = await client.PutAsJsonAsync("api/v1/admin/diagnostics/provider-interval", new { providerId = "olympusxyz", intervalSeconds = 3 });
            Assert.Equal(configured ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, deniedInterval.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ordinary-user-session-token");
            using var wrongInterval = await client.PutAsJsonAsync("api/v1/admin/diagnostics/provider-interval", new { providerId = "olympusxyz", intervalSeconds = 3 });
            Assert.Equal(configured ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, wrongInterval.StatusCode);
            if (!configured) return;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            using var snapshot = JsonDocument.Parse(await client.GetStringAsync("api/v1/admin/diagnostics"));
            Assert.Equal(1, snapshot.RootElement.GetProperty("catalog").GetProperty("titles").GetInt32());
            Assert.Equal(17, snapshot.RootElement.GetProperty("providers").GetArrayLength());
            var provider = snapshot.RootElement.GetProperty("providers").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "olympusxyz");
            Assert.Equal("backoff", provider.GetProperty("status").GetString());
            using var logs = JsonDocument.Parse(await client.GetStringAsync("api/v1/admin/diagnostics/logs?provider=olympusxyz&level=error"));
            var failure = Assert.Single(logs.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(502, failure.GetProperty("httpStatus").GetInt32());
            Assert.Equal("updates", failure.GetProperty("stage").GetString());
            using var invalid = await client.GetAsync("api/v1/admin/diagnostics/logs?limit=201");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var invalidCrawl = await client.PostAsJsonAsync("api/v1/admin/diagnostics/crawl", new { providerId = "not-a-provider" });
            Assert.Equal(HttpStatusCode.BadRequest, invalidCrawl.StatusCode);
            Assert.True(crawler.TryAcquireLease("cron-fixture", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
            using var busy = await client.PostAsJsonAsync("api/v1/admin/diagnostics/crawl", new { providerId = "olympusxyz" });
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            crawler.ReleaseLease("cron-fixture");
            using var oversized = await client.PostAsJsonAsync("api/v1/admin/diagnostics/crawl", new { providerId = new string('x', 5000) });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            using var intervalUpdate = await client.PutAsJsonAsync("api/v1/admin/diagnostics/provider-interval", new { providerId = "olympusxyz", intervalSeconds = 3 });
            Assert.Equal(HttpStatusCode.OK, intervalUpdate.StatusCode);
            using var updatedSnapshot = JsonDocument.Parse(await client.GetStringAsync("api/v1/admin/diagnostics"));
            var updatedProvider = updatedSnapshot.RootElement.GetProperty("providers").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "olympusxyz");
            Assert.Equal(3, updatedProvider.GetProperty("intervalSeconds").GetInt32());
            foreach (var seconds in new[] { 0, 61 })
            {
                using var invalidInterval = await client.PutAsJsonAsync("api/v1/admin/diagnostics/provider-interval", new { providerId = "olympusxyz", intervalSeconds = seconds });
                Assert.Equal(HttpStatusCode.BadRequest, invalidInterval.StatusCode);
            }
            using var unknownProvider = await client.PutAsJsonAsync("api/v1/admin/diagnostics/provider-interval", new { providerId = "does-not-exist", intervalSeconds = 3 });
            Assert.Equal(HttpStatusCode.BadRequest, unknownProvider.StatusCode);
            using var oversizedInterval = await client.PutAsJsonAsync("api/v1/admin/diagnostics/provider-interval", new { providerId = new string('x', 5000), intervalSeconds = 3 });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedInterval.StatusCode);
            Assert.Equal(3, new CatalogCrawlerSettingsStore(new(scope.Path)).ReadIntervals()["olympusxyz"]);

            // The seeded backoff keeps this HTTP test isolated from upstream sites.
            await RunFromAdmin(client, database, "olympusxyz");
            foreach (var descriptor in snapshot.RootElement.GetProperty("providers").EnumerateArray())
            {
                var id = descriptor.GetProperty("id").GetString()!;
                crawler.State(id);
                crawler.ProviderFailed(id, "throttled", 0, DateTimeOffset.UtcNow);
            }
            await RunFromAdmin(client, database, null);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
        }
    }

    [Fact]
    public async Task PublicCatalogSearchAcceptsFilterOnlyQueriesAndReturnsPagingHeaders()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        await store.SaveAsync(new CatalogTransferDocument
        {
            Items = Enumerable.Range(1, 2).Select(i => new CatalogTransferItem
            {
                Id = Guid.CreateVersion7(),
                Name = $"Filtered manga {i}",
                MediaType = "book",
                Subtype = "manga",
                Categories = ["Manhwa"],
                Genres = ["Acción"],
                PublicationStatus = "completed",
                ReleaseYear = 2026,
                Units = [new() { Kind = "chapter", DisplayNumber = "1" }]
            }).ToArray()
        }, default);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var root = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
        reservation.Stop();
        using var process = StartServer(root, scope.Path, configured: false);
        using var client = new HttpClient { BaseAddress = root, Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await Ready(process, client);
            const string query = "api/v1/catalog/search?category=Manhwa&genre=action&year=2026&status=completed&limit=1&summary=true";
            using var first = await client.GetAsync(query);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal("true", Assert.Single(first.Headers.GetValues("X-UMD-Summary")));
            using var summary = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var item = summary.RootElement.GetProperty("items")[0];
            Assert.False(item.TryGetProperty("units", out var units) && units.GetArrayLength() > 0);
            using var full = await client.GetAsync("api/v1/catalog/items/" + item.GetProperty("syncId").GetString());
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            using var detail = JsonDocument.Parse(await full.Content.ReadAsStringAsync());
            Assert.Equal(1, detail.RootElement.GetProperty("items")[0].GetProperty("units").GetArrayLength());
            var cursor = Assert.Single(first.Headers.GetValues("X-UMD-Next-Cursor"));
            using var next = await client.GetAsync(query + "&cursor=" + cursor);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.False(next.Headers.Contains("X-UMD-Next-Cursor"));
            using var invalid = await client.GetAsync("api/v1/catalog/search?status=not-a-status");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var empty = await client.GetAsync("api/v1/catalog/search?mediaType=book");
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task PublicCatalogSharesAnAnonymousRateLimitPerClientWithoutAffectingPrivateRoutes()
    {
        using var scope = TestDirectory.Create();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var root = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
        reservation.Stop();
        using var process = StartServer(root, scope.Path, configured: true);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var client = new HttpClient { BaseAddress = root, Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await Ready(process, client);
            for (var index = 0; index < 60; index++)
            {
                var path = index % 2 == 0 ? "api/v1/catalog/search?q=fixture" : "api/v1/catalog/items";
                using var response = await RequestFromAsync(path, "192.0.2.10");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using var limited = await RequestFromAsync("api/v1/catalog/metadata", "192.0.2.12, 192.0.2.10");
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.InRange(limited.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0, 1, 60);
            Assert.True(limited.Headers.CacheControl?.NoStore);

            using var otherClient = await RequestFromAsync("api/v1/catalog/search?q=fixture", "192.0.2.11");
            Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
            using var health = await RequestFromAsync("health", "192.0.2.10");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var sync = await RequestFromAsync("api/v1/sync/snapshot", "192.0.2.10");
            Assert.Equal(HttpStatusCode.Unauthorized, sync.StatusCode);
            using var admin = await RequestFromAsync("api/v1/admin/diagnostics", "192.0.2.10");
            Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }

        async Task<HttpResponseMessage> RequestFromAsync(string path, string forwardedFor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            return await client.SendAsync(request);
        }
    }

    private static async Task RunFromAdmin(HttpClient client, ServerDatabase database, string? providerId)
    {
        using var response = await client.PostAsJsonAsync("api/v1/admin/diagnostics/crawl", new { providerId, retryFailures = false });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("accepted", result.RootElement.GetProperty("status").GetString());
        var runId = result.RootElement.GetProperty("runId").GetString();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = new ServerDiagnosticsStore(database).Snapshot([], new HashSet<string>(), new(DateTimeOffset.UtcNow, 0, 0, 0, 0));
            var run = snapshot.Runs.SingleOrDefault(item => item.Id == runId);
            if (run?.FinishedAt is not null)
            {
                Assert.Equal("completed", run.Status);
                Assert.Equal(0, run.Failures);
                Assert.Equal(0, run.TitlesUpdated);
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The admin-triggered background run did not complete.");
    }

    private static Process StartServer(Uri root, string directory, bool configured)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "UniversalMediaDistribution.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(repository.FullName, "src", "UniversalMediaDistribution.Server", "bin", configuration,
            "net11.0", "UniversalMediaDistribution.Server.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(root.AbsoluteUri);
        start.Environment["UMD_SERVER_DATA_DIRECTORY"] = directory;
        start.Environment["UMD_SERVER_ADMIN_TOKEN"] = configured ? AdminToken : "";
        start.Environment.Remove("UMD_SERVER_CATALOG_REQUESTS_PER_MINUTE");
        return Process.Start(start) ?? throw new InvalidOperationException("The isolated test server did not start.");
    }

    private static async Task Ready(Process process, HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException("The isolated test server exited before startup.");
            try
            {
                using var response = await client.GetAsync("health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(50);
        }
        throw new TimeoutException("The isolated test server did not become ready.");
    }
}
