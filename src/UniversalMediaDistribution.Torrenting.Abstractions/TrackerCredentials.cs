namespace UniversalMediaDistribution.Torrenting;

public sealed record TrackerCredentialRequest(
    Uri Endpoint,
    TrackerTransport Transport,
    string? CredentialKey = null)
{
    public override string ToString() =>
        $"Credential request for {TrackerEndpointRedactor.Redact(Endpoint)}";
}

public sealed record TrackerCredentialResolution
{
    public TrackerCredentialResolution(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "An authorized tracker endpoint must be absolute.",
                nameof(endpoint));
        }

        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public override string ToString() =>
        $"Authorized tracker {TrackerEndpointRedactor.Redact(Endpoint)}";
}

public interface ITrackerCredentialProvider
{
    ValueTask<TrackerCredentialResolution?> ResolveAsync(
        TrackerCredentialRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableTrackerCredentialProvider : ITrackerCredentialProvider
{
    public static UnavailableTrackerCredentialProvider Instance { get; } = new();

    private UnavailableTrackerCredentialProvider()
    {
    }

    public ValueTask<TrackerCredentialResolution?> ResolveAsync(
        TrackerCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<TrackerCredentialResolution?>(null);
    }
}
