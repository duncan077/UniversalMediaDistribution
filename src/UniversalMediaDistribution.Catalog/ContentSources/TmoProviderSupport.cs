using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.Catalog.ContentSources;

internal static class TmoProviderHttp
{
    private const int BufferSize = 16 * 1024;

    /// <param name="extraHeaders">
    /// Request headers a specific site requires, such as the tenant header a
    /// multi-organization host resolves its content from. Applied per call
    /// rather than per client so one provider's requirement cannot leak onto
    /// another's requests.
    /// </param>
    internal static async ValueTask<string> ReadStringAsync(
        HttpClient httpClient,
        Uri endpoint,
        Uri referrer,
        int maximumBytes,
        string accept,
        string providerName,
        CancellationToken cancellationToken,
        IReadOnlyList<(string Name, string Value)>? extraHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(referrer);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(accept);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (!TmoProviderUri.IsSafePublicHttpUri(endpoint) ||
            !TmoProviderUri.IsSafePublicHttpUri(referrer))
        {
            throw new InvalidDataException(
                $"{providerName} returned an unsafe HTTP endpoint.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; UniversalMediaDistribution/1.0)");
        request.Headers.Referrer = referrer;
        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await ApiProviderHttp.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"{providerName} response from {endpoint.Host} exceeds the " +
                $"{maximumBytes / 1024} KiB limit.");
        }

        await using var input =
            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        using var output = new MemoryStream(
            contentLength is >= 0 && contentLength <= maximumBytes
                ? checked((int)contentLength.Value)
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, BufferSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"{providerName} response from {endpoint.Host} exceeds " +
                        $"the {maximumBytes / 1024} KiB limit.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }

            var encoding = GetEncoding(
                response.Content.Headers.ContentType?.CharSet);
            return encoding.GetString(
                output.GetBuffer(),
                0,
                checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Encoding GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim(' ', '\t', '"', '\''));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}

internal static class TmoProviderUri
{
    internal static Uri NormalizeRoot(Uri baseUri, string providerName)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (!baseUri.IsAbsoluteUri ||
            baseUri.Scheme is not ("http" or "https") ||
            baseUri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                $"{providerName} requires an absolute HTTP or HTTPS base URL " +
                "without embedded credentials.",
                nameof(baseUri));
        }

