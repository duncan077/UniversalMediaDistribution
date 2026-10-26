namespace UniversalMediaDistribution.Torrenting.Ingestion;

public static class MagnetStartupRequestParser
{
    public const int MaximumStartupLinks = 16;

    private static readonly HashSet<string> QueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "magnet",
        "uri",
        "url"
    };

    public static IReadOnlyList<string> Extract(IEnumerable<string>? arguments)
    {
        if (arguments is null)
        {
            return [];
        }

        var result = new List<string>();
        var identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawArgument in arguments)
        {
            if (result.Count >= MaximumStartupLinks || string.IsNullOrWhiteSpace(rawArgument))
            {
                continue;
            }

            foreach (var candidate in ExtractCandidates(rawArgument.Trim()))
            {
                if (result.Count >= MaximumStartupLinks)
                {
                    break;
                }

                if (MagnetLink.TryParse(candidate, out var magnetLink) &&
                    identities.Add(magnetLink!.StableIdentity))
                {
                    result.Add(magnetLink.OriginalUri);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ExtractCandidates(string argument)
    {
        if (argument.StartsWith("--magnet=", StringComparison.OrdinalIgnoreCase))
        {
            yield return Decode(argument[9..]);
            yield break;
        }

        if (argument.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            yield return argument;
            yield break;
        }

        var decoded = Decode(argument);
        if (decoded.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            yield return decoded;
            yield break;
        }

        if (!Uri.TryCreate(argument, UriKind.Absolute, out var activationUri))
        {
            yield break;
        }

        var query = activationUri.Query;
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Decode(segment[..separator]);
            if (QueryKeys.Contains(key))
            {
                yield return Decode(segment[(separator + 1)..]);
            }
        }
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
