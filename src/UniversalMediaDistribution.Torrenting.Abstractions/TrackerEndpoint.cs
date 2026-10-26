namespace UniversalMediaDistribution.Torrenting;

public sealed record TrackerEndpoint
{
    public TrackerEndpoint(
        Uri endpoint,
        TrackerTransport transport,
        int tier = 0,
        int sortOrder = 0,
        bool usesCredential = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("Tracker endpoint must be absolute.", nameof(endpoint));
        }

        if (tier < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tier));
        }

        if (sortOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }

        Endpoint = endpoint;
        Transport = transport;
        Tier = tier;
        SortOrder = sortOrder;
        UsesCredential = usesCredential;
    }

    public Uri Endpoint { get; }

    public TrackerTransport Transport { get; }

    public int Tier { get; }

    public int SortOrder { get; }

    public bool UsesCredential { get; }

    public string SafeDisplay => TrackerEndpointRedactor.Redact(Endpoint);

    public override string ToString() =>
        $"{Transport} tracker {SafeDisplay} (tier {Tier}, order {SortOrder})";
}
