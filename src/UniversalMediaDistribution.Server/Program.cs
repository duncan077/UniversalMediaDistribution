using System.Globalization;
using System.Security.Cryptography;
using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using System.Text;
using System.Text.Json.Serialization;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Server;

if (await ServerMaintenance.TryRunAsync(args)) return;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.AddConsole();
builder.Services.AddRequestDecompression();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
var maximumPackageBytes = ReadMaximumPackageBytes();
var maximumJsonMutationBytes = ReadMaximumJsonMutationBytes();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = Math.Max(
        maximumPackageBytes,
        maximumJsonMutationBytes);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(
        0,
        ServerJsonSerializerContext.Default);
});

var dataDirectory = Environment.GetEnvironmentVariable(
    "UMD_SERVER_DATA_DIRECTORY");
if (string.IsNullOrWhiteSpace(dataDirectory))
{
    dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
}

var releaseCatalog = new SqliteReleaseCatalog(dataDirectory);
var packageCatalog = new FilePackageCatalog(
    dataDirectory,
    maximumPackageBytes);
var onlineCatalog = new SqliteOnlineCatalogStore(dataDirectory);
var accountStore = new SqliteAccountStore(
    dataDirectory,
    ReadPasswordIterations(),
    TimeSpan.FromDays(ReadSessionLifetimeDays()));
var accountLoginAttemptLimiter =
    new AccountLoginAttemptLimiter();
var userSyncStore = new SqliteUserSyncStore(dataDirectory);
var sharedListStore = new SqliteSharedListStore(
    dataDirectory,
    userSyncStore);
var friendStore = new SqliteFriendStore(dataDirectory);
var watchPartyRegistry = new WatchPartyRegistry();
var allowAccountRegistration = ReadBoolean(
    "UMD_SERVER_ALLOW_REGISTRATION",
    defaultValue: false);
var adminToken = Environment.GetEnvironmentVariable(
    "UMD_SERVER_ADMIN_TOKEN");
if (!string.IsNullOrWhiteSpace(adminToken) &&
    adminToken.Length < 32)
{
    throw new InvalidOperationException(
        "UMD_SERVER_ADMIN_TOKEN must contain at least 32 characters.");
}

// Fail before reporting healthy if migration or storage is unavailable.
var serverDatabase = new ServerDatabase(dataDirectory);
serverDatabase.Initialize();
var diagnostics = new ServerDiagnostics(serverDatabase);
builder.Services.AddHostedService(_ => new ServerDiagnosticsRetention(diagnostics.Store));
builder.Services.AddSingleton(_ => new ManualCatalogCrawler(serverDatabase));
builder.Services.AddHostedService(services => services.GetRequiredService<ManualCatalogCrawler>());
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin") ||
        context.Request.Path.StartsWithSegments("/api/v1/admin/diagnostics"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; " +
            "base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    }
    Exception? failure = null;
    try { await next(context); }
    catch (Exception exception) { failure = exception; throw; }
    finally { diagnostics.RequestCompleted(context, failure); }
});

ServerDiagnosticsEndpoints.Map(app, diagnostics, adminToken, app.Services.GetRequiredService<ManualCatalogCrawler>());

app.Use(async (context, next) =>
{
    if (IsAccountRequest(context.Request) ||
        IsLatestReleaseRequest(context.Request))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    if (!JsonMutationRequestPolicy.ApplyLimit(
            context,
            maximumJsonMutationBytes))
    {
        return;
    }

    await next(context);
});

app.UseWebSockets();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/v1/sync"), sync =>
{
    sync.UseRequestDecompression();
    sync.UseResponseCompression();
});

app.MapGet(
    "/health",
    () => TypedResults.Ok(new HealthResponse(
        "ok",
        DateTimeOffset.UtcNow)));

app.MapGet(
    "/api/v1/capabilities",
    () => TypedResults.Ok(
        ServerCapabilitiesResponse.Create(
            !string.IsNullOrWhiteSpace(adminToken),
            allowAccountRegistration,
            maximumJsonMutationBytes)));

app.MapPost(
    "/api/v1/accounts/register",
    async (
        AccountRegistrationRequest request,
        CancellationToken cancellationToken) =>
    {
        if (!allowAccountRegistration)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Self-service account registration is disabled.");
        }

        return await RegisterAccountAsync(
            accountStore,
            request,
            cancellationToken);
    });

app.MapPost(
    "/api/v1/admin/accounts",
    async (
        HttpContext context,
        AdminAccountRegistrationRequest request,
        CancellationToken cancellationToken) =>
    {
        var authorization = AdminAuthorization.TryAuthorize(
            context,
            adminToken);
        return authorization ??
            await CreateAdminAccountAsync(
                accountStore,
                request,
                cancellationToken);
    });

app.MapPost(
    "/api/v1/accounts/login",
    async (
        HttpContext context,
        AccountLoginRequest request,
        CancellationToken cancellationToken) =>
    {
        string normalizedUsername;
        try
        {
            normalizedUsername =
                AccountRules.NormalizeUsername(request.Username);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_account_request",
                    exception.Message));
        }

        var sourceAddress = GetClientAddress(context);
        if (accountLoginAttemptLimiter.IsBlocked(
                sourceAddress,
                normalizedUsername,
                out var retryAfter))
        {
            context.Response.Headers.RetryAfter = Math.Max(
                    1,
                    (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return Results.Json(
                new ErrorResponse(
                    "login_rate_limited",
                    "Too many failed sign-in attempts. Try again later."),
                ServerJsonSerializerContext.Default.ErrorResponse,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            var session = await accountStore.LoginAsync(
                request,
                cancellationToken);
            if (session is null)
            {
                accountLoginAttemptLimiter.RecordFailure(
                    sourceAddress,
                    normalizedUsername);
                return Results.Json(
                    new ErrorResponse(
                        "invalid_credentials",
                        "The account name or password is incorrect."),
                    ServerJsonSerializerContext.Default.ErrorResponse,
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            accountLoginAttemptLimiter.RecordSuccess(
                sourceAddress,
                normalizedUsername);
            return Results.Ok(session);
        }
        catch (ArgumentException exception)
        {
            accountLoginAttemptLimiter.RecordFailure(
                sourceAddress,
                normalizedUsername);
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_account_request",
                    exception.Message));
        }
    });

app.MapGet(
    "/api/v1/accounts/me",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        return authenticated is null
            ? Results.Unauthorized()
            : Results.Ok(new AccountProfileResponse(
                authenticated.Account.AccountId,
                authenticated.Account.Username,
                authenticated.Account.DisplayName,
                authenticated.Account.CreatedAt,
                authenticated.Session.ExpiresAt));
    });

