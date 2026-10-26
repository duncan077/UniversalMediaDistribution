namespace UniversalMediaDistribution.Torrenting;

public static class TrackerEndpointRedactor
{
    public static string Redact(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            return "<invalid tracker endpoint>";
        }

        var host = endpoint.HostNameType is UriHostNameType.IPv6
            ? $"[{endpoint.IdnHost}]"
            : endpoint.IdnHost;
        var port = endpoint.IsDefaultPort ? string.Empty : $":{endpoint.Port}";
        return $"{endpoint.Scheme.ToLowerInvariant()}://{host}{port}/…";
    }

    public static string Redact(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            ? Redact(parsed)
            : "<invalid tracker endpoint>";
}
