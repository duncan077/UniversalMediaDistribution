using System.Text;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class EnvironmentTrackerCredentialProvider(
    Func<string, string?>? environmentVariableReader = null) : ITrackerCredentialProvider
{
    public const string VariablePrefix = "UMD_TRACKER_ENDPOINT_";

    private readonly Func<string, string?> _environmentVariableReader =
        environmentVariableReader ?? Environment.GetEnvironmentVariable;

    public ValueTask<TrackerCredentialResolution?> ResolveAsync(
        TrackerCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Transport is not (TrackerTransport.Https or TrackerTransport.Wss))
        {
            return ValueTask.FromResult<TrackerCredentialResolution?>(null);
        }

        var variableName = GetEnvironmentVariableName(request);
        var value = _environmentVariableReader(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return ValueTask.FromResult<TrackerCredentialResolution?>(null);
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var authorizedEndpoint) ||
            !string.Equals(
                authorizedEndpoint.Scheme,
                request.Endpoint.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                authorizedEndpoint.IdnHost,
                request.Endpoint.IdnHost,
                StringComparison.OrdinalIgnoreCase) ||
            authorizedEndpoint.Port != request.Endpoint.Port ||
            !string.IsNullOrEmpty(authorizedEndpoint.Fragment))
        {
            throw new InvalidOperationException(
                "A local tracker credential resolved to an invalid or unexpected endpoint.");
        }

        return ValueTask.FromResult<TrackerCredentialResolution?>(
            new TrackerCredentialResolution(authorizedEndpoint));
    }

    public static string GetEnvironmentVariableName(TrackerCredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lookupKey = string.IsNullOrWhiteSpace(request.CredentialKey)
            ? $"{request.Transport}_{request.Endpoint.IdnHost}" +
              (request.Endpoint.IsDefaultPort ? string.Empty : $"_{request.Endpoint.Port}")
            : request.CredentialKey.Trim();
        var normalized = new StringBuilder(lookupKey.Length);
        var lastWasSeparator = false;
        foreach (var character in lookupKey)
        {
            if (character is >= 'a' and <= 'z')
            {
                normalized.Append(char.ToUpperInvariant(character));
                lastWasSeparator = false;
            }
            else if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                normalized.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && normalized.Length > 0)
            {
                normalized.Append('_');
                lastWasSeparator = true;
            }
        }

        var normalizedKey = normalized.ToString().TrimEnd('_');
        if (normalizedKey.Length == 0)
        {
            throw new InvalidOperationException(
                "The tracker credential lookup key is invalid.");
        }

        return VariablePrefix + normalizedKey;
    }
}