app.MapDelete(
    "/api/v1/accounts/session",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var token = AccountRules.GetBearerToken(context);
        var authenticated = await accountStore.AuthenticateAsync(
            token,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        await accountStore.RevokeAsync(token, cancellationToken);
        return Results.NoContent();
    });

app.MapPut(
    "/api/v1/catalog/current",
    async (
        HttpContext context,
        CatalogTransferDocument document,
        CancellationToken cancellationToken) =>
    {
        var authorization = AdminAuthorization.TryAuthorize(
            context,
            adminToken);
        if (authorization is not null)
        {
            return authorization;
        }

        try
        {
            var saved = await onlineCatalog.SaveAsync(
                document,
                cancellationToken);
            return Results.Ok(ToCatalogMetadata(saved));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidDataException or
                CatalogTransferValidationException)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_online_catalog",
                    exception.Message));
        }
    });

app.MapGet(
    "/api/v1/catalog/current",
    async (CancellationToken cancellationToken) =>
    {
        try
        {
            var current = await onlineCatalog.GetAsync(cancellationToken);
            return current is null ? Results.NotFound() : Results.Ok(current.Document);
        }
        catch (CatalogSnapshotTooLargeException exception)
        {
            return Results.Json(new ErrorResponse("catalog_requires_pagination", exception.Message),
                ServerJsonSerializerContext.Default.ErrorResponse, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
    });

app.MapGet(
    "/api/v1/catalog/metadata",
    (CancellationToken cancellationToken) =>
    {
        var metadata = onlineCatalog.Metadata(cancellationToken);
        return metadata is null ? Results.NotFound() : Results.Ok(metadata);
    });

app.MapGet(
    "/api/v1/catalog/search",
    (
        string? q,
        string? mediaType,
        int? limit,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(onlineCatalog.SearchDatabase(q ?? string.Empty, mediaType, limit ?? 100, cancellationToken));
        }
        catch (CatalogSnapshotTooLargeException)
        {
            return Results.Json(new ErrorResponse("catalog_item_too_large", "A catalog item exceeds the response size limit."),
                ServerJsonSerializerContext.Default.ErrorResponse, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidDataException)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_catalog_search",
                    exception.Message));
        }
    });

app.MapGet("/api/v1/catalog/items", (string? cursor, int? limit, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(onlineCatalog.Page(cursor, limit ?? 50, cancellationToken)); }
    catch (ArgumentException exception) { return Results.BadRequest(new ErrorResponse("invalid_catalog_page", exception.Message)); }
    catch (CatalogSnapshotTooLargeException)
    {
        return Results.Json(new ErrorResponse("catalog_item_too_large", "A catalog item exceeds the response size limit."),
            ServerJsonSerializerContext.Default.ErrorResponse, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
});

app.MapGet(
    "/api/v1/sync/snapshot",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var snapshot = await userSyncStore.GetAsync(
            authenticated.Account.AccountId,
            cancellationToken);
        return snapshot is null
            ? Results.NotFound()
            : Results.Ok(new UserSyncSnapshotResponse(
                snapshot.Revision,
                snapshot.UpdatedAt,
                snapshot.UpdatedByDeviceId,
                snapshot.Document));
    });

app.MapGet(
    "/api/v1/sync/changes",
    async (
        HttpContext context,
        long? since,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var changes = await userSyncStore.GetChangesAsync(
            authenticated.Account.AccountId,
            Math.Max(0, since ?? 0),
            cancellationToken);
        return changes is null
            ? Results.NotFound()
            : Results.Ok(changes);
    });

app.MapPatch(
    "/api/v1/sync/snapshot/progress",
    async (
        HttpContext context,
        UserSyncProgressPatchRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var saved = await userSyncStore.ApplyProgressPatchAsync(
                authenticated.Account.AccountId,
                request,
                cancellationToken);

            // Friends read the projection, not the snapshot, so a patch that
            // did not refresh it would move the position without moving what
            // anyone can see — which is the whole reason this endpoint exists.
            try
            {
                await friendStore.SynchronizeAsync(
                    authenticated.Account,
                    saved,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                app.Logger.LogError(
                    exception,
                    "Progress patch {Revision} was applied for account " +
                    "{AccountId}, but friend-visible progress could not be " +
                    "refreshed.",
                    saved.Revision,
                    authenticated.Account.AccountId);
            }

            return Results.Ok(new UserSyncWriteResponse(
                saved.Revision,
                saved.UpdatedAt,
                saved.UpdatedByDeviceId));
        }
        catch (SyncPatchWithoutSnapshotException exception)
        {
            // Not a bad request: the patch is fine, the account simply has
            // nothing to merge it into yet. Answering separately lets a client
            // tell "send the whole thing instead" apart from "this is wrong".
            return Results.Conflict(
                new ErrorResponse(
                    "sync_patch_without_snapshot",
                    exception.Message));
        }
        catch (Exception exception)
            when (exception is InvalidDataException or ArgumentException)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_sync_patch",
                    exception.Message));
        }
    });

