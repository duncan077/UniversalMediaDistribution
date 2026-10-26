using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.UI.Remote;

internal sealed record RemoteAccountRegistrationRequest(
    string Username,
    string Password,
    string DisplayName,
    string DeviceName);

internal sealed record RemoteAccountLoginRequest(
    string Username,
    string Password,
    string DeviceName);

internal sealed record RemoteAccountSessionResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    string AccessToken,
    DateTimeOffset ExpiresAt);

internal sealed record RemoteAccountProfileResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    DateTimeOffset CreatedAt,
    // The profile endpoint names this differently from the login response.
    DateTimeOffset SessionExpiresAt);

internal sealed record RemoteSyncSnapshotResponse(
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId,
    UserSyncDocument Document);

internal sealed record RemoteSyncUploadRequest(
    long ExpectedRevision,
    UserSyncDocument Document);

internal sealed record RemoteSyncProgressPatchRequest(UserSyncDocument Patch);

internal sealed record RemoteSyncWriteResponse(
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId);

internal sealed record RemoteSharedListPublishRequest(Guid ListId);

internal sealed record RemoteFriendRequestCreateRequest(string Username);

internal sealed record RemoteFriendSharingUpdateRequest(bool ShareProgress);

internal sealed record RemoteFriendVisibilityUpdateRequest(
    bool ShareProgress,
    bool DiscoverableByUsername,
    string[] HiddenItemKeys);

internal sealed record RemoteFriendVisibilityResponse(
    bool ShareProgress,
    bool DiscoverableByUsername,
    string[] HiddenItemKeys);

internal sealed record RemoteFriendSummaryResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    DateTimeOffset Since,
    bool ShareProgress,
    DateTimeOffset? ProgressUpdatedAt);

internal sealed record RemoteFriendRequestResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    string Direction,
    DateTimeOffset CreatedAt);

internal sealed record RemoteFriendActivityLookupRequest(string[] ItemKeys);

internal sealed record RemoteFriendActivityMatchResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    string ItemKey,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt);

internal sealed record RemoteFriendActivityLookupResponse(
    RemoteFriendActivityMatchResponse[] Matches);

internal sealed record RemoteFriendSharedTitleResponse(
    string ItemKey,
    string Title,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt);

internal sealed record RemoteFriendComparisonResponse(
    RemoteFriendComparisonTitle[]? InCommon,
    RemoteFriendComparisonTitle[]? FriendOnly,
    RemoteFriendComparisonTitle[]? ViewerOnly);

internal sealed record RemoteFriendSharedTitlesResponse(
    RemoteFriendSharedTitleResponse[] Titles);

internal sealed record RemoteSharedListResponse(
    string ShareId,
    Guid ListId,
    string Name,
    string OwnerDisplayName,
    int ItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CatalogPath);

internal sealed record RemoteWatchPartyContentPayload(
    string MediaType,
    string Name,
    string? Title,
    string? UnitKind,
    int? SeasonNumber,
    string? DisplayNumber);

internal sealed record RemoteWatchPartyCreateRequest(
    RemoteWatchPartyContentPayload? Content,
    double PositionSeconds);

internal sealed record RemoteWatchPartyJoinRequest(string JoinCode);

internal sealed record RemoteWatchPartyContentResponse(
    string ItemKey,
    string MediaType,
    string Name,
    string Title,
    string? UnitKind,
    int? SeasonNumber,
    string? DisplayNumber);

internal sealed record RemoteWatchPartyPlaybackResponse(
    bool IsPlaying,
    double PositionSeconds,
    DateTimeOffset UpdatedAt,
    string UpdatedByDisplayName);

internal sealed record RemoteWatchPartyMemberResponse(
    Guid AccountId,
    string DisplayName,
    bool IsHost,
    bool IsConnected,
    bool IsBuffering);

internal sealed record RemoteWatchPartyStateResponse(
    Guid PartyId,
    string JoinCode,
    Guid HostAccountId,
    RemoteWatchPartyMemberResponse[]? Members,
    RemoteWatchPartyContentResponse? Content,
    RemoteWatchPartyPlaybackResponse? Playback,
    DateTimeOffset CreatedAt,
    bool HostControlsOnly);

internal sealed record RemoteWatchPartyOptionsRequest(
    bool HostControlsOnly);

internal sealed record RemoteWatchPartyClientMessage(
    string Type,
    double? PositionSeconds,
    RemoteWatchPartyContentPayload? Content,
    bool? IsBuffering);

