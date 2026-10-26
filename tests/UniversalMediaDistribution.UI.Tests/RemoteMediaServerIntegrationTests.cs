using System.Diagnostics;
using System.Net.Sockets;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.UI.Remote;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class RemoteMediaServerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MigratedPublicCatalogSupportsPagedHttpReadsAndSearch()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "umd-http-catalog-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "online-catalog"));
        var document = new CatalogTransferDocument
        {
            Items = [
                new CatalogTransferItem { Id = Guid.CreateVersion7(), MediaType = "book", Subtype = "manga", Name = "Café first" },
                new CatalogTransferItem { Id = Guid.CreateVersion7(), MediaType = "book", Subtype = "manga", Name = "Second" }
            ]
        };
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "online-catalog", "current.json"),
            JsonSerializer.Serialize(new { revision = 7, updatedAt = DateTimeOffset.UtcNow, sha256 = "legacy", document },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var serviceUri = new Uri($"http://127.0.0.1:{ReservePort()}/");
        using var process = StartServer(serviceUri, dataDirectory);
        try
        {
            await WaitUntilReadyAsync(process, serviceUri);
            using var client = new HttpClient { BaseAddress = serviceUri };
            using var first = JsonDocument.Parse(await client.GetStringAsync("api/v1/catalog/items?limit=1"));
            Assert.Equal(7, first.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(1, first.RootElement.GetProperty("document").GetProperty("items").GetArrayLength());
            var cursor = first.RootElement.GetProperty("continuationToken").GetString();
            Assert.NotNull(cursor);
            using var second = JsonDocument.Parse(await client.GetStringAsync($"api/v1/catalog/items?limit=1&cursor={cursor}"));
            Assert.Equal(1, second.RootElement.GetProperty("document").GetProperty("items").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("continuationToken").ValueKind);
            using var search = JsonDocument.Parse(await client.GetStringAsync("api/v1/catalog/search?q=cafe&mediaType=book"));
            Assert.Equal("Café first", search.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
            using var metadata = JsonDocument.Parse(await client.GetStringAsync("api/v1/catalog/metadata"));
            Assert.Equal(2, metadata.RootElement.GetProperty("itemCount").GetInt32());
            using var invalid = await client.GetAsync("api/v1/catalog/items?cursor=invalid");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StreamedDesktopJson_RoundTripsAgainstKestrel()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "umd-http-sync-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var port = ReservePort();
        var serviceUri = new Uri($"http://127.0.0.1:{port}/");
        using var process = StartServer(
            serviceUri,
            dataDirectory);
        try
        {
            await WaitUntilReadyAsync(process, serviceUri);
            using var client = new HttpRemoteMediaServerClient();
            var capabilities =
                await client.GetCapabilitiesAsync(serviceUri);
            Assert.Equal(
                UserSyncDocument.CurrentVersion,
                capabilities.SyncProtocolVersion);
            Assert.True(capabilities.MaximumSyncBytes >= 1024);
            Assert.True(capabilities.GzipSyncRequests);

            await client.RegisterAsync(
                serviceUri,
                "integration.user",
                "correct horse battery staple",
                "Integration User",
                "Desktop");
            var write = await client.UploadSnapshotAsync(
                new UserSyncDocument
                {
                    Version = 1,
                    DeviceId = "integration-device",
                    DeviceName = "Desktop",
                    Catalog = new CatalogTransferDocument()
                },
                expectedRevision: 0);
            var snapshot = Assert.IsType<RemoteSyncSnapshot>(
                await client.GetSnapshotAsync());

            Assert.Equal(1, write.Revision);
            Assert.Equal(1, snapshot.Revision);
            // A version-one upload comes back as the version its shape
            // actually has; the server never restamps a document newer
            // than the device that wrote it.
            Assert.Equal(2, snapshot.Document.Version);

            // More than the old one-megabyte delta-response limit, and large
            // enough to exercise compressed streaming in both directions.
            var largeDocument = CreateDocument() with
            {
                Catalog = new CatalogTransferDocument
                {
                    Items = Enumerable.Range(0, 600).Select(index => new CatalogTransferItem
                    {
                        Id = Guid.CreateVersion7(),
                        MediaType = "book",
                        Subtype = "manga",
                        Name = $"Compressed title {index}",
                        Description = new string('x', 2048)
                    }).ToArray()
                }
            };
            await client.UploadSnapshotAsync(largeDocument, expectedRevision: 1);
            var changes = Assert.IsType<RemoteSyncChanges>(await client.GetChangesAsync(1));
            Assert.Equal(600, changes.Document.Catalog.Items.Count);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(changes).Length > 1024 * 1024);

            using var rawClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(serviceUri, "api/v1/sync/snapshot"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", client.AccessToken);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using var response = await rawClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length < 100_000);
            await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var decoded = await JsonDocument.ParseAsync(gzip);
            Assert.Equal(600, decoded.RootElement.GetProperty("document").GetProperty("catalog").GetProperty("items").GetArrayLength());

            var patch = largeDocument with
            {
                Catalog = largeDocument.Catalog with
                {
                    Items = [new CatalogTransferItem { Id = Guid.CreateVersion7(), MediaType = "book", Subtype = "novel", Name = "Compressed title 0" }]
                }
            };
            await client.PatchProgressAsync(patch);
            var delta = Assert.IsType<RemoteSyncChanges>(await client.GetChangesAsync(2));
            Assert.Equal("novel", Assert.Single(delta.Document.Catalog.Items).Subtype);
            Assert.Equal(601, (await client.GetSnapshotAsync())!.Document.Catalog.Items.Count);
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    // Opt-in measurement against a real HTTP server and a private copy of
    // an existing catalog. Ordinary test runs need no user data.
    [Fact]
    public async Task MeasureRepeatedSyncAgainstKestrel()
    {
        var source = Environment.GetEnvironmentVariable("UMD_TEST_SNAPSHOT_DB");
        if (string.IsNullOrEmpty(source)) return;
        var dataDirectory = Path.Combine(Path.GetTempPath(), "umd-http-sync-probe-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "catalog.db");
        File.Copy(source, databasePath);
        var serviceUri = new Uri($"http://127.0.0.1:{ReservePort()}/");
        using var process = StartServer(serviceUri, dataDirectory);
        try
        {
            await WaitUntilReadyAsync(process, serviceUri);
            using var client = new HttpRemoteMediaServerClient();
            await client.GetCapabilitiesAsync(serviceUri);
            await client.RegisterAsync(serviceUri, "probe.user", "correct horse battery staple", "Probe User", "Desktop");
            var database = new SqliteCatalogDatabase(databasePath);
            var store = new SqliteUserSyncStore(database, new SqliteCatalogRepository(database));
            var document = await store.ExportAsync("probe-device", "Performance probe");
            await client.UploadSnapshotAsync(document, 0);

            var timer = Stopwatch.StartNew();
            var first = await RemoteUserSyncReconciler.SynchronizeAsync(client, store, "probe-device", "Performance probe");
            output.WriteLine($"http-full-sync={timer.Elapsed.TotalMilliseconds:F1}ms; revision={first.WriteResult.Revision}");
            Assert.Equal(1, first.WriteResult.Revision);
            timer.Restart();
            var second = await RemoteUserSyncReconciler.SynchronizeAsync(client, store, "probe-device", "Performance probe",
                new RemoteSyncDeltaContext(first.BaselineRevision!.Value, first.NewBaseline));
            output.WriteLine($"http-delta-sync={timer.Elapsed.TotalMilliseconds:F1}ms; revision={second.WriteResult.Revision}");
            Assert.Equal(1, second.WriteResult.Revision);
            Assert.NotNull(second.NewBaseline);
            Assert.Empty(UserSyncDocumentDelta.ComputeChangedKeys(document, second.NewBaseline));
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompressedSnapshotsStillRespectTheUncompressedRequestLimit()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "umd-http-sync-limit-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var serviceUri = new Uri($"http://127.0.0.1:{ReservePort()}/");
        using var process = StartServer(serviceUri, dataDirectory, maximumJsonBytes: 4096);
        try
        {
            await WaitUntilReadyAsync(process, serviceUri);
            using var client = new HttpRemoteMediaServerClient();
            var capabilities = await client.GetCapabilitiesAsync(serviceUri);
            Assert.True(capabilities.GzipSyncRequests);
            await client.RegisterAsync(serviceUri, "bounded.user", "correct horse battery staple", "Bounded User", "Desktop");
            var document = CreateDocument() with
            {
                Catalog = new CatalogTransferDocument
                {
                    Items = [new CatalogTransferItem { MediaType = "book", Subtype = "manga", Name = "Large description", Description = new string('x', 16_384) }]
                }
            };

            await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadSnapshotAsync(document, 0).AsTask());

            Assert.Null(await client.GetSnapshotAsync());
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ASessionStoredOnOneRunSignsTheNextRunBackIn()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "umd-http-session-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var port = ReservePort();
        var serviceUri = new Uri($"http://127.0.0.1:{port}/");
        using var process = StartServer(serviceUri, dataDirectory);
        try
        {
            await WaitUntilReadyAsync(process, serviceUri);
            var store = new InMemoryRemoteSessionStore();

            // First run: sign in and keep the session.
            RemoteAccountSessionInfo firstSession;
            using (var first = new HttpRemoteMediaServerClient())
            {
                firstSession = await first.RegisterAsync(
                    serviceUri,
                    "session.user",
                    "correct horse battery staple",
                    "Session User",
                    "Desktop");
                await store.SaveAsync(new RemoteSessionState
                {
                    ServiceUrl = firstSession.ServiceBaseUri.AbsoluteUri,
                    AccessToken = first.AccessToken!,
                    AccountId = firstSession.AccountId,
                    Username = firstSession.Username,
                    DisplayName = firstSession.DisplayName,
                    ExpiresAt = firstSession.ExpiresAt
                });
                await first.UploadSnapshotAsync(
                    CreateDocument(),
                    expectedRevision: 0);
            }

            // Closing the app must not revoke the session, so the second run
            // starts signed in without a password.
            var stored = await store.LoadAsync();
            Assert.NotNull(stored);
            Assert.True(stored.IsUsable(DateTimeOffset.UtcNow));

            using var second = new HttpRemoteMediaServerClient();
            var restored = await second.RestoreSessionAsync(
                new Uri(stored.ServiceUrl),
                stored.AccessToken);

            Assert.NotNull(restored);
            Assert.Equal(firstSession.AccountId, restored.AccountId);
            Assert.Equal("session.user", restored.Username);
            var snapshot = Assert.IsType<RemoteSyncSnapshot>(
                await second.GetSnapshotAsync());
            Assert.Equal(1, snapshot.Revision);

            // An explicit sign-out ends it for good: the same token cannot be
            // restored again.
            await second.SignOutAsync();
            using var third = new HttpRemoteMediaServerClient();
            Assert.Null(
                await third.RestoreSessionAsync(
                    new Uri(stored.ServiceUrl),
                    stored.AccessToken));
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The whole watch-party path against a real server: a party made over
    /// HTTP, joined by code, and a pause sent up one member's socket arriving
    /// down the other's with the shared timeline stopped where they pressed
    /// it.
    /// </summary>
    [Fact]
    public async Task AWatchPartyRelaysAPauseBetweenTwoMembers()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "umd-http-party-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var port = ReservePort();
        var serviceUri = new Uri($"http://127.0.0.1:{port}/");
        using var process = StartServer(serviceUri, dataDirectory);
        try
        {
            await WaitUntilReadyAsync(process, serviceUri);
            using var host = new HttpRemoteMediaServerClient();
            using var guest = new HttpRemoteMediaServerClient();
            await host.RegisterAsync(
                serviceUri,
                "party.host",
                "correct horse battery staple",
                "Party Host",
                "Desktop");
            await guest.RegisterAsync(
                serviceUri,
                "party.guest",
                "correct horse battery staple",
                "Party Guest",
                "Desktop");

            var party = await host.CreateWatchPartyAsync(
                new RemoteWatchPartyContent(
                    ItemKey: string.Empty,
                    "movie",
                    "Cowboy Bebop",
                    "Cowboy Bebop · S1 · 5",
                    "episode",
                    SeasonNumber: 1,
                    DisplayNumber: "5"),
                positionSeconds: 100);
            Assert.Equal(6, party.JoinCode.Length);
            Assert.NotNull(party.Content);

            var joined = await guest.JoinWatchPartyAsync(party.JoinCode);
            Assert.NotNull(joined);
            Assert.Equal(2, joined.Members.Count);
            Assert.Equal(party.Content.ItemKey, joined.Content?.ItemKey);

            await using var hostSocket =
                await host.ConnectWatchPartyAsync();
            await using var guestSocket =
                await guest.ConnectWatchPartyAsync();
            await guestSocket.SendAsync(
                new RemoteWatchPartyCommand("pause", 123));

            // The socket also carries join/connect notices; the pause is the
            // one this test is about.
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            var received = await ReceiveUntilAsync(
                hostSocket,
                "pause",
                timeout.Token);

            Assert.NotNull(received);
            Assert.NotNull(received.State);
            Assert.False(received.State.Playback.IsPlaying);
            Assert.Equal(123, received.State.Playback.PositionSeconds);
            Assert.Equal("Party Guest", received.InitiatedByDisplayName);

            // A stalled member pauses the party, and their recovery resumes
            // it — the whole point of telling buffering apart from a pause.
            // Resumed first, because a party paused on purpose is meant to
            // stay paused when somebody's buffer refills.
            await guestSocket.SendAsync(
                new RemoteWatchPartyCommand("play", 130));
            await ReceiveUntilAsync(hostSocket, "play", timeout.Token);
            await guestSocket.SendAsync(
                new RemoteWatchPartyCommand(
                    "buffering",
                    130,
                    Content: null,
                    IsBuffering: true));
            var stalled = await ReceiveUntilAsync(
                hostSocket,
                "buffering",
                timeout.Token);
            Assert.NotNull(stalled?.State);
            Assert.False(stalled.State.Playback.IsPlaying);
            Assert.True(
                stalled.State.Members
                    .Single(member => !member.IsHost)
                    .IsBuffering);

            await guestSocket.SendAsync(
                new RemoteWatchPartyCommand(
                    "buffering",
                    130,
                    Content: null,
                    IsBuffering: false));
            var recovered = await ReceiveUntilAsync(
                hostSocket,
                "buffering",
                timeout.Token);
            Assert.NotNull(recovered?.State);
            Assert.True(recovered.State.Playback.IsPlaying);
            Assert.False(
                recovered.State.Members
                    .Single(member => !member.IsHost)
                    .IsBuffering);

            // A deliberate pause during a stall must survive both recovery
            // and the stalled member leaving the room.
            foreach (var leavesWhileBuffering in new[] { false, true })
            {
                await guestSocket.SendAsync(new RemoteWatchPartyCommand("play", 150));
                await ReceiveUntilAsync(hostSocket, "play", timeout.Token);
                await guestSocket.SendAsync(new RemoteWatchPartyCommand("buffering", IsBuffering: true));
                await ReceiveUntilAsync(hostSocket, "buffering", timeout.Token);
                await hostSocket.SendAsync(new RemoteWatchPartyCommand("pause", 155));
                await ReceiveUntilAsync(hostSocket, "pause", timeout.Token);
                if (leavesWhileBuffering)
                {
                    Assert.True(await guest.LeaveWatchPartyAsync());
                    var left = await ReceiveUntilAsync(hostSocket, "left", timeout.Token);
                    Assert.False(left!.State!.Playback.IsPlaying);
                    Assert.Equal(155, left.State.Playback.PositionSeconds);
                    Assert.NotNull(await guest.JoinWatchPartyAsync(party.JoinCode));
                }
                else
                {
                    await guestSocket.SendAsync(new RemoteWatchPartyCommand("buffering", IsBuffering: false));
                    var stillPaused = await ReceiveUntilAsync(hostSocket, "buffering", timeout.Token);
                    Assert.False(stillPaused!.State!.Playback.IsPlaying);
                    Assert.Equal(155, stillPaused.State.Playback.PositionSeconds);
                }
            }

            // Locking transport is the host's to do, and only theirs.
            Assert.Null(await guest.SetWatchPartyOptionsAsync(true));
            var locked = await host.SetWatchPartyOptionsAsync(true);
            Assert.NotNull(locked);
            Assert.True(locked.HostControlsOnly);
            Assert.True(
                (await guest.GetCurrentWatchPartyAsync())!.HostControlsOnly);

            Assert.True(await guest.LeaveWatchPartyAsync());
            Assert.Null(await guest.GetCurrentWatchPartyAsync());
            var remaining = await host.GetCurrentWatchPartyAsync();
            Assert.NotNull(remaining);
            Assert.Single(remaining.Members);
        }
        finally
        {
            await StopServerAsync(process);
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The next update for that reason. A party socket also carries join,
    /// connect, and disconnect notices, so a test waiting for one particular
    /// change has to read past them.
    /// </summary>
    private static async Task<RemoteWatchPartyEvent?> ReceiveUntilAsync(
        IWatchPartyConnection connection,
        string reason,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var received = await connection.ReceiveAsync(cancellationToken);
            if (received is null ||
                string.Equals(
                    received.Reason,
                    reason,
                    StringComparison.Ordinal))
            {
                return received;
            }
        }
    }

    private static UserSyncDocument CreateDocument() =>
        new()
        {
            Version = 1,
            DeviceId = "session-device",
            DeviceName = "Desktop",
            Catalog = new CatalogTransferDocument()
        };

    private static Process StartServer(
        Uri serviceUri,
        string dataDirectory,
        long? maximumJsonBytes = null)
    {
        var repositoryDirectory = FindRepositoryDirectory();
        var configuration = new DirectoryInfo(
            AppContext.BaseDirectory).Parent?.Name ??
            throw new InvalidOperationException(
                "The test build configuration could not be determined.");
        var assemblyPath = Path.Combine(
            repositoryDirectory,
            "src",
            "UniversalMediaDistribution.Server",
            "bin",
            configuration,
            "net11.0",
            "UniversalMediaDistribution.Server.dll");
        Assert.True(
            File.Exists(assemblyPath),
            $"The test server assembly was not built at '{assemblyPath}'.");
        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"\"{assemblyPath}\" --urls {serviceUri.AbsoluteUri}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["UMD_SERVER_DATA_DIRECTORY"] =
            dataDirectory;
        startInfo.Environment["UMD_SERVER_ALLOW_REGISTRATION"] = "true";
        if (maximumJsonBytes is { } limit)
        {
            startInfo.Environment["UMD_SERVER_MAX_JSON_BYTES"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "The integration server could not be started.");
    }

    private static string FindRepositoryDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                current.FullName,
                "UniversalMediaDistribution.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "The repository directory could not be found.");
    }

    private static async Task WaitUntilReadyAsync(
        Process process,
        Uri serviceUri)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(500)
        };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "The integration server exited before startup.\n" +
                    await process.StandardError.ReadToEndAsync());
            }

            try
            {
                using var response = await client.GetAsync(
                    new Uri(serviceUri, "health"));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            "The integration server did not become ready.");
    }

    private static async Task StopServerAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