        var builder = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += '/';
        }

        var root = builder.Uri;
        if (!IsSafePublicHttpUri(root))
        {
            throw new ArgumentException(
                $"{providerName} requires a public HTTP or HTTPS base URL.",
                nameof(baseUri));
        }

        return root;
    }

    internal static bool TryResolveSameOrigin(
        Uri root,
        string? value,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(
                root,
                WebUtility.HtmlDecode(value.Trim()),
                out var candidate) ||
            !IsSafePublicHttpUri(candidate) ||
            !HasSameOrigin(root, candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    internal static bool TryResolveProviderAsset(
        Uri root,
        string? value,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(
                root,
                WebUtility.HtmlDecode(value.Trim()),
                out var candidate) ||
            !IsSafePublicHttpUri(candidate) ||
            !IsProviderHost(root, candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    internal static bool TryCreatePublicHttpUri(
        string? value,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(
                WebUtility.HtmlDecode(value.Trim()),
                UriKind.Absolute,
                out var candidate) ||
            !IsSafePublicHttpUri(candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    internal static bool IsSafePublicHttpUri(Uri uri) =>
        HosterExtractorBase.IsSafePublicHttpUri(uri);

    internal static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(
            left.Scheme,
            right.Scheme,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.IdnHost,
            right.IdnHost,
            StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    internal static bool IsProviderHost(Uri root, Uri candidate) =>
        string.Equals(
            root.IdnHost,
            candidate.IdnHost,
            StringComparison.OrdinalIgnoreCase) ||
        candidate.IdnHost.EndsWith(
            $".{root.IdnHost}",
            StringComparison.OrdinalIgnoreCase);

    internal static Uri GetOrigin(Uri uri) =>
        new UriBuilder(
            uri.Scheme,
            uri.IdnHost,
            uri.IsDefaultPort ? -1 : uri.Port,
            "/").Uri;

    internal static string[] GetPathSegments(Uri uri)
    {
        try
        {
            return uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
        }
        catch (UriFormatException)
        {
            return [];
        }
    }

    internal static bool TryValidateSegment(
        string? value,
        int maximumLength,
        Func<char, bool> isAllowed,
        out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
               normalized.Length <= maximumLength &&
               normalized.All(isAllowed);
    }
}

internal static class TmoProviderText
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex AttributeRegex = new(
        """
        (?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s>]+))
        """,
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex BreakRegex = new(
        @"<br\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex StripTagsRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex PunctuationSpacingRegex = new(
        @"\s+([.,;:!?])",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    internal static IEnumerable<TmoHtmlElement> EnumerateElements(
        string html,
        string tagName)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        var escaped = Regex.Escape(tagName);
        var pattern =
            $@"<{escaped}\b(?<attributes>[^>]*)>" +
            $@"(?<content>.*?)</{escaped}\s*>";
        foreach (Match match in Regex.Matches(
                     html,
                     pattern,
                     RegexOptions.IgnoreCase |
                     RegexOptions.Singleline |
                     RegexOptions.CultureInvariant,
                     RegexTimeout))
        {
            yield return new TmoHtmlElement(
                match.Groups["attributes"].Value,
                match.Groups["content"].Value);
        }
    }

    internal static IEnumerable<string> EnumerateTagAttributes(
        string html,
        string tagName)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        var pattern =
            $@"<{Regex.Escape(tagName)}\b(?<attributes>[^>]*)>";
        foreach (Match match in Regex.Matches(
                     html,
                     pattern,
                     RegexOptions.IgnoreCase |
                     RegexOptions.Singleline |
                     RegexOptions.CultureInvariant,
                     RegexTimeout))
        {
            yield return match.Groups["attributes"].Value;
        }
    }

    /// <summary>
    /// Returns balanced blocks for nested div/list markup. The span scanner
    /// avoids allocating substrings for every unrelated tag on large ZonaTMO
    /// title pages.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateClassBlocks(
        string html,
        string tagName,
        string className,
        int maximumBlocks)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBlocks, 1);
        var blocks = new List<string>();
        var source = html.AsSpan();
        var openMarker = $"<{tagName}";
        var closeMarker = $"</{tagName}";
        var searchAt = 0;
        while (blocks.Count < maximumBlocks)
        {
            var start = FindHtmlTag(source, openMarker, searchAt);
            if (start < 0)
            {
                break;
            }

            var openingEnd = source[start..].IndexOf('>');
            if (openingEnd < 0)
            {
                break;
            }

            openingEnd += start;
            if (!HtmlTagHasClass(
                    source[start..(openingEnd + 1)],
                    className))
            {
                searchAt = openingEnd + 1;
                continue;
            }

            var end = FindBalancedTagEnd(
                source,
                openMarker,
                closeMarker,
                openingEnd + 1);
            if (end < 0)
            {
                break;
            }

            blocks.Add(html[start..end]);
            searchAt = end;
        }

        return blocks;
    }

    internal static string? FindElementContentByClass(
        string html,
        string tagName,
        string className)
    {
        var block = EnumerateClassBlocks(
                html,
                tagName,
                className,
                maximumBlocks: 1)
            .FirstOrDefault();
        if (block is null)
        {
            return null;
        }

        var openingEnd = block.IndexOf('>');
        var closingStart = block.LastIndexOf(
            $"</{tagName}",
            StringComparison.OrdinalIgnoreCase);
        return openingEnd >= 0 && closingStart > openingEnd
            ? block[(openingEnd + 1)..closingStart]
            : null;
    }

    internal static string? FindElementContentById(
        string html,
        string tagName,
        string id)
    {
        foreach (var element in EnumerateElements(html, tagName))
        {
            if (string.Equals(
                    GetAttribute(element.Attributes, "id"),
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return element.Content;
            }
        }

        return null;
    }

    internal static string? FindImageSourceByClass(
        string html,
        string className)
    {
        foreach (var attributes in EnumerateTagAttributes(html, "img"))
        {
            if (!HasClass(GetAttribute(attributes, "class"), className))
            {
                continue;
            }

            return GetAttribute(attributes, "data-src") ??
                GetAttribute(attributes, "data-lazy-src") ??
                GetAttribute(attributes, "src");
        }

        return null;
    }

    internal static string? ExtractMetaContent(
        string html,
        string identifyingAttribute,
        string identifyingValue)
    {
        foreach (var attributes in EnumerateTagAttributes(html, "meta"))
        {
            if (string.Equals(
                    GetAttribute(attributes, identifyingAttribute),
                    identifyingValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetAttribute(attributes, "content");
            }
        }

        return null;
    }

    internal static string? GetOpeningTagAttributes(
        string block,
        string tagName) =>
        EnumerateTagAttributes(block, tagName).FirstOrDefault();

    internal static string? GetAttribute(
        string? attributes,
        string name)
    {
        if (string.IsNullOrEmpty(attributes))
        {
            return null;
        }

        foreach (Match match in AttributeRegex.Matches(attributes))
        {
            if (!string.Equals(
                    match.Groups["name"].Value,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
            return WebUtility.HtmlDecode(value);
        }

        return null;
    }

    internal static bool HasClass(
        string? classValue,
        string className) =>
        classValue?.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.OrdinalIgnoreCase) is true;

    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var withLines = BreakRegex.Replace(html, "\n");
        var withoutTags = StripTagsRegex.Replace(withLines, " ");
        return NormalizeText(WebUtility.HtmlDecode(withoutTags));
    }

    internal static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : PunctuationSpacingRegex.Replace(
                WhitespaceRegex.Replace(value, " ").Trim(),
                "$1");

    internal static bool Contains(string value, string query) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            value,
            query.Trim(),
            CompareOptions.IgnoreCase |
            CompareOptions.IgnoreNonSpace) >= 0;

    private static int FindBalancedTagEnd(
        ReadOnlySpan<char> source,
        ReadOnlySpan<char> openMarker,
        ReadOnlySpan<char> closeMarker,
        int contentStart)
    {
        var depth = 1;
        var cursor = contentStart;
        while (cursor < source.Length)
        {
            var nextOpen = FindHtmlTag(source, openMarker, cursor);
            var nextClose = FindHtmlTag(source, closeMarker, cursor);
            if (nextClose < 0)
            {
                return -1;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                var openingEnd = source[nextOpen..].IndexOf('>');
                if (openingEnd < 0)
                {
                    return -1;
                }

                depth++;
                cursor = nextOpen + openingEnd + 1;
                continue;
            }

            var closingEnd = source[nextClose..].IndexOf('>');
            if (closingEnd < 0)
            {
                return -1;
            }

            depth--;
            cursor = nextClose + closingEnd + 1;
            if (depth == 0)
            {
                return cursor;
            }
        }

        return -1;
    }

    private static int FindHtmlTag(
        ReadOnlySpan<char> source,
        ReadOnlySpan<char> marker,
        int start)
    {
        while (start < source.Length)
        {
            var relative = source[start..].IndexOf(
                marker,
                StringComparison.OrdinalIgnoreCase);
            if (relative < 0)
            {
                return -1;
            }

            var index = start + relative;
            var boundary = index + marker.Length;
            if (boundary >= source.Length ||
                char.IsWhiteSpace(source[boundary]) ||
                source[boundary] is '>' or '/')
            {
                return index;
            }

            start = boundary;
        }

        return -1;
    }

    private static bool HtmlTagHasClass(
        ReadOnlySpan<char> tag,
        ReadOnlySpan<char> expectedClass)
    {
        var searchAt = 0;
        while (searchAt < tag.Length)
        {
            var relative = tag[searchAt..].IndexOf(
                "class",
                StringComparison.OrdinalIgnoreCase);
            if (relative < 0)
            {
                return false;
            }

            var nameStart = searchAt + relative;
            var cursor = nameStart + "class".Length;
            if (nameStart > 0 &&
                (char.IsAsciiLetterOrDigit(tag[nameStart - 1]) ||
                 tag[nameStart - 1] is '-' or '_') ||
                cursor < tag.Length &&
                (char.IsAsciiLetterOrDigit(tag[cursor]) ||
                 tag[cursor] is '-' or '_'))
            {
                searchAt = cursor;
                continue;
            }

            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length || tag[cursor] != '=')
            {
                searchAt = cursor;
                continue;
            }

            cursor++;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length)
            {
                return false;
            }

            var quote = tag[cursor] is '"' or '\''
                ? tag[cursor++]
                : '\0';
            var valueStart = cursor;
            while (cursor < tag.Length &&
                   (quote == '\0'
                       ? !char.IsWhiteSpace(tag[cursor]) &&
                         tag[cursor] != '>'
                       : tag[cursor] != quote))
            {
                cursor++;
            }

            var value = tag[valueStart..cursor];
            while (!value.IsEmpty)
            {
                var tokenStart = 0;
                while (tokenStart < value.Length &&
                       char.IsWhiteSpace(value[tokenStart]))
                {
                    tokenStart++;
                }

                value = value[tokenStart..];
                if (value.IsEmpty)
                {
                    break;
                }

                var tokenEnd = 0;
                while (tokenEnd < value.Length &&
                       !char.IsWhiteSpace(value[tokenEnd]))
                {
                    tokenEnd++;
                }

                if (value[..tokenEnd].Equals(
                        expectedClass,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                value = value[tokenEnd..];
            }

            searchAt = cursor + 1;
        }

        return false;
    }
}

internal readonly record struct TmoHtmlElement(
    string Attributes,
    string Content);