internal sealed record RemoteWatchPartyServerMessage(
    string Reason,
    long Sequence,
    Guid? InitiatedByAccountId,
    string? InitiatedByDisplayName,
    RemoteWatchPartyStateResponse? State);

internal sealed record RemoteErrorResponse(
    string Code,
    string Message);

internal sealed record RemoteSyncConflictResponse(
    string Code,
    string Message,
    long CurrentRevision);

public sealed class HttpRemoteMediaServerClient :
    IRemoteMediaServerClient,
    IDisposable
{
    private static readonly TimeSpan ShortTimeout =
        TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SyncTimeout =
        TimeSpan.FromMinutes(3);
    private const long MaximumShortResponseBytes =
        1024L * 1024;
    private const long DefaultMaximumSyncResponseBytes =
        40L * 1024 * 1024;
    private const long AbsoluteMaximumSyncResponseBytes =
        256L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private long _maximumSyncResponseBytes =
        DefaultMaximumSyncResponseBytes;
    private string? _accessToken;
    private Uri? _gzipSyncService;

    public HttpRemoteMediaServerClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public RemoteAccountSessionInfo? Session { get; private set; }

    public string? AccessToken => _accessToken;

    /// <summary>
    /// A token kept from an earlier run is only trusted after the server
    /// confirms it, so a revoked or expired session never presents itself as
    /// signed in.
    /// </summary>
    public async ValueTask<RemoteAccountSessionInfo?> RestoreSessionAsync(
        Uri serviceBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceBaseUri);
        if (!IsAccessTokenValid(accessToken))
        {
            return null;
        }

        var root = NormalizeServiceBaseUri(serviceBaseUri);
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            new Uri(root, "api/v1/accounts/me"),
            accessToken);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSecureResponse(response);
        if (response.StatusCode is HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        var profile = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteAccountProfileResponse,
            timeout.Token);
        if (profile.AccountId == Guid.Empty ||
            profile.SessionExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        _accessToken = accessToken;
        Session = new RemoteAccountSessionInfo(
            profile.AccountId,
            profile.Username,
            profile.DisplayName,
            profile.SessionExpiresAt,
            root);
        return Session;
    }

    public async ValueTask<RemoteServerCapabilities>
        GetCapabilitiesAsync(
            Uri serviceBaseUri,
            CancellationToken cancellationToken = default)
    {
        var root = NormalizeServiceBaseUri(serviceBaseUri);
        using var timeout = CreateTimeout(
            cancellationToken,
            ShortTimeout);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(root, "api/v1/capabilities"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        var capabilities = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteServerCapabilities,
            timeout.Token);
        _maximumSyncResponseBytes = capabilities.MaximumSyncBytes is
                >= 1024 and <= AbsoluteMaximumSyncResponseBytes
            ? capabilities.MaximumSyncBytes
            : DefaultMaximumSyncResponseBytes;
        _gzipSyncService = capabilities.GzipSyncRequests ? root : null;
        return capabilities;
    }

    public ValueTask<RemoteAccountSessionInfo> RegisterAsync(
        Uri serviceBaseUri,
        string username,
        string password,
        string displayName,
        string deviceName,
        CancellationToken cancellationToken = default) =>
        StartSessionAsync(
            serviceBaseUri,
            "api/v1/accounts/register",
            new RemoteAccountRegistrationRequest(
                username,
                password,
                displayName,
                deviceName),
            RemoteMediaJsonContext.Default
                .RemoteAccountRegistrationRequest,
            cancellationToken);

    public ValueTask<RemoteAccountSessionInfo> LoginAsync(
        Uri serviceBaseUri,
        string username,
        string password,
        string deviceName,
        CancellationToken cancellationToken = default) =>
        StartSessionAsync(
            serviceBaseUri,
            "api/v1/accounts/login",
            new RemoteAccountLoginRequest(
                username,
                password,
                deviceName),
            RemoteMediaJsonContext.Default.RemoteAccountLoginRequest,
            cancellationToken);

    public async ValueTask SignOutAsync(
        CancellationToken cancellationToken = default)
    {
        var session = Session;
        var token = _accessToken;
        Session = null;
        _accessToken = null;
        if (session is null || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        using var timeout = CreateTimeout(
            cancellationToken,
            ShortTimeout);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(
                session.ServiceBaseUri,
                "api/v1/accounts/session"));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            EnsureSecureResponse(response);
            if (response.StatusCode is not
                (HttpStatusCode.Unauthorized or
                 HttpStatusCode.NotFound))
            {
                await EnsureSuccessAsync(response, timeout.Token);
            }
        }
        finally
        {
            Session = null;
            _accessToken = null;
        }
    }

    public void AbandonSession()
    {
        Session = null;
        _accessToken = null;
    }

    public async ValueTask<RemoteSyncSnapshot?> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(
            cancellationToken,
            SyncTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            new Uri(session.ServiceBaseUri, "api/v1/sync/snapshot"),
            token);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSecureResponse(response);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        var value = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteSyncSnapshotResponse,
            timeout.Token,
            _maximumSyncResponseBytes);
        var document = UserSyncDocumentMerger.Normalize(value.Document);
        return new RemoteSyncSnapshot(
            value.Revision,
            value.UpdatedAt,
            value.UpdatedByDeviceId,
            document);
    }

    public async ValueTask<RemoteSyncWriteResult> UploadSnapshotAsync(
        UserSyncDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        document = UserSyncDocumentMerger.Normalize(document);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision));
        }

        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(
            cancellationToken,
            SyncTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Put,
            new Uri(session.ServiceBaseUri, "api/v1/sync/snapshot"),
            token);
        request.Content = CreateSyncContent(session.ServiceBaseUri,
            new RemoteSyncUploadRequest(
                expectedRevision,
                document),
            RemoteMediaJsonContext.Default.RemoteSyncUploadRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (response.StatusCode is HttpStatusCode.Conflict)
        {
            var conflict = await ReadJsonAsync(
                response,
                RemoteMediaJsonContext.Default
                    .RemoteSyncConflictResponse,
                timeout.Token);
            throw new RemoteSyncConflictException(
                conflict.CurrentRevision,
                conflict.Message);
        }

        await EnsureSuccessAsync(response, timeout.Token);
        var result = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteSyncWriteResponse,
            timeout.Token);
        return new RemoteSyncWriteResult(
            result.Revision,
            result.UpdatedAt,
            result.UpdatedByDeviceId);
    }

    public async ValueTask<RemoteSharedList> PublishSharedListAsync(
        Guid listId,
        CancellationToken cancellationToken = default)
    {
        if (listId == Guid.Empty)
        {
            throw new ArgumentException(
                "A shared list requires a list ID.",
                nameof(listId));
        }

        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(
            cancellationToken,
            ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(session.ServiceBaseUri, "api/v1/shared-lists"),
            token);
        request.Content = JsonContent.Create(
            new RemoteSharedListPublishRequest(listId),
            RemoteMediaJsonContext.Default
                .RemoteSharedListPublishRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        return ToSharedList(
            await ReadJsonAsync(
                response,
                RemoteMediaJsonContext.Default.RemoteSharedListResponse,
                timeout.Token),
            session.ServiceBaseUri);
    }

    public async ValueTask<IReadOnlyList<RemoteSharedList>>
        GetSharedListsAsync(
            CancellationToken cancellationToken = default)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(
            cancellationToken,
            ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            new Uri(session.ServiceBaseUri, "api/v1/shared-lists"),
            token);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        var values = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default
                .RemoteSharedListResponseArray,
            timeout.Token);
        return values
            .Select(value => ToSharedList(
                value,
                session.ServiceBaseUri))
            .ToArray();
    }

    public async ValueTask RevokeSharedListAsync(
        string shareId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareId);
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(
            cancellationToken,
            ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Delete,
            new Uri(
                session.ServiceBaseUri,
                $"api/v1/shared-lists/" +
                $"{Uri.EscapeDataString(shareId.Trim())}"),
            token);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
    }

    public async ValueTask<IReadOnlyList<RemoteFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        var values = await GetFriendJsonAsync(
            "api/v1/friends",
            RemoteMediaJsonContext.Default.RemoteFriendSummaryResponseArray,
            cancellationToken);
        return values
            .Select(static value => new RemoteFriend(
                value.AccountId,
                value.Username,
                value.DisplayName,
                value.Since,
                value.ShareProgress,
                value.ProgressUpdatedAt))
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<RemoteFriendRequest>>
        GetFriendRequestsAsync(
            CancellationToken cancellationToken = default)
    {
        var values = await GetFriendJsonAsync(
            "api/v1/friends/requests",
            RemoteMediaJsonContext.Default.RemoteFriendRequestResponseArray,
            cancellationToken);
        return values
            .Select(static value => new RemoteFriendRequest(
                value.AccountId,
                value.Username,
                value.DisplayName,
                string.Equals(
                    value.Direction,
                    "incoming",
                    StringComparison.Ordinal),
                value.CreatedAt))
            .ToArray();
    }

    public async ValueTask<RemoteFriendRequestOutcome?>
        SendFriendRequestAsync(
            string username,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(session.ServiceBaseUri, "api/v1/friends/requests"),
            token);
        request.Content = JsonContent.Create(
            new RemoteFriendRequestCreateRequest(username.Trim()),
            RemoteMediaJsonContext.Default.RemoteFriendRequestCreateRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSecureResponse(response);

        // The server answers alike for a name nobody holds and one whose owner
        // does not accept requests, so this cannot tell them apart either.
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        var created = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteFriendRequestResponse,
            timeout.Token);
        return string.Equals(
            created.Direction,
            "accepted",
            StringComparison.Ordinal)
            ? RemoteFriendRequestOutcome.Accepted
            : RemoteFriendRequestOutcome.Sent;
    }

    public ValueTask<bool> AcceptFriendRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        SendFriendCommandAsync(
            HttpMethod.Post,
            $"api/v1/friends/requests/{accountId:D}/accept",
            cancellationToken);

    public ValueTask<bool> DeclineFriendRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        SendFriendCommandAsync(
            HttpMethod.Delete,
            $"api/v1/friends/requests/{accountId:D}",
            cancellationToken);

    public ValueTask<bool> RemoveFriendAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        SendFriendCommandAsync(
            HttpMethod.Delete,
            $"api/v1/friends/{accountId:D}",
            cancellationToken);

    public ValueTask<bool> SetFriendSharingAsync(
        Guid accountId,
        bool shareProgress,
        CancellationToken cancellationToken = default) =>
        SendFriendCommandAsync(
            HttpMethod.Put,
            $"api/v1/friends/{accountId:D}/sharing",
            cancellationToken,
            () => JsonContent.Create(
                new RemoteFriendSharingUpdateRequest(shareProgress),
                RemoteMediaJsonContext.Default
                    .RemoteFriendSharingUpdateRequest));

    public async ValueTask<RemoteFriendVisibility> GetFriendVisibilityAsync(
        CancellationToken cancellationToken = default) =>
        ToVisibility(
            await GetFriendJsonAsync(
                "api/v1/friends/visibility",
                RemoteMediaJsonContext.Default
                    .RemoteFriendVisibilityResponse,
                cancellationToken));

    public async ValueTask<RemoteFriendVisibility> SetFriendVisibilityAsync(
        RemoteFriendVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Put,
            new Uri(session.ServiceBaseUri, "api/v1/friends/visibility"),
            token);
        request.Content = JsonContent.Create(
            new RemoteFriendVisibilityUpdateRequest(
                visibility.ShareProgress,
                visibility.DiscoverableByUsername,
                visibility.HiddenItemKeys.ToArray()),
            RemoteMediaJsonContext.Default
                .RemoteFriendVisibilityUpdateRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        return ToVisibility(
            await ReadJsonAsync(
                response,
                RemoteMediaJsonContext.Default
                    .RemoteFriendVisibilityResponse,
                timeout.Token));
    }

    public async ValueTask<IReadOnlyList<RemoteFriendProgress>>
        LookupFriendProgressAsync(
            IReadOnlyList<string> itemKeys,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemKeys);
        var keys = itemKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
        {
            return [];
        }

        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(
                session.ServiceBaseUri,
                "api/v1/friends/activity/lookup"),
            token);
        request.Content = JsonContent.Create(
            new RemoteFriendActivityLookupRequest(keys),
            RemoteMediaJsonContext.Default.RemoteFriendActivityLookupRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        var lookup = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteFriendActivityLookupResponse,
            timeout.Token);
        return lookup.Matches
            .Select(static match => new RemoteFriendProgress(
                match.AccountId,
                match.DisplayName,
                match.ItemKey,
                match.UnitKind,
                match.DisplayNumber,
                match.SeasonNumber,
                match.IsCompleted,
                match.UpdatedAt))
            .ToArray();
    }

    public async ValueTask<RemoteFriendComparison>
        GetFriendComparisonAsync(
            Guid accountId,
            CancellationToken cancellationToken = default)
    {
        var response = await GetFriendJsonAsync(
            $"api/v1/friends/{accountId:D}/comparison",
            RemoteMediaJsonContext.Default.RemoteFriendComparisonResponse,
            cancellationToken);
        return new RemoteFriendComparison(
            response.InCommon ?? [],
            response.FriendOnly ?? [],
            response.ViewerOnly ?? []);
    }

    public async ValueTask<IReadOnlyList<RemoteSharedTitle>>
        GetSharedTitlesAsync(
            Guid accountId,
            CancellationToken cancellationToken = default)
    {
        var response = await GetFriendJsonAsync(
            $"api/v1/friends/{accountId:D}/shared",
            RemoteMediaJsonContext.Default
                .RemoteFriendSharedTitlesResponse,
            cancellationToken);
        return response.Titles
            .Select(static title => new RemoteSharedTitle(
                title.ItemKey,
                title.Title,
                title.UnitKind,
                title.DisplayNumber,
                title.SeasonNumber,
                title.IsCompleted,
                title.UpdatedAt))
            .ToArray();
    }

    public async ValueTask<RemoteSyncChanges?> GetChangesAsync(
        long sinceRevision,
        CancellationToken cancellationToken = default)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, SyncTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            new Uri(
                session.ServiceBaseUri,
                "api/v1/sync/changes?since=" +
                Math.Max(0, sinceRevision).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            token);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        return await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteSyncChanges,
            timeout.Token,
            _maximumSyncResponseBytes);
    }

    public async ValueTask<RemoteSyncWriteResult> PatchProgressAsync(
        UserSyncDocument patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, SyncTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Patch,
            new Uri(
                session.ServiceBaseUri,
                "api/v1/sync/snapshot/progress"),
            token);
        request.Content = CreateSyncContent(session.ServiceBaseUri,
            new RemoteSyncProgressPatchRequest(patch),
            RemoteMediaJsonContext.Default.RemoteSyncProgressPatchRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        var written = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteSyncWriteResponse,
            timeout.Token);
        return new RemoteSyncWriteResult(
            written.Revision,
            written.UpdatedAt,
            written.UpdatedByDeviceId);
    }

    private HttpContent CreateSyncContent<T>(Uri serviceBaseUri, T value, JsonTypeInfo<T> typeInfo) =>
        _gzipSyncService == serviceBaseUri
            ? new GzipJsonContent<T>(value, typeInfo)
            : JsonContent.Create(value, typeInfo);

    public void Dispose()
    {
        Session = null;
        _accessToken = null;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async ValueTask<RemoteWatchPartyState> CreateWatchPartyAsync(
        RemoteWatchPartyContent? content,
        double positionSeconds,
        CancellationToken cancellationToken = default)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(session.ServiceBaseUri, "api/v1/watch-parties"),
            token);
        request.Content = JsonContent.Create(
            new RemoteWatchPartyCreateRequest(
                ToContentPayload(content),
                positionSeconds),
            RemoteMediaJsonContext.Default.RemoteWatchPartyCreateRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        return ToWatchPartyState(
            await ReadJsonAsync(
                response,
                RemoteMediaJsonContext.Default.RemoteWatchPartyStateResponse,
                timeout.Token));
    }

    public async ValueTask<RemoteWatchPartyState?> JoinWatchPartyAsync(
        string joinCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(joinCode);
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(session.ServiceBaseUri, "api/v1/watch-parties/join"),
            token);
        request.Content = JsonContent.Create(
            new RemoteWatchPartyJoinRequest(joinCode.Trim()),
            RemoteMediaJsonContext.Default.RemoteWatchPartyJoinRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSecureResponse(response);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        return ToWatchPartyState(
            await ReadJsonAsync(
                response,
                RemoteMediaJsonContext.Default.RemoteWatchPartyStateResponse,
                timeout.Token));
    }

    public async ValueTask<RemoteWatchPartyState?> GetCurrentWatchPartyAsync(
        CancellationToken cancellationToken = default)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            new Uri(
                session.ServiceBaseUri,
                "api/v1/watch-parties/current"),
            token);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSecureResponse(response);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        return ToWatchPartyState(
            await ReadJsonAsync(
                response,
                RemoteMediaJsonContext.Default.RemoteWatchPartyStateResponse,
                timeout.Token));
    }

    public ValueTask<bool> LeaveWatchPartyAsync(
        CancellationToken cancellationToken = default) =>
        SendFriendCommandAsync(
            HttpMethod.Delete,
            "api/v1/watch-parties/current",
            cancellationToken);

    public async ValueTask<RemoteWatchPartyState?>
        SetWatchPartyOptionsAsync(
            bool hostControlsOnly,
            CancellationToken cancellationToken = default)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Put,
            new Uri(
                session.ServiceBaseUri,
                "api/v1/watch-parties/current/options"),
            token);
        request.Content = JsonContent.Create(
            new RemoteWatchPartyOptionsRequest(hostControlsOnly),
            RemoteMediaJsonContext.Default.RemoteWatchPartyOptionsRequest);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSecureResponse(response);

        // Not in a party, or not this member's setting to change; both are
        // ordinary answers rather than failures.
        if (response.StatusCode is HttpStatusCode.NotFound or
            HttpStatusCode.Forbidden)
        {
            return null;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        return ToWatchPartyState(
            await ReadJsonAsync(
                response,
                RemoteMediaJsonContext.Default.RemoteWatchPartyStateResponse,
                timeout.Token));
    }

    public async ValueTask<IWatchPartyConnection> ConnectWatchPartyAsync(
        CancellationToken cancellationToken = default)
    {
        var (session, token) = GetRequiredSession();
        var socketUri = new UriBuilder(
            new Uri(session.ServiceBaseUri, "api/v1/watch-parties/socket"))
        {
            Scheme = string.Equals(
                session.ServiceBaseUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        }.Uri;
        var socket = new System.Net.WebSockets.ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader(
                "Authorization",
                "Bearer " + token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            using var timeout = CreateTimeout(
                cancellationToken,
                ShortTimeout);
            await socket.ConnectAsync(socketUri, timeout.Token);
            return new WatchPartyWebSocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static RemoteWatchPartyContentPayload? ToContentPayload(
        RemoteWatchPartyContent? content) =>
        content is null
            ? null
            : new RemoteWatchPartyContentPayload(
                content.MediaType,
                content.Name,
                content.Title,
                content.UnitKind,
                content.SeasonNumber,
                content.DisplayNumber);

    internal static RemoteWatchPartyState ToWatchPartyState(
        RemoteWatchPartyStateResponse value)
    {
        if (value.PartyId == Guid.Empty ||
            string.IsNullOrWhiteSpace(value.JoinCode) ||
            value.Playback is null)
        {
            throw new InvalidDataException(
                "The optional server returned an invalid watch party.");
        }

        return new RemoteWatchPartyState(
            value.PartyId,
            value.JoinCode,
            value.HostAccountId,
            (value.Members ?? [])
                .Select(static member => new RemoteWatchPartyMember(
                    member.AccountId,
                    member.DisplayName,
                    member.IsHost,
                    member.IsConnected,
                    member.IsBuffering))
                .ToArray(),
            value.Content is { } content
                ? new RemoteWatchPartyContent(
                    content.ItemKey,
                    content.MediaType,
                    content.Name,
                    content.Title,
                    content.UnitKind,
                    content.SeasonNumber,
                    content.DisplayNumber)
                : null,
            new RemoteWatchPartyPlayback(
                value.Playback.IsPlaying,
                value.Playback.PositionSeconds,
                value.Playback.UpdatedAt,
                value.Playback.UpdatedByDisplayName),
            value.CreatedAt,
            value.HostControlsOnly);
    }

    private static RemoteFriendVisibility ToVisibility(
        RemoteFriendVisibilityResponse value) =>
        new(
            value.ShareProgress,
            value.DiscoverableByUsername,
            value.HiddenItemKeys ?? []);

    private async ValueTask<T> GetFriendJsonAsync<T>(
        string relativePath,
        JsonTypeInfo<T> responseType,
        CancellationToken cancellationToken)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            new Uri(session.ServiceBaseUri, relativePath),
            token);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        return await ReadJsonAsync(response, responseType, timeout.Token);
    }

    /// <summary>
    /// Sends a friend command whose only interesting answer is whether the
    /// thing it named was there. A 404 is an ordinary outcome — the request was
    /// already withdrawn, the friend already removed — so it reports false
    /// rather than raising.
    /// </summary>
    private async ValueTask<bool> SendFriendCommandAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken,
        Func<HttpContent>? createContent = null)
    {
        var (session, token) = GetRequiredSession();
        using var timeout = CreateTimeout(cancellationToken, ShortTimeout);
        using var request = CreateAuthenticatedRequest(
            method,
            new Uri(session.ServiceBaseUri, relativePath),
            token);
        if (createContent is not null)
        {
            request.Content = createContent();
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSecureResponse(response);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, timeout.Token);
        return true;
    }

    private async ValueTask<RemoteAccountSessionInfo> StartSessionAsync<T>(
        Uri serviceBaseUri,
        string relativePath,
        T payload,
        JsonTypeInfo<T> payloadType,
        CancellationToken cancellationToken)
    {
        var root = NormalizeServiceBaseUri(serviceBaseUri);
        using var timeout = CreateTimeout(
            cancellationToken,
            ShortTimeout);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(root, relativePath))
        {
            Content = JsonContent.Create(payload, payloadType)
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await EnsureSuccessAsync(response, timeout.Token);
        var value = await ReadJsonAsync(
            response,
            RemoteMediaJsonContext.Default.RemoteAccountSessionResponse,
            timeout.Token);
        if (!IsAccessTokenValid(value.AccessToken) ||
            value.AccountId == Guid.Empty ||
            value.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException(
                "The optional server returned an invalid account session.");
        }

        _accessToken = value.AccessToken;
        Session = new RemoteAccountSessionInfo(
            value.AccountId,
            value.Username,
            value.DisplayName,
            value.ExpiresAt,
            root);
        return Session;
    }

    private (RemoteAccountSessionInfo Session, string Token)
        GetRequiredSession() =>
        Session is { } session &&
        !string.IsNullOrWhiteSpace(_accessToken)
            ? (session, _accessToken)
            : throw new InvalidOperationException(
                "Sign in to the optional server first.");

    private static HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        Uri uri,
        string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static RemoteSharedList ToSharedList(
        RemoteSharedListResponse value,
        Uri serviceBaseUri)
    {
        if (!IsShareIdValid(value.ShareId) ||
            string.IsNullOrWhiteSpace(value.CatalogPath) ||
            !value.CatalogPath.StartsWith(
                "/",
                StringComparison.Ordinal) ||
            value.CatalogPath.StartsWith(
                "//",
                StringComparison.Ordinal) ||
            value.CatalogPath.Contains('\\') ||
            value.ListId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The optional server returned an invalid shared list.");
        }

        return new RemoteSharedList(
            value.ShareId,
            value.ListId,
            value.Name,
            value.OwnerDisplayName,
            value.ItemCount,
            value.CreatedAt,
            value.UpdatedAt,
            new Uri(serviceBaseUri, value.CatalogPath));
    }

    private static Uri NormalizeServiceBaseUri(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!RemoteServerSettings.IsSecureServiceUri(value))
        {
            throw new ArgumentException(
                "The optional server must use HTTPS. HTTP is accepted only for loopback development.",
                nameof(value));
        }

        return value.AbsoluteUri.EndsWith('/')
            ? value
            : new Uri(value.AbsoluteUri + '/', UriKind.Absolute);
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static async ValueTask<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken,
        long maximumBytes = MaximumShortResponseBytes)
    {
        EnsureSecureResponse(response);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException(
                "The optional server response is too large.");
        }

        await response.Content.LoadIntoBufferAsync(
            maximumBytes,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        return await System.Text.Json.JsonSerializer.DeserializeAsync(
                   stream,
                   typeInfo,
                   cancellationToken) ??
               throw new InvalidDataException(
                   "The optional server returned an empty response.");
    }

    private static async ValueTask EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        EnsureSecureResponse(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync(
                RemoteMediaJsonContext.Default.RemoteErrorResponse,
                cancellationToken);
            message = error?.Message;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(message)
                ? $"The optional server returned HTTP " +
                  $"{(int)response.StatusCode} ({response.ReasonPhrase})."
                : message,
            inner: null,
            response.StatusCode);
    }

    private static void EnsureSecureResponse(
        HttpResponseMessage response)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        // Configured base addresses cannot contain queries. API endpoints
        // can: delta sync uses ?since= to ask for a particular revision.
        // Keep the scheme, authority, credentials and fragment checks intact.
        var endpointWithoutQuery = responseUri is { IsAbsoluteUri: true }
            ? new UriBuilder(responseUri) { Query = string.Empty }.Uri
            : null;
        if (endpointWithoutQuery is null ||
            !RemoteServerSettings.IsSecureServiceUri(endpointWithoutQuery))
        {
            throw new HttpRequestException(
                "The optional server redirected to an insecure address.");
        }
    }

    private static bool IsAccessTokenValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 43 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_');

    private static bool IsShareIdValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 24 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_');
}

