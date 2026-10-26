using System.Text;

namespace UniversalMediaDistribution.Playback.LibVLC;

internal enum HlsProxyResourceKind
{
    Media,
    Playlist
}

/// <summary>
/// Replaces every remotely-fetchable URI in an HLS document with an opaque
/// loopback route. Signed queries remain only in the in-memory route table.
/// </summary>
internal static class HlsPlaylistRewriter
{
    public static string Rewrite(
        string playlist,
        Uri playlistUri,
        Func<Uri, HlsProxyResourceKind, string> mapResource)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(playlistUri);
        ArgumentNullException.ThrowIfNull(mapResource);

        var newline = playlist.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        var normalized = playlist
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        var lines = normalized.Split('\n');
        var nextUriIsPlaylist = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
            {
                continue;
            }

            if (trimmed[0] != '#')
            {
                lines[index] = RewriteStandaloneUri(
                    line,
                    playlistUri,
                    nextUriIsPlaylist
                        ? HlsProxyResourceKind.Playlist
                        : HlsProxyResourceKind.Media,
                    mapResource);
                nextUriIsPlaylist = false;
                continue;
            }

            var attributeKind = CarriesPlaylistUri(trimmed)
                ? HlsProxyResourceKind.Playlist
                : HlsProxyResourceKind.Media;
            lines[index] = RewriteUriAttributes(
                line,
                playlistUri,
                attributeKind,
                mapResource);
            if (trimmed.StartsWith(
                    "#EXT-X-STREAM-INF:",
                    StringComparison.OrdinalIgnoreCase))
            {
                nextUriIsPlaylist = true;
            }
        }

        return string.Join(newline, lines);
    }

    private static string RewriteStandaloneUri(
        string line,
        Uri playlistUri,
        HlsProxyResourceKind kind,
        Func<Uri, HlsProxyResourceKind, string> mapResource)
    {
        var leading = 0;
        while (leading < line.Length && char.IsWhiteSpace(line[leading]))
        {
            leading++;
        }

        var end = line.Length;
        while (end > leading && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        var value = line[leading..end];
        var replacement = RewriteUriValue(
            value,
            playlistUri,
            kind,
            mapResource);
        return string.Equals(value, replacement, StringComparison.Ordinal)
            ? line
            : string.Concat(line.AsSpan(0, leading), replacement, line.AsSpan(end));
    }

    private static string RewriteUriAttributes(
        string line,
        Uri playlistUri,
        HlsProxyResourceKind kind,
        Func<Uri, HlsProxyResourceKind, string> mapResource)
    {
        StringBuilder? rewritten = null;
        var copyFrom = 0;
        var searchFrom = 0;
        while (TryFindUriAttribute(
                   line,
                   searchFrom,
                   out var valueStart,
                   out var valueLength))
        {
            var value = line.Substring(valueStart, valueLength);
            var replacement = RewriteUriValue(
                value,
                playlistUri,
                kind,
                mapResource);
            if (!string.Equals(value, replacement, StringComparison.Ordinal))
            {
                rewritten ??= new StringBuilder(line.Length + 64);
                rewritten.Append(line, copyFrom, valueStart - copyFrom);
                rewritten.Append(replacement);
                copyFrom = valueStart + valueLength;
            }

            searchFrom = valueStart + valueLength;
            if (valueLength == 0)
            {
                searchFrom++;
            }
        }

        if (rewritten is null)
        {
            return line;
        }

        rewritten.Append(line, copyFrom, line.Length - copyFrom);
        return rewritten.ToString();
    }

    private static bool TryFindUriAttribute(
        string line,
        int searchFrom,
        out int valueStart,
        out int valueLength)
    {
        while (searchFrom < line.Length)
        {
            var attribute = line.IndexOf(
                "URI=",
                searchFrom,
                StringComparison.OrdinalIgnoreCase);
            if (attribute < 0)
            {
                break;
            }

            if (attribute > 0 &&
                line[attribute - 1] is not
                    (':' or ',' or '-' or ' ' or '\t'))
            {
                searchFrom = attribute + 4;
                continue;
            }

            var start = attribute + 4;
            while (start < line.Length && char.IsWhiteSpace(line[start]))
            {
                start++;
            }

            if (start < line.Length && line[start] == '"')
            {
                start++;
                var endQuote = line.IndexOf('"', start);
                if (endQuote < 0)
                {
                    break;
                }

                valueStart = start;
                valueLength = endQuote - start;
                return true;
            }

            var end = line.IndexOf(',', start);
            if (end < 0)
            {
                end = line.Length;
            }

            while (end > start && char.IsWhiteSpace(line[end - 1]))
            {
                end--;
            }

            valueStart = start;
            valueLength = end - start;
            return true;
        }

        valueStart = 0;
        valueLength = 0;
        return false;
    }

    private static string RewriteUriValue(
        string value,
        Uri playlistUri,
        HlsProxyResourceKind kind,
        Func<Uri, HlsProxyResourceKind, string> mapResource)
    {
        if (value.Length == 0 || value.Contains("{$", StringComparison.Ordinal))
        {
            return value;
        }

        if (!Uri.TryCreate(playlistUri, value, out var candidate))
        {
            throw new InvalidDataException(
                "The HLS playlist contained an invalid media URI.");
        }

        if (candidate.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (!PublicHttpEndpointPolicy.IsSafePublicHttpUri(candidate))
        {
            throw new InvalidDataException(
                "The HLS playlist contained an unsafe media URI.");
        }

        if (candidate.Fragment.Length > 0)
        {
            candidate = new UriBuilder(candidate)
            {
                Fragment = string.Empty
            }.Uri;
        }

        return mapResource(candidate, kind);
    }

    private static bool CarriesPlaylistUri(ReadOnlySpan<char> tag) =>
        tag.StartsWith(
            "#EXT-X-MEDIA:",
            StringComparison.OrdinalIgnoreCase) ||
        tag.StartsWith(
            "#EXT-X-I-FRAME-STREAM-INF:",
            StringComparison.OrdinalIgnoreCase) ||
        tag.StartsWith(
            "#EXT-X-IMAGE-STREAM-INF:",
            StringComparison.OrdinalIgnoreCase) ||
        tag.StartsWith(
            "#EXT-X-RENDITION-REPORT:",
            StringComparison.OrdinalIgnoreCase);
}
