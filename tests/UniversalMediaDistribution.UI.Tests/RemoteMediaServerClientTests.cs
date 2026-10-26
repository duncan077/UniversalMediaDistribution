using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class RemoteMediaServerClientTests
{
    private static readonly Uri LoopbackServer =
        new("http://127.0.0.1:5123/");

    [Fact]
    public async Task LoginUsesBearerOnlyInMemoryAndSignOutClearsIt()
    {
        var token = CreateToken();
        var handler = new QueueHandler(
            request => JsonResponse(
                request,
                new RemoteAccountSessionResponse(
                    Guid.CreateVersion7(),
                    "alice",
                    "Alice",
                    token,
                    DateTimeOffset.UtcNow.AddHours(1)),
                RemoteMediaJsonContext.Default
                    .RemoteAccountSessionResponse),
            request => Response(
                request,
                HttpStatusCode.NotFound),
            request => Response(
                request,
                HttpStatusCode.NoContent));
        using var httpClient = new HttpClient(handler);
        using var client =
            new HttpRemoteMediaServerClient(httpClient);

        var session = await client.LoginAsync(
            LoopbackServer,
            "alice",
            "correct horse battery staple",
            "Desktop");
        var snapshot = await client.GetSnapshotAsync();
        await client.SignOutAsync();

        Assert.Equal("alice", session.Username);
        Assert.Null(snapshot);
        Assert.Null(client.Session);
        Assert.Null(
            typeof(RemoteAccountSessionInfo)
                .GetProperty("AccessToken"));
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal(
            $"Bearer {token}",
            handler.Requests[1].Authorization);
        Assert.Equal(
            $"Bearer {token}",
            handler.Requests[2].Authorization);
        Assert.Contains(
            "correct horse battery staple",
            handler.Requests[0].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "correct horse battery staple",
            handler.Requests[1].Body ?? string.Empty,
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSnapshotAsync().AsTask());
    }

    [Fact]
    public async Task UploadReportsOptimisticRevisionConflict()
    {
        var token = CreateToken();
        var handler = new QueueHandler(
            request => JsonResponse(
                request,
                new RemoteAccountSessionResponse(
                    Guid.CreateVersion7(),
                    "alice",
                    "Alice",
                    token,
                    DateTimeOffset.UtcNow.AddHours(1)),
                RemoteMediaJsonContext.Default
                    .RemoteAccountSessionResponse),
            request => JsonResponse(
                request,
                new RemoteSyncConflictResponse(
                    "sync_conflict",
                    "A newer device copy exists.",
                    7),
                RemoteMediaJsonContext.Default
                    .RemoteSyncConflictResponse,
                HttpStatusCode.Conflict));
        using var httpClient = new HttpClient(handler);
        using var client =
            new HttpRemoteMediaServerClient(httpClient);
        await client.LoginAsync(
            LoopbackServer,
            "alice",
            "correct horse battery staple",
            "Desktop");

        var exception =
            await Assert.ThrowsAsync<RemoteSyncConflictException>(
                () => client.UploadSnapshotAsync(
                    CreateDocument(),
                    expectedRevision: 0).AsTask());

        Assert.Equal(7, exception.CurrentRevision);
        Assert.Equal(
            HttpMethod.Put,
            handler.Requests[1].Method);
        Assert.Contains(
            "\"expectedRevision\":0",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Equal(
            $"Bearer {token}",
            handler.Requests[1].Authorization);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SyncUploadsUseCompressionOnlyForAServerThatAdvertisesIt(bool supportsGzip, bool differentServer)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var handler = new QueueHandler(
            request => JsonResponse(request,
                new RemoteServerCapabilities(1, false, false, true, true, true, true, true, GzipSyncRequests: supportsGzip),
                RemoteMediaJsonContext.Default.RemoteServerCapabilities),
            request => JsonResponse(request,
                new RemoteAccountSessionResponse(Guid.CreateVersion7(), "alice", "Alice", CreateToken(), timestamp.AddHours(1)),
                RemoteMediaJsonContext.Default.RemoteAccountSessionResponse),
            request => JsonResponse(request, new RemoteSyncWriteResponse(1, timestamp, "desktop-one"),
                RemoteMediaJsonContext.Default.RemoteSyncWriteResponse),
            request => JsonResponse(request, new RemoteSyncWriteResponse(2, timestamp, "desktop-one"),
                RemoteMediaJsonContext.Default.RemoteSyncWriteResponse));
        using var httpClient = new HttpClient(handler);
        using var client = new HttpRemoteMediaServerClient(httpClient);
        await client.GetCapabilitiesAsync(LoopbackServer);
        await client.LoginAsync(differentServer ? new Uri("http://127.0.0.1:5124/") : LoopbackServer,
            "alice", "correct horse battery staple", "Desktop");

        await client.UploadSnapshotAsync(CreateDocument(), 0);
        await client.PatchProgressAsync(CreateDocument());

        var expectedEncoding = supportsGzip && !differentServer ? "gzip" : string.Empty;
        Assert.Equal(expectedEncoding, handler.Requests[2].ContentEncoding);
        Assert.Equal(expectedEncoding, handler.Requests[3].ContentEncoding);
        Assert.Contains("\"expectedRevision\":0", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"patch\":", handler.Requests[3].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalPlainHttpServerIsRejectedBeforeSending()
    {
        var handler = new QueueHandler();
        using var httpClient = new HttpClient(handler);
        using var client =
            new HttpRemoteMediaServerClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetCapabilitiesAsync(
                new Uri("http://media.example/")).AsTask());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadReconcilesDuplicatesAndKeepsMissingTargetsForTheLocalCatalog()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var progress = new UserSyncProgressEntry
        {
            Content = new UserSyncContentIdentity
            {
                Item = new UserSyncCatalogItemIdentity { MediaType = "book", Name = "Local title" }
            },
            Position = 10,
            PositionKind = "page",
            UpdatedAt = timestamp
        };
        var document = CreateDocument() with
        {
            Progress = [progress, progress with { Position = 20, UpdatedAt = timestamp.AddSeconds(1) }]
        };
        var handler = new QueueHandler(
            request => JsonResponse(request,
                new RemoteAccountSessionResponse(Guid.CreateVersion7(), "alice", "Alice",
                    CreateToken(), timestamp.AddHours(1)),
                RemoteMediaJsonContext.Default.RemoteAccountSessionResponse),
            request => JsonResponse(request,
                new RemoteSyncSnapshotResponse(1, timestamp, document.DeviceId, document),
                RemoteMediaJsonContext.Default.RemoteSyncSnapshotResponse));
        using var httpClient = new HttpClient(handler);
        using var client = new HttpRemoteMediaServerClient(httpClient);
        await client.LoginAsync(LoopbackServer, "alice", "correct horse battery staple", "Desktop");

        var snapshot = await client.GetSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Document.Catalog.Items);
        Assert.Equal(20, Assert.Single(snapshot.Document.Progress).Position);
        Assert.NotNull(snapshot.Document.Progress[0].SyncVersion);
    }

    [Theory]
    [InlineData("https://media.example/api/v1/sync/changes?since=5", true)]
    [InlineData("http://media.example/api/v1/sync/changes?since=5", false)]
    [InlineData("https://user@media.example/api/v1/sync/changes?since=5", false)]
    [InlineData("https://media.example/api/v1/sync/changes?since=5#fragment", false)]
    public async Task DeltaResponseQueriesAreAllowedWithoutRelaxingTransportChecks(string responseUrl, bool accepted)
    {
        var handler = new QueueHandler(
            request => JsonResponse(request,
                new RemoteAccountSessionResponse(Guid.CreateVersion7(), "alice", "Alice", CreateToken(), DateTimeOffset.UtcNow.AddHours(1)),
                RemoteMediaJsonContext.Default.RemoteAccountSessionResponse),
            request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(request.Method, responseUrl),
                Content = JsonContent.Create(new RemoteSyncChanges(5, 6, false, CreateDocument()), RemoteMediaJsonContext.Default.RemoteSyncChanges)
            });
        using var httpClient = new HttpClient(handler);
        using var client = new HttpRemoteMediaServerClient(httpClient);
        await client.LoginAsync(LoopbackServer, "alice", "correct horse battery staple", "Desktop");

        if (accepted)
        {
            Assert.Equal(6, (await client.GetChangesAsync(5))!.ToRevision);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetChangesAsync(5).AsTask());
        }
    }

    private static UserSyncDocument CreateDocument() =>
        new()
        {
            Version = 1,
            DeviceId = "desktop-one",
            DeviceName = "Desktop",
            Catalog = new CatalogTransferDocument()
        };

    [Fact]
    public async Task ARestoredSessionIsAdoptedOnlyAfterTheServerConfirmsIt()
    {
        var token = CreateToken();
        var accountId = Guid.CreateVersion7();
        var handler = new QueueHandler(
            request => JsonResponse(
                request,
                new RemoteAccountProfileResponse(
                    accountId,
                    "alice",
                    "Alice",
                    DateTimeOffset.UtcNow.AddDays(-3),
                    SessionExpiresAt: DateTimeOffset.UtcNow.AddDays(27)),
                RemoteMediaJsonContext.Default
                    .RemoteAccountProfileResponse),
            request => Response(request, HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        using var client = new HttpRemoteMediaServerClient(httpClient);

        var session = await client.RestoreSessionAsync(LoopbackServer, token);

        Assert.NotNull(session);
        Assert.Equal(accountId, session.AccountId);
        Assert.Equal("Alice", session.DisplayName);
        Assert.Equal(token, client.AccessToken);
        Assert.Equal($"Bearer {token}", handler.Requests[0].Authorization);
        // The restored token authenticates ordinary calls too.
        Assert.Null(await client.GetSnapshotAsync());
        Assert.Equal($"Bearer {token}", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task ARejectedStoredTokenLeavesTheClientSignedOut()
    {
        var handler = new QueueHandler(
            request => Response(request, HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        using var client = new HttpRemoteMediaServerClient(httpClient);

        var session = await client.RestoreSessionAsync(
            LoopbackServer,
            CreateToken());

        Assert.Null(session);
        Assert.Null(client.Session);
        Assert.Null(client.AccessToken);
    }

    [Fact]
    public async Task AnExpiredProfileIsNotTreatedAsASession()
    {
        var handler = new QueueHandler(
            request => JsonResponse(
                request,
                new RemoteAccountProfileResponse(
                    Guid.CreateVersion7(),
                    "alice",
                    "Alice",
                    DateTimeOffset.UtcNow.AddDays(-40),
                    SessionExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)),
                RemoteMediaJsonContext.Default
                    .RemoteAccountProfileResponse));
        using var httpClient = new HttpClient(handler);
        using var client = new HttpRemoteMediaServerClient(httpClient);

        Assert.Null(
            await client.RestoreSessionAsync(LoopbackServer, CreateToken()));
        Assert.Null(client.Session);
    }

    [Fact]
    public async Task AMalformedStoredTokenIsRejectedWithoutARequest()
    {
        var handler = new QueueHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new HttpRemoteMediaServerClient(httpClient);

        Assert.Null(await client.RestoreSessionAsync(LoopbackServer, "short"));
        Assert.Empty(handler.Requests);
    }

    private static string CreateToken() =>
        Convert.ToBase64String(
                Enumerable.Range(0, 32)
                    .Select(static value => (byte)value)
                    .ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static HttpResponseMessage JsonResponse<T>(
        HttpRequestMessage request,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>
            typeInfo,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            RequestMessage = new HttpRequestMessage(
                request.Method,
                request.RequestUri),
            Content = JsonContent.Create(value, typeInfo)
        };

    private static HttpResponseMessage Response(
        HttpRequestMessage request,
        HttpStatusCode statusCode) =>
        new(statusCode)
        {
            RequestMessage = new HttpRequestMessage(
                request.Method,
                request.RequestUri)
        };

    private sealed record CapturedRequest(
        HttpMethod Method,
        string? Authorization,
        string? Body,
        string ContentEncoding);

    private sealed class QueueHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[]
            responses) : HttpMessageHandler
    {
        private readonly Queue<
            Func<HttpRequestMessage, HttpResponseMessage>>
            _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage>
            SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    await using var compressed = await request.Content.ReadAsStreamAsync(cancellationToken);
                    await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip);
                    body = await reader.ReadToEndAsync(cancellationToken);
                }
                else
                {
                    body = await request.Content.ReadAsStringAsync(cancellationToken);
                }
            }
            Requests.Add(new CapturedRequest(
                request.Method,
                request.Headers.Authorization?.ToString(),
                body,
                request.Content is null ? string.Empty : string.Join(",", request.Content.Headers.ContentEncoding)));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "The test server received an unexpected request.");
            }

            return _responses.Dequeue()(request);
        }
    }
}
