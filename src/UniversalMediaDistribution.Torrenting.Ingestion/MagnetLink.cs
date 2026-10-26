using System.Text;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class MagnetLink
{
    public const int MaximumLength = 32 * 1024;
    public const int MaximumTrackers = 64;

    private MagnetLink(
        string originalUri,
        string? infoHashV1,
        string? infoHashV2,
        string? displayName,
        bool declaresPrivate,
        IReadOnlyList<Uri> trackerUris)
    {
        OriginalUri = originalUri;
        InfoHashV1 = infoHashV1;
        InfoHashV2 = infoHashV2;
        DisplayName = displayName;
        DeclaresPrivate = declaresPrivate;
        TrackerUris = trackerUris;
        CatalogIdentityUri = BuildCatalogIdentityUri(infoHashV1, infoHashV2, displayName);
    }

    public string OriginalUri { get; }

    public string CatalogIdentityUri { get; }

    public string? InfoHashV1 { get; }

    public string? InfoHashV2 { get; }

    public string? DisplayName { get; }

    public bool DeclaresPrivate { get; }

    public IReadOnlyList<Uri> TrackerUris { get; }

    public string StableIdentity => InfoHashV2 is not null
        ? $"v2:{InfoHashV2}"
        : $"v1:{InfoHashV1}";

    public static MagnetLink Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        if (trimmed.Length > MaximumLength)
        {
            throw new ArgumentException($"Magnet link exceeds the {MaximumLength:N0}-character limit.", nameof(value));
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The supplied value is not a magnet URI.", nameof(value));
        }

        if (trimmed.Contains('#', StringComparison.Ordinal))
        {
            throw new ArgumentException("Magnet URI fragments are not supported.", nameof(value));
        }

        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex < 0 || queryIndex == trimmed.Length - 1)
        {
            throw new ArgumentException("Magnet URI does not contain any parameters.", nameof(value));
        }

        string? infoHashV1 = null;
        string? infoHashV2 = null;
        string? displayName = null;
        var declaresPrivate = false;
        var trackers = new List<Uri>();
        var trackerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameterCount = 0;
        var trackerParameterCount = 0;

        foreach (var segment in trimmed[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            parameterCount++;
            if (parameterCount > 256)
            {
                throw new ArgumentException("Magnet URI contains too many parameters.", nameof(value));
            }

            var separator = segment.IndexOf('=');
            var key = Decode(separator < 0 ? segment : segment[..separator]);
            var parameterValue = Decode(separator < 0 ? string.Empty : segment[(separator + 1)..]);

            if (string.Equals(key, "xt", StringComparison.OrdinalIgnoreCase))
            {
                if (TryNormalizeV1ExactTopic(parameterValue, out var v1))
                {
                    if (infoHashV1 is not null && !string.Equals(infoHashV1, v1, StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Magnet URI contains conflicting BitTorrent v1 identities.", nameof(value));
                    }

                    infoHashV1 = v1;
                }
                else if (TryNormalizeV2ExactTopic(parameterValue, out var v2))
                {
                    if (infoHashV2 is not null && !string.Equals(infoHashV2, v2, StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Magnet URI contains conflicting BitTorrent v2 identities.", nameof(value));
                    }

                    infoHashV2 = v2;
                }
            }
            else if (string.Equals(key, "dn", StringComparison.OrdinalIgnoreCase) && displayName is null)
            {
                displayName = string.IsNullOrWhiteSpace(parameterValue) ? null : parameterValue.Trim();
            }
            else if (string.Equals(key, "tr", StringComparison.OrdinalIgnoreCase))
            {
                trackerParameterCount++;
                if (trackerParameterCount > MaximumTrackers)
                {
                    throw new ArgumentException(
                        $"Magnet URI contains more than {MaximumTrackers} tracker parameters.",
                        nameof(value));
                }

                if (Uri.TryCreate(parameterValue, UriKind.Absolute, out var tracker) &&
                    trackerKeys.Add(tracker.AbsoluteUri))
                {
                    trackers.Add(tracker);
                }
            }
            else if (string.Equals(key, "x.umd.private", StringComparison.OrdinalIgnoreCase))
            {
                declaresPrivate = string.Equals(parameterValue, "1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parameterValue, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parameterValue, "yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (infoHashV1 is null && infoHashV2 is null)
        {
            throw new ArgumentException(
                "Magnet URI must contain a supported btih or btmh exact topic.",
                nameof(value));
        }

        return new MagnetLink(
            trimmed,
            infoHashV1,
            infoHashV2,
            displayName,
            declaresPrivate,
            trackers.ToArray());
    }

    public static bool TryParse(string? value, out MagnetLink? magnetLink)
    {
        try
        {
            magnetLink = Parse(value!);
            return true;
        }
        catch (ArgumentException)
        {
            magnetLink = null;
            return false;
        }
    }

    public IReadOnlyList<TrackerEndpoint> CreateTrackerEndpoints(
        TorrentEngineCapabilities capabilities,
        bool privateMode)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var result = new List<TrackerEndpoint>();
        for (var index = 0; index < TrackerUris.Count; index++)
        {
            var uri = TrackerUris[index];
            var transport = uri.Scheme.ToLowerInvariant() switch
            {
                "https" => TrackerTransport.Https,
                "wss" => TrackerTransport.Wss,
                "udp" => TrackerTransport.Udp,
                _ => (TrackerTransport?)null
            };

            if (transport is null || !capabilities.TrackerTransports.Contains(transport.Value))
            {
                continue;
            }

            if (privateMode && transport is TrackerTransport.Udp)
            {
                continue;
            }

            result.Add(new TrackerEndpoint(uri, transport.Value, tier: 0, sortOrder: result.Count));
        }

        return result;
    }

    public string CreateSessionUri(IReadOnlyList<TrackerEndpoint> trackers)
    {
        ArgumentNullException.ThrowIfNull(trackers);
        if (trackers.Count == 0)
        {
            return CatalogIdentityUri;
        }

        var builder = new StringBuilder(CatalogIdentityUri);
        foreach (var tracker in trackers)
        {
            builder.Append("&tr=");
            builder.Append(Uri.EscapeDataString(tracker.Endpoint.AbsoluteUri));
        }

        return builder.ToString();
    }

    internal static string NormalizeV1Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return TryNormalizeV1Hash(value.Trim(), out var normalized)
            ? normalized
            : throw new ArgumentException("BitTorrent v1 info hash must be 40 hexadecimal or 32 base32 characters.", nameof(value));
    }

    internal static string NormalizeV2Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit)
            ? trimmed.ToUpperInvariant()
            : throw new ArgumentException("BitTorrent v2 info hash must be 64 hexadecimal characters.", nameof(value));
    }

    private static bool TryNormalizeV1ExactTopic(string value, out string normalized)
    {
        const string prefix = "urn:btih:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
            return false;
        }

        return TryNormalizeV1Hash(value[prefix.Length..], out normalized);
    }

    private static bool TryNormalizeV2ExactTopic(string value, out string normalized)
    {
        const string prefix = "urn:btmh:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
            return false;
        }

        var taggedHash = value[prefix.Length..];
        if (taggedHash.Length != 68 ||
            !taggedHash.StartsWith("1220", StringComparison.OrdinalIgnoreCase) ||
            !taggedHash.All(Uri.IsHexDigit))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = taggedHash[4..].ToUpperInvariant();
        return true;
    }

    private static bool TryNormalizeV1Hash(string value, out string normalized)
    {
        if (value.Length == 40 && value.All(Uri.IsHexDigit))
        {
            normalized = value.ToUpperInvariant();
            return true;
        }

        if (value.Length != 32)
        {
            normalized = string.Empty;
            return false;
        }

        Span<byte> bytes = stackalloc byte[20];
        var accumulator = 0;
        var bits = 0;
        var outputIndex = 0;

        foreach (var character in value.ToUpperInvariant())
        {
            var digit = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => -1
            };
            if (digit < 0)
            {
                normalized = string.Empty;
                return false;
            }

            accumulator = (accumulator << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[outputIndex++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }

        if (outputIndex != bytes.Length || bits != 0)
        {
            normalized = string.Empty;
            return false;
        }

        normalized = Convert.ToHexString(bytes);
        return true;
    }

    private static string BuildCatalogIdentityUri(
        string? infoHashV1,
        string? infoHashV2,
        string? displayName)
    {
        var parameters = new List<string>();
        if (infoHashV1 is not null)
        {
            parameters.Add("xt=urn:btih:" + infoHashV1);
        }

        if (infoHashV2 is not null)
        {
            parameters.Add("xt=urn:btmh:1220" + infoHashV2);
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            parameters.Add("dn=" + Uri.EscapeDataString(displayName));
        }

        return "magnet:?" + string.Join('&', parameters);
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException exception)
        {
            throw new ArgumentException("Magnet URI contains invalid percent encoding.", exception);
        }
    }
}
