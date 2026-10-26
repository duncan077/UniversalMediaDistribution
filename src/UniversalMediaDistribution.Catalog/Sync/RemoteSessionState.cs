namespace UniversalMediaDistribution.Catalog.Sync;

/// <summary>
/// A signed-in session kept across application runs so the user does not have
/// to enter their password on every start. The access token is a bearer
/// credential: it is stored apart from <see cref="RemoteServerSettings"/> so it
/// never travels with ordinary settings, and it is never placed in a sync
/// snapshot or a catalog export.
/// </summary>
public sealed record RemoteSessionState
{
    public required string ServiceUrl { get; init; }

    public required string AccessToken { get; init; }

    public required Guid AccountId { get; init; }

    public required string Username { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// When the server stops honouring the token. A stored session past this
    /// point is discarded without a request.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public bool IsUsable(DateTimeOffset now) =>
        AccessToken.Length >= 16 &&
        AccountId != Guid.Empty &&
        ExpiresAt > now &&
        Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) &&
        RemoteServerSettings.IsSecureServiceUri(uri);
}

public interface IRemoteSessionStore
{
    ValueTask<RemoteSessionState?> LoadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Passing <see langword="null"/> forgets the stored session.
    /// </summary>
    ValueTask SaveAsync(
        RemoteSessionState? session,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryRemoteSessionStore(
    RemoteSessionState? initialSession = null) : IRemoteSessionStore
{
    private readonly Lock _gate = new();
    private RemoteSessionState? _session = initialSession;

    public ValueTask<RemoteSessionState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_session);
        }
    }

    public ValueTask SaveAsync(
        RemoteSessionState? session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _session = session;
        }

        return ValueTask.CompletedTask;
    }
}