app.MapPut(
    "/api/v1/sync/snapshot",
    async (
        HttpContext context,
        UserSyncUploadRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var saved = await userSyncStore.SaveAsync(
                authenticated.Account.AccountId,
                request,
                cancellationToken);
            try
            {
                await sharedListStore.SynchronizeAsync(
                    authenticated.Account,
                    saved,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                app.Logger.LogError(
                    exception,
                    "Snapshot revision {Revision} was saved for account " +
                    "{AccountId}, but one or more shared lists could not " +
                    "be refreshed.",
                    saved.Revision,
                    authenticated.Account.AccountId);
            }

            // Friend-visible progress is derived from the snapshot that just
            // landed, so there is no second upload for the two to disagree
            // about. Failing here must not fail the sync: the snapshot is the
            // real payload and the projection is rebuilt by the next one.
            try
            {
                await friendStore.SynchronizeAsync(
                    authenticated.Account,
                    saved,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                app.Logger.LogError(
                    exception,
                    "Snapshot revision {Revision} was saved for account " +
                    "{AccountId}, but friend-visible progress could not be " +
                    "refreshed.",
                    saved.Revision,
                    authenticated.Account.AccountId);
            }

            return Results.Ok(new UserSyncWriteResponse(
                saved.Revision,
                saved.UpdatedAt,
                saved.UpdatedByDeviceId));
        }
        catch (SyncConflictException exception)
        {
            return Results.Conflict(new SyncConflictResponse(
                "sync_conflict",
                exception.Message,
                exception.CurrentRevision));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidDataException or
                CatalogTransferValidationException)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_sync_snapshot",
                    exception.Message));
        }
    });

app.MapDelete(
    "/api/v1/sync/snapshot",
    async (
        HttpContext context,
        [Microsoft.AspNetCore.Mvc.FromBody] UserSyncDeleteRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            await userSyncStore.DeleteAsync(
                authenticated.Account.AccountId,
                request.ExpectedRevision,
                cancellationToken);
            return Results.NoContent();
        }
        catch (SyncConflictException exception)
        {
            return Results.Conflict(new SyncConflictResponse(
                "sync_conflict",
                exception.Message,
                exception.CurrentRevision));
        }
    });

app.MapPost(
    "/api/v1/shared-lists",
    async (
        HttpContext context,
        SharedListPublishRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var shared = await sharedListStore.PublishAsync(
                authenticated.Account,
                request.ListId,
                cancellationToken);
            return Results.Ok(
                SqliteSharedListStore.ToResponse(shared));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(
                new ErrorResponse(
                    "sync_required",
                    exception.Message));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidDataException)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_shared_list",
                    exception.Message));
        }
    });

app.MapGet(
    "/api/v1/shared-lists",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var records = await sharedListStore.ListAsync(
            authenticated.Account.AccountId,
            cancellationToken);
        return Results.Ok(
            records
                .Select(SqliteSharedListStore.ToResponse)
                .ToArray());
    });

app.MapGet(
    "/api/v1/shared-lists/{shareId}",
    async (
        string shareId,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var record = await sharedListStore.GetAsync(
                shareId,
                cancellationToken);
            return record is null
                ? Results.NotFound()
                : Results.Ok(SqliteSharedListStore.ToResponse(record));
        }
        catch (ArgumentException)
        {
            return Results.NotFound();
        }
    });

app.MapGet(
    "/api/v1/shared-lists/{shareId}/catalog",
    async (
        string shareId,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var record = await sharedListStore.GetAsync(
                shareId,
                cancellationToken);
            return record is null
                ? Results.NotFound()
                : Results.Ok(record.Catalog);
        }
        catch (ArgumentException)
        {
            return Results.NotFound();
        }
    });

app.MapDelete(
    "/api/v1/shared-lists/{shareId}",
    async (
        HttpContext context,
        string shareId,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var removed = await sharedListStore.RevokeAsync(
                authenticated.Account.AccountId,
                shareId,
                cancellationToken);
            return removed
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(
                StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException)
        {
            return Results.NotFound();
        }
    });

app.MapGet(
    "/api/v1/friends",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        return authenticated is null
            ? Results.Unauthorized()
            : Results.Ok(
                (await friendStore.ListFriendsAsync(
                    authenticated.Account.AccountId,
                    cancellationToken)).ToArray());
    });

app.MapGet(
    "/api/v1/friends/requests",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        return authenticated is null
            ? Results.Unauthorized()
            : Results.Ok(
                (await friendStore.ListRequestsAsync(
                    authenticated.Account.AccountId,
                    cancellationToken)).ToArray());
    });