/// <summary>
/// The live watch-party socket over a <see cref="ClientWebSocket"/>. Reads
/// and writes are the caller's loop; this only frames, serializes, and maps.
/// </summary>
internal sealed class WatchPartyWebSocketConnection :
    IWatchPartyConnection
{
    private const int MaximumMessageBytes = 256 * 1024;

    private readonly System.Net.WebSockets.ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _receiveBuffer = new byte[16 * 1024];

    public WatchPartyWebSocketConnection(
        System.Net.WebSockets.ClientWebSocket socket) =>
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));

    public async ValueTask SendAsync(
        RemoteWatchPartyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new RemoteWatchPartyClientMessage(
                command.Type,
                command.PositionSeconds,
                command.Content is { } content
                    ? new RemoteWatchPartyContentPayload(
                        content.MediaType,
                        content.Name,
                        content.Title,
                        content.UnitKind,
                        content.SeasonNumber,
                        content.DisplayNumber)
                    : null,
                command.IsBuffering),
            RemoteMediaJsonContext.Default.RemoteWatchPartyClientMessage);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(
                payload.AsMemory(),
                System.Net.WebSockets.WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask<RemoteWatchPartyEvent?> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        while (_socket.State is System.Net.WebSockets.WebSocketState.Open)
        {
            using var message = new MemoryStream();
            System.Net.WebSockets.ValueWebSocketReceiveResult received;
            do
            {
                received = await _socket.ReceiveAsync(
                    _receiveBuffer.AsMemory(),
                    cancellationToken);
                if (received.MessageType is
                    System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(_receiveBuffer.AsSpan(0, received.Count));
                if (message.Length > MaximumMessageBytes)
                {
                    throw new InvalidDataException(
                        "The watch-party update is too large.");
                }
            }
            while (!received.EndOfMessage);

            RemoteWatchPartyServerMessage? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize(
                    message.GetBuffer().AsSpan(0, (int)message.Length),
                    RemoteMediaJsonContext.Default
                        .RemoteWatchPartyServerMessage);
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed frame is dropped; the next full-state frame
                // carries everything this one would have.
                continue;
            }

            if (parsed is not null)
            {
                return new RemoteWatchPartyEvent(
                    parsed.Reason,
                    parsed.Sequence,
                    parsed.InitiatedByAccountId,
                    parsed.InitiatedByDisplayName,
                    parsed.State is { } state
                        ? HttpRemoteMediaServerClient.ToWatchPartyState(
                            state)
                        : null);
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is
                System.Net.WebSockets.WebSocketState.Open or
                System.Net.WebSockets.WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(3));
                await _socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                    statusDescription: null,
                    timeout.Token);
            }
        }
        catch (Exception)
        {
            // Closing an already-broken socket is not worth surfacing.
        }
        finally
        {
            _socket.Dispose();
            _sendGate.Dispose();
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RemoteServerCapabilities))]
[JsonSerializable(typeof(RemoteAccountRegistrationRequest))]
[JsonSerializable(typeof(RemoteAccountLoginRequest))]
[JsonSerializable(typeof(RemoteAccountSessionResponse))]
[JsonSerializable(typeof(RemoteAccountProfileResponse))]
[JsonSerializable(typeof(RemoteSyncSnapshotResponse))]
[JsonSerializable(typeof(RemoteSyncUploadRequest))]
[JsonSerializable(typeof(RemoteSyncProgressPatchRequest))]
[JsonSerializable(typeof(RemoteSyncWriteResponse))]
[JsonSerializable(typeof(RemoteFriendRequestCreateRequest))]
[JsonSerializable(typeof(RemoteFriendSharingUpdateRequest))]
[JsonSerializable(typeof(RemoteFriendVisibilityUpdateRequest))]
[JsonSerializable(typeof(RemoteFriendVisibilityResponse))]
[JsonSerializable(typeof(RemoteFriendSummaryResponse))]
[JsonSerializable(typeof(RemoteFriendSummaryResponse[]))]
[JsonSerializable(typeof(RemoteFriendRequestResponse))]
[JsonSerializable(typeof(RemoteFriendRequestResponse[]))]
[JsonSerializable(typeof(RemoteFriendActivityLookupRequest))]
[JsonSerializable(typeof(RemoteFriendActivityLookupResponse))]
[JsonSerializable(typeof(RemoteFriendSharedTitleResponse))]
[JsonSerializable(typeof(RemoteFriendSharedTitlesResponse))]
[JsonSerializable(typeof(RemoteFriendComparisonResponse))]
[JsonSerializable(typeof(RemoteSharedListPublishRequest))]
[JsonSerializable(typeof(RemoteSharedListResponse))]
[JsonSerializable(typeof(RemoteSharedListResponse[]))]
[JsonSerializable(typeof(RemoteWatchPartyCreateRequest))]
[JsonSerializable(typeof(RemoteWatchPartyJoinRequest))]
[JsonSerializable(typeof(RemoteWatchPartyOptionsRequest))]
[JsonSerializable(typeof(RemoteWatchPartyStateResponse))]
[JsonSerializable(typeof(RemoteWatchPartyClientMessage))]
[JsonSerializable(typeof(RemoteWatchPartyServerMessage))]
[JsonSerializable(typeof(RemoteErrorResponse))]
[JsonSerializable(typeof(RemoteSyncConflictResponse))]
[JsonSerializable(typeof(UserSyncDocument))]
[JsonSerializable(typeof(RemoteSyncChanges))]
internal sealed partial class RemoteMediaJsonContext :
    JsonSerializerContext;