app.MapPost(
    "/api/v1/friends/requests",
    async (
        HttpContext context,
        FriendRequestCreateRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        // Rate limited on the same limiter login uses. Exact-name lookup is
        // still a way to ask whether an account exists, and this keeps that
        // from being asked thousands of times.
        var address = context.Connection.RemoteIpAddress?.ToString() ??
            "unknown";
        if (accountLoginAttemptLimiter.IsBlocked(
                address,
                "friend-request",
                out var retryAfter))
        {
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var target = await accountStore.FindByUsernameAsync(
            request.Username,
            cancellationToken);
        var visibility = target is null
            ? null
            : await friendStore.GetVisibilityAsync(
                target.AccountId,
                cancellationToken);

        // An account that has turned discovery off answers exactly as a
        // missing one does, or the toggle would leak what it exists to hide.
        if (target is null ||
            visibility?.DiscoverableByUsername is not true)
        {
            accountLoginAttemptLimiter.RecordFailure(
                address,
                "friend-request");
            return Results.NotFound(
                new ErrorResponse(
                    "account_not_found",
                    "No account with that name accepts friend requests."));
        }

        try
        {
            var accepted = await friendStore.SendRequestAsync(
                authenticated.Account,
                target,
                cancellationToken);
            return Results.Ok(
                new FriendRequestResponse(
                    target.AccountId,
                    target.Username,
                    target.DisplayName,
                    accepted ? "accepted" : "outgoing",
                    DateTimeOffset.UtcNow));
        }
        catch (FriendConflictException exception)
        {
            return Results.Conflict(
                new ErrorResponse("friend_conflict", exception.Message));
        }
    });

app.MapPost(
    "/api/v1/friends/requests/{accountId:guid}/accept",
    async (
        HttpContext context,
        Guid accountId,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var requester = await accountStore.FindByIdAsync(
            accountId,
            cancellationToken);
        if (requester is null)
        {
            return Results.NotFound();
        }

        var accepted = await friendStore.AcceptRequestAsync(
            authenticated.Account,
            requester,
            cancellationToken);
        return accepted
            ? Results.NoContent()
            : Results.NotFound();
    });

app.MapDelete(
    "/api/v1/friends/requests/{accountId:guid}",
    async (
        HttpContext context,
        Guid accountId,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var removed = await friendStore.DeclineRequestAsync(
            authenticated.Account.AccountId,
            accountId,
            cancellationToken);
        return removed
            ? Results.NoContent()
            : Results.NotFound();
    });

app.MapDelete(
    "/api/v1/friends/{accountId:guid}",
    async (
        HttpContext context,
        Guid accountId,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var removed = await friendStore.RemoveFriendAsync(
            authenticated.Account.AccountId,
            accountId,
            cancellationToken);
        return removed
            ? Results.NoContent()
            : Results.NotFound();
    });

app.MapGet(
    "/api/v1/friends/{accountId:guid}/shared",
    async (
        HttpContext context,
        Guid accountId,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var snapshot = await userSyncStore.GetAsync(
            authenticated.Account.AccountId,
            cancellationToken);
        var titles = await friendStore.ListSharedTitlesAsync(
            authenticated.Account.AccountId,
            accountId,
            snapshot,
            cancellationToken);
        return Results.Ok(
            new FriendSharedTitlesResponse(titles.ToArray()));
    });

app.MapGet(
    "/api/v1/friends/{accountId:guid}/comparison",
    async (
        HttpContext context,
        Guid accountId,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var snapshot = await userSyncStore.GetAsync(
            authenticated.Account.AccountId,
            cancellationToken);
        return Results.Ok(await friendStore.CompareAsync(
            authenticated.Account.AccountId,
            accountId,
            snapshot,
            cancellationToken));
    });

app.MapPut(
    "/api/v1/friends/{accountId:guid}/sharing",
    async (
        HttpContext context,
        Guid accountId,
        FriendSharingUpdateRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var updated = await friendStore.SetFriendSharingAsync(
            authenticated.Account.AccountId,
            accountId,
            request.ShareProgress,
            cancellationToken);
        return updated
            ? Results.NoContent()
            : Results.NotFound();
    });

app.MapGet(
    "/api/v1/friends/visibility",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        return authenticated is null
            ? Results.Unauthorized()
            : Results.Ok(
                await friendStore.GetVisibilityAsync(
                    authenticated.Account.AccountId,
                    cancellationToken));
    });

app.MapPut(
    "/api/v1/friends/visibility",
    async (
        HttpContext context,
        FriendVisibilityUpdateRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(
                await friendStore.SetVisibilityAsync(
                    authenticated.Account.AccountId,
                    request,
                    cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(
                new ErrorResponse("invalid_visibility", exception.Message));
        }
    });

app.MapPost(
    "/api/v1/friends/activity/lookup",
    async (
        HttpContext context,
        FriendActivityLookupRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var matches = await friendStore.LookupActivityAsync(
                authenticated.Account.AccountId,
                request.ItemKeys,
                cancellationToken);
            return Results.Ok(
                new FriendActivityLookupResponse(matches.ToArray()));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(
                new ErrorResponse("invalid_lookup", exception.Message));
        }
    });

app.MapPost(
    "/api/v1/watch-parties",
    async (
        HttpContext context,
        WatchPartyCreateRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(watchPartyRegistry.Create(
                authenticated.Account,
                request));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "invalid_watch_party",
                    exception.Message));
        }
    });

app.MapPost(
    "/api/v1/watch-parties/join",
    async (
        HttpContext context,
        WatchPartyJoinRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var joined = watchPartyRegistry.Join(
                authenticated.Account,
                request.JoinCode);
            return joined is null
                ? Results.NotFound(
                    new ErrorResponse(
                        "watch_party_not_found",
                        "No watch party uses that code."))
                : Results.Ok(joined);
        }
        catch (WatchPartyConflictException exception)
        {
            return Results.Conflict(
                new ErrorResponse(
                    "watch_party_full",
                    exception.Message));
        }
    });

app.MapGet(
    "/api/v1/watch-parties/current",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        var current = watchPartyRegistry.Get(
            authenticated.Account.AccountId);
        return current is null
            ? Results.NotFound()
            : Results.Ok(current);
    });

app.MapDelete(
    "/api/v1/watch-parties/current",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        return watchPartyRegistry.Leave(authenticated.Account.AccountId)
            ? Results.NoContent()
            : Results.NotFound();
    });

app.MapPut(
    "/api/v1/watch-parties/current/options",
    async (
        HttpContext context,
        WatchPartyOptionsRequest request,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var updated = watchPartyRegistry.SetOptions(
                authenticated.Account.AccountId,
                request);
            return updated is null
                ? Results.NotFound()
                : Results.Ok(updated);
        }
        catch (WatchPartyConflictException)
        {
            // Only the host may change this, and a member who asks is not
            // told anything they could not already see.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    });

app.MapGet(
    "/api/v1/watch-parties/socket",
    async (
        HttpContext context,
        CancellationToken cancellationToken) =>
    {
        var authenticated = await accountStore.AuthenticateAsync(
            context,
            cancellationToken);
        if (authenticated is null)
        {
            return Results.Unauthorized();
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(
                new ErrorResponse(
                    "websocket_required",
                    "The watch-party socket requires a WebSocket upgrade."));
        }

        using var socket = await context.WebSockets
            .AcceptWebSocketAsync();
        try
        {
            await watchPartyRegistry.ConnectAsync(
                authenticated.Account,
                socket,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
                System.Net.WebSockets.WebSocketException)
        {
            // A member closing the app, a dropped connection, or server
            // shutdown all end the socket the same way.
        }

        return Results.Empty;
    });

app.MapGet(
    "/api/v1/releases/{channel}/{runtimeIdentifier}/latest",
    async (
        string channel,
        string runtimeIdentifier,
        CancellationToken cancellationToken) =>
    {
        var manifest = await releaseCatalog.GetLatestAsync(
            channel,
            runtimeIdentifier,
            cancellationToken);
        return manifest is null
            ? Results.NotFound()
            : Results.Ok(manifest);
    });

app.MapPut(
    "/api/v1/releases/{channel}/{runtimeIdentifier}/latest",
    async (
        HttpContext context,
        string channel,
        string runtimeIdentifier,
        ReleaseManifest manifest,
        CancellationToken cancellationToken) =>
    {
        var authorization = AdminAuthorization.TryAuthorize(
            context,
            adminToken);
        if (authorization is not null)
        {
            return authorization;
        }

        try
        {
            await releaseCatalog.SaveLatestAsync(
                channel,
                runtimeIdentifier,
                manifest,
                cancellationToken);
            return Results.Ok(manifest);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidDataException)
        {
            return Results.BadRequest(
                new ErrorResponse("invalid_release_manifest", exception.Message));
        }
    });

app.MapPut(
    "/api/v1/releases/{channel}/{runtimeIdentifier}/{version}/package/{fileName}",
    async (
        HttpContext context,
        string channel,
        string runtimeIdentifier,
        string version,
        string fileName,
        CancellationToken cancellationToken) =>
    {
        var authorization = AdminAuthorization.TryAuthorize(
            context,
            adminToken);
        if (authorization is not null)
        {
            return authorization;
        }

        try
        {
            var expectedSha256 =
                context.Request.Headers["X-UMD-SHA256"].ToString();
            var result = await packageCatalog.SaveAsync(
                channel,
                runtimeIdentifier,
                version,
                fileName,
                context.Request.Body,
                context.Request.ContentLength,
                expectedSha256,
                cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidDataException)
        {
            return Results.BadRequest(
                new ErrorResponse("invalid_release_package", exception.Message));
        }
    });

app.MapGet(
    "/api/v1/releases/{channel}/{runtimeIdentifier}/{version}/package/{fileName}",
    (
        string channel,
        string runtimeIdentifier,
        string version,
        string fileName) =>
    {
        try
        {
            var package = packageCatalog.Get(
                channel,
                runtimeIdentifier,
                version,
                fileName);
            return package is null
                ? Results.NotFound()
                : Results.File(
                    package.Path,
                    "application/octet-stream",
                    package.FileName,
                    enableRangeProcessing: true);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(
                new ErrorResponse("invalid_package_path", exception.Message));
        }
    });

app.Run();

static async ValueTask<IResult> RegisterAccountAsync(
    SqliteAccountStore accountStore,
    AccountRegistrationRequest request,
    CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await accountStore.RegisterAsync(
            request,
            cancellationToken));
    }
    catch (AccountConflictException exception)
    {
        return Results.Conflict(
            new ErrorResponse(
                "account_name_unavailable",
                exception.Message));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(
            new ErrorResponse(
                "invalid_account_request",
                exception.Message));
    }
}

static async ValueTask<IResult> CreateAdminAccountAsync(
    SqliteAccountStore accountStore,
    AdminAccountRegistrationRequest request,
    CancellationToken cancellationToken)
{
    try
    {
        var account = await accountStore.CreateAsync(
            request,
            cancellationToken);
        return Results.Ok(new AccountCreatedResponse(
            account.AccountId,
            account.Username,
            account.DisplayName,
            account.CreatedAt));
    }
    catch (AccountConflictException exception)
    {
        return Results.Conflict(
            new ErrorResponse(
                "account_name_unavailable",
                exception.Message));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(
            new ErrorResponse(
                "invalid_account_request",
                exception.Message));
    }
}

static OnlineCatalogMetadataResponse ToCatalogMetadata(
    OnlineCatalogState state) =>
    new(
        state.Revision,
        state.UpdatedAt,
        state.Sha256,
        state.Document.Items.Count);

static bool IsAccountRequest(HttpRequest request) =>
    request.Path.StartsWithSegments("/api/v1/accounts") ||
    request.Path.StartsWithSegments("/api/v1/admin/accounts");

static bool IsLatestReleaseRequest(HttpRequest request) =>
    request.Path.StartsWithSegments("/api/v1/releases") &&
    request.Path.Value?.EndsWith(
        "/latest",
        StringComparison.OrdinalIgnoreCase) is true;

static string GetClientAddress(HttpContext context) =>
    context.Connection.RemoteIpAddress?
        .MapToIPv6()
        .ToString() ??
    "unknown";

static long ReadMaximumPackageBytes()
{
    const long defaultValue = 2L * 1024 * 1024 * 1024;
    var configured = Environment.GetEnvironmentVariable(
        "UMD_SERVER_MAX_PACKAGE_BYTES");
    if (string.IsNullOrWhiteSpace(configured))
    {
        return defaultValue;
    }

    return long.TryParse(configured, out var value) && value > 0
        ? value
        : throw new InvalidOperationException(
            "UMD_SERVER_MAX_PACKAGE_BYTES must be a positive integer.");
}

static long ReadMaximumJsonMutationBytes()
{
    const long defaultValue = 40L * 1024 * 1024;
    var configured = Environment.GetEnvironmentVariable(
        "UMD_SERVER_MAX_JSON_BYTES");
    return string.IsNullOrWhiteSpace(configured)
        ? defaultValue
        : long.TryParse(configured, out var value) &&
          value is >= 1024 and <= 256L * 1024 * 1024
            ? value
            : throw new InvalidOperationException(
                "UMD_SERVER_MAX_JSON_BYTES must be between 1 KiB and 256 MiB.");
}

static int ReadPasswordIterations()
{
    var configured = Environment.GetEnvironmentVariable(
        "UMD_SERVER_PASSWORD_ITERATIONS");
    return string.IsNullOrWhiteSpace(configured)
        ? SqliteAccountStore.DefaultPasswordIterations
        : int.TryParse(configured, out var value) &&
          value is >= 100_000 and <= 2_000_000
            ? value
            : throw new InvalidOperationException(
                "UMD_SERVER_PASSWORD_ITERATIONS must be between 100000 and 2000000.");
}

static int ReadSessionLifetimeDays()
{
    var configured = Environment.GetEnvironmentVariable(
        "UMD_SERVER_SESSION_DAYS");
    return string.IsNullOrWhiteSpace(configured)
        ? 30
        : int.TryParse(configured, out var value) &&
          value is >= 1 and <= 365
            ? value
            : throw new InvalidOperationException(
                "UMD_SERVER_SESSION_DAYS must be between 1 and 365.");
}

static bool ReadBoolean(string name, bool defaultValue)
{
    var configured = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(configured))
    {
        return defaultValue;
    }

    return bool.TryParse(configured, out var value)
        ? value
        : throw new InvalidOperationException(
            $"{name} must be true or false.");
}

namespace UniversalMediaDistribution.Server
{
    internal sealed record HealthResponse(
        string Status,
        DateTimeOffset ServerTime);

    internal sealed record ServerCapabilitiesResponse(
        int ApiVersion,
        bool ReleaseNotifications,
        bool ReleaseUploads,
        bool Accounts,
        bool AccountRegistration,
        bool OnlineCatalog,
        bool BackupSync,
        bool SharedLists,
        bool Friends,
        bool ProgressPatch,
        bool DeltaSync,
        int SyncProtocolVersion,
        long MaximumSyncBytes,
        bool WatchParties,
        bool GzipSyncRequests)
    {
        public static ServerCapabilitiesResponse Create(
            bool releaseUploads,
            bool accountRegistration,
            long maximumSyncBytes) =>
            new(
                ApiVersion: 1,
                ReleaseNotifications: true,
                ReleaseUploads: releaseUploads,
                Accounts: true,
                AccountRegistration: accountRegistration,
                OnlineCatalog: true,
                BackupSync: true,
                SharedLists: true,
                Friends: true,
                ProgressPatch: true,
                DeltaSync: true,
                SyncProtocolVersion:
                    UserSyncDocument.CurrentVersion,
                MaximumSyncBytes: maximumSyncBytes,
                WatchParties: true,
                GzipSyncRequests: true);
    }

    internal sealed record ErrorResponse(string Code, string Message);

    internal sealed record PackageUploadResponse(
        string RelativePath,
        string Sha256,
        long SizeBytes);

    internal sealed record ReleaseManifest(
        string Version,
        string Channel,
        string RuntimeIdentifier,
        Uri DownloadUri,
        string Sha256,
        long SizeBytes,
        DateTimeOffset PublishedAt,
        string SigningKeyId,
        string SignatureAlgorithm,
        string Signature,
        string? ReleaseNotes);

    internal sealed class SqliteReleaseCatalog
    {
        private readonly string _releaseRoot;
        private readonly ServerDatabase _storage;

        public SqliteReleaseCatalog(string dataDirectory)
        {
            _storage = new ServerDatabase(dataDirectory);
            _releaseRoot = Path.GetFullPath(Path.Combine(dataDirectory, "releases"));
        }

        public async ValueTask<ReleaseManifest?> GetLatestAsync(string channel, string runtimeIdentifier, CancellationToken cancellationToken)
        {
            var normalizedChannel = NormalizePathSegment(channel, nameof(channel));
            var normalizedRuntime = NormalizePathSegment(runtimeIdentifier, nameof(runtimeIdentifier));
            var manifest = await _storage.ReadJsonAsync(GetLatestPath(normalizedChannel, normalizedRuntime),
                ServerJsonSerializerContext.Default.ReleaseManifest, cancellationToken);
            return manifest is null ? null : Validate(manifest, normalizedChannel, normalizedRuntime);
        }

        public ValueTask SaveLatestAsync(string channel, string runtimeIdentifier, ReleaseManifest manifest, CancellationToken cancellationToken)
        {
            var normalizedChannel = NormalizePathSegment(channel, nameof(channel));
            var normalizedRuntime = NormalizePathSegment(runtimeIdentifier, nameof(runtimeIdentifier));
            return _storage.WriteJsonAtomicAsync(GetLatestPath(normalizedChannel, normalizedRuntime),
                Validate(manifest, normalizedChannel, normalizedRuntime), ServerJsonSerializerContext.Default.ReleaseManifest, cancellationToken);
        }

        private string GetLatestPath(
            string normalizedChannel,
            string normalizedRuntimeIdentifier)
        {
            var path = Path.GetFullPath(
                Path.Combine(
                    _releaseRoot,
                    normalizedChannel,
                    normalizedRuntimeIdentifier,
                    "latest.json"));
            if (!path.StartsWith(
                    _releaseRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The release manifest path is outside the release directory.");
            }

            return path;
        }

        internal static ReleaseManifest Validate(
            ReleaseManifest? manifest,
            string expectedChannel,
            string expectedRuntimeIdentifier)
        {
            if (manifest is null ||
                string.IsNullOrWhiteSpace(manifest.Version) ||
                !string.Equals(
                    manifest.Channel,
                    expectedChannel,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    manifest.RuntimeIdentifier,
                    expectedRuntimeIdentifier,
                    StringComparison.OrdinalIgnoreCase) ||
                !manifest.DownloadUri.IsAbsoluteUri ||
                manifest.DownloadUri.Scheme is not "https" ||
                manifest.Sha256.Length != 64 ||
                !manifest.Sha256.All(Uri.IsHexDigit) ||
                 manifest.SizeBytes <= 0 ||
                 string.IsNullOrWhiteSpace(manifest.SigningKeyId) ||
                 !string.Equals(
                     manifest.SignatureAlgorithm,
                     "ECDSA-P256-SHA256",
                     StringComparison.Ordinal) ||
                 !HasValidSignatureShape(manifest.Signature))
            {
                throw new InvalidDataException(
                    "The latest release manifest is invalid.");
            }

            return manifest;
        }

        private static bool HasValidSignatureShape(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            Span<byte> signature = stackalloc byte[64];
            return Convert.TryFromBase64String(
                       value,
                       signature,
                       out var bytesWritten) &&
                   bytesWritten == signature.Length;
        }

        internal static string NormalizePathSegment(
            string value,
            string parameterName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var normalized = value.Trim().ToLowerInvariant();
            if (normalized.Length > 64 ||
                normalized.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not ('.' or '-')))
            {
                throw new ArgumentException(
                    "Only ASCII letters, digits, dots, and hyphens are allowed.",
                    parameterName);
            }

            return normalized;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A failed temporary-file cleanup is harmless.
            }
        }
    }

    internal sealed class FilePackageCatalog
    {
        private readonly string _packageRoot;
        private readonly long _maximumPackageBytes;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public FilePackageCatalog(
            string dataDirectory,
            long maximumPackageBytes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
            if (maximumPackageBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPackageBytes));
            }

            _packageRoot = Path.GetFullPath(
                Path.Combine(dataDirectory, "packages"));
            _maximumPackageBytes = maximumPackageBytes;
        }

        public async ValueTask<PackageUploadResponse> SaveAsync(
            string channel,
            string runtimeIdentifier,
            string version,
            string fileName,
            Stream source,
            long? contentLength,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            var normalizedChannel = SqliteReleaseCatalog.NormalizePathSegment(
                channel,
                nameof(channel));
            var normalizedRuntimeIdentifier =
                SqliteReleaseCatalog.NormalizePathSegment(
                    runtimeIdentifier,
                    nameof(runtimeIdentifier));
            var normalizedVersion = SqliteReleaseCatalog.NormalizePathSegment(
                version.TrimStart('v', 'V'),
                nameof(version));
            var normalizedFileName = NormalizeFileName(fileName);
            if (contentLength is not > 0 ||
                contentLength > _maximumPackageBytes)
            {
                throw new InvalidDataException(
                    $"The package must declare a size between 1 and " +
                    $"{_maximumPackageBytes} bytes.");
            }
            var declaredSize = contentLength.GetValueOrDefault();

            if (string.IsNullOrWhiteSpace(expectedSha256) ||
                expectedSha256.Length != 64 ||
                !expectedSha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    "X-UMD-SHA256 must contain the package SHA-256 checksum.");
            }

            var finalPath = ResolvePath(
                normalizedChannel,
                normalizedRuntimeIdentifier,
                normalizedVersion,
                normalizedFileName);
            var directory = Path.GetDirectoryName(finalPath) ??
                throw new InvalidOperationException(
                    "The package has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = finalPath + "." +
                Guid.CreateVersion7().ToString("N") +
                ".tmp";

            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                try
                {
                    var (actualSha256, actualSize) =
                        await CopyAndHashAsync(
                            source,
                            temporaryPath,
                            declaredSize,
                            cancellationToken);
                    if (!string.Equals(
                            actualSha256,
                            expectedSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "The uploaded package checksum does not match X-UMD-SHA256.");
                    }

                    File.Move(temporaryPath, finalPath, overwrite: true);
                    var relativePath = string.Join(
                        '/',
                        "api",
                        "v1",
                        "releases",
                        normalizedChannel,
                        normalizedRuntimeIdentifier,
                        normalizedVersion,
                        "package",
                        normalizedFileName);
                    return new PackageUploadResponse(
                        "/" + relativePath,
                        actualSha256,
                        actualSize);
                }
                catch
                {
                    TryDelete(temporaryPath);
                    throw;
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public StoredPackage? Get(
            string channel,
            string runtimeIdentifier,
            string version,
            string fileName)
        {
            var normalizedChannel = SqliteReleaseCatalog.NormalizePathSegment(
                channel,
                nameof(channel));
            var normalizedRuntimeIdentifier =
                SqliteReleaseCatalog.NormalizePathSegment(
                    runtimeIdentifier,
                    nameof(runtimeIdentifier));
            var normalizedVersion = SqliteReleaseCatalog.NormalizePathSegment(
                version.TrimStart('v', 'V'),
                nameof(version));
            var normalizedFileName = NormalizeFileName(fileName);
            var path = ResolvePath(
                normalizedChannel,
                normalizedRuntimeIdentifier,
                normalizedVersion,
                normalizedFileName);
            return File.Exists(path)
                ? new StoredPackage(path, normalizedFileName)
                : null;
        }

        private async ValueTask<(string Sha256, long SizeBytes)>
            CopyAndHashAsync(
                Stream source,
                string destinationPath,
                long expectedSize,
                CancellationToken cancellationToken)
        {
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long totalBytes = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(
                    buffer,
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes += bytesRead;
                if (totalBytes > expectedSize ||
                    totalBytes > _maximumPackageBytes)
                {
                    throw new InvalidDataException(
                        "The uploaded package is larger than its declared size.");
                }

                hash.AppendData(buffer.AsSpan(0, bytesRead));
                await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
            if (totalBytes != expectedSize)
            {
                throw new InvalidDataException(
                    "The uploaded package is incomplete.");
            }

            return (
                Convert.ToHexString(hash.GetHashAndReset()),
                totalBytes);
        }

        private string ResolvePath(
            string channel,
            string runtimeIdentifier,
            string version,
            string fileName)
        {
            var path = Path.GetFullPath(
                Path.Combine(
                    _packageRoot,
                    channel,
                    runtimeIdentifier,
                    version,
                    fileName));
            if (!path.StartsWith(
                    _packageRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The package path is outside the package directory.");
            }

            return path;
        }

        private static string NormalizeFileName(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var normalized = value.Trim();
            if (normalized.Length > 160 ||
                normalized is "." or ".." ||
                !string.Equals(
                    normalized,
                    Path.GetFileName(normalized),
                    StringComparison.Ordinal) ||
                normalized.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not ('.' or '-' or '_')))
            {
                throw new ArgumentException(
                    "The package file name contains unsupported characters.",
                    nameof(value));
            }

            return normalized;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A failed temporary-file cleanup is harmless.
            }
        }
    }

    internal sealed record StoredPackage(
        string Path,
        string FileName);

    internal static class AdminAuthorization
    {
        public static IResult? TryAuthorize(
            HttpContext context,
            string? configuredToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (string.IsNullOrWhiteSpace(configuredToken))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Administrative operations are not configured.");
            }

            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!header.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Unauthorized();
            }

            var suppliedToken = header[prefix.Length..].Trim();
            var expectedHash = SHA256.HashData(
                Encoding.UTF8.GetBytes(configuredToken));
            var suppliedHash = SHA256.HashData(
                Encoding.UTF8.GetBytes(suppliedToken));
            return CryptographicOperations.FixedTimeEquals(
                expectedHash,
                suppliedHash)
                ? null
                : Results.Unauthorized();
        }
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(HealthResponse))]
    [JsonSerializable(typeof(ServerCapabilitiesResponse))]
    [JsonSerializable(typeof(ErrorResponse))]
    [JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
    [JsonSerializable(typeof(ServerDiagnosticSnapshot))]
    [JsonSerializable(typeof(ServerDiagnosticLogPage))]
    [JsonSerializable(typeof(ManualCrawlRequest))]
    [JsonSerializable(typeof(ManualCrawlResponse))]
    [JsonSerializable(typeof(ProviderIntervalRequest))]
    [JsonSerializable(typeof(ProviderIntervalResponse))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(PackageUploadResponse))]
    [JsonSerializable(typeof(ReleaseManifest))]
    [JsonSerializable(typeof(AccountRegistrationRequest))]
    [JsonSerializable(typeof(AdminAccountRegistrationRequest))]
    [JsonSerializable(typeof(AccountLoginRequest))]
    [JsonSerializable(typeof(AccountSessionResponse))]
    [JsonSerializable(typeof(AccountCreatedResponse))]
    [JsonSerializable(typeof(AccountProfileResponse))]
    [JsonSerializable(typeof(AccountIndexRecord))]
    [JsonSerializable(typeof(AccountRecord))]
    [JsonSerializable(typeof(AccountSessionRecord))]
    [JsonSerializable(typeof(CatalogTransferDocument))]
    [JsonSerializable(typeof(OnlineCatalogState))]
    [JsonSerializable(typeof(CatalogPageResponse))]
    [JsonSerializable(typeof(CatalogTransferItem))]
    [JsonSerializable(typeof(OnlineCatalogMetadataResponse))]
    [JsonSerializable(typeof(UserSyncDocument))]
    [JsonSerializable(typeof(UserSyncChangesResponse))]
    [JsonSerializable(typeof(UserSyncSnapshotState))]
    [JsonSerializable(typeof(UserSyncSnapshotResponse))]
    [JsonSerializable(typeof(UserSyncUploadRequest))]
    [JsonSerializable(typeof(UserSyncProgressPatchRequest))]
    [JsonSerializable(typeof(UserSyncDeleteRequest))]
    [JsonSerializable(typeof(UserSyncWriteResponse))]
    [JsonSerializable(typeof(SyncConflictResponse))]
    [JsonSerializable(typeof(SharedListPublishRequest))]
    [JsonSerializable(typeof(SharedListOwnerIndex))]
    [JsonSerializable(typeof(SharedListRecord))]
    [JsonSerializable(typeof(SharedListResponse))]
    [JsonSerializable(typeof(SharedListResponse[]))]
    [JsonSerializable(typeof(FriendRequestCreateRequest))]
    [JsonSerializable(typeof(FriendVisibilityUpdateRequest))]
    [JsonSerializable(typeof(FriendSharingUpdateRequest))]
    [JsonSerializable(typeof(FriendActivityLookupRequest))]
    [JsonSerializable(typeof(FriendActivityLookupResponse))]
    [JsonSerializable(typeof(FriendSharedTitleResponse))]
    [JsonSerializable(typeof(FriendSharedTitlesResponse))]
    [JsonSerializable(typeof(FriendComparisonResponse))]
    [JsonSerializable(typeof(FriendRequestRecord))]
    [JsonSerializable(typeof(FriendEdgeRecord))]
    [JsonSerializable(typeof(FriendVisibilityRecord))]
    [JsonSerializable(typeof(FriendActivityRecord))]
    [JsonSerializable(typeof(FriendSummaryResponse))]
    [JsonSerializable(typeof(FriendSummaryResponse[]))]
    [JsonSerializable(typeof(FriendRequestResponse))]
    [JsonSerializable(typeof(FriendRequestResponse[]))]
    [JsonSerializable(typeof(WatchPartyCreateRequest))]
    [JsonSerializable(typeof(WatchPartyJoinRequest))]
    [JsonSerializable(typeof(WatchPartyOptionsRequest))]
    [JsonSerializable(typeof(WatchPartyContentRequest))]
    [JsonSerializable(typeof(WatchPartyStateResponse))]
    [JsonSerializable(typeof(WatchPartyClientMessage))]
    [JsonSerializable(typeof(WatchPartyServerMessage))]
    internal sealed partial class ServerJsonSerializerContext :
        JsonSerializerContext;
}
