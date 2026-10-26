namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Resolves the page the embedded player should actually navigate to.
///
/// Several hosts hand out a wrapper whose only content is an iframe of the
/// real player — <c>host/iframe/?url=https%3A%2F%2Freal.example%2Fe%2Fid</c>.
/// Opening the wrapper is the worst possible arrangement for filtering: a
/// cross-origin iframe runs in its own renderer, and WebView2 raises
/// <c>WebResourceRequested</c> only for the frame the filter is attached to.
/// Measured on one such page, seven requests were offered to the filter and
/// fifty-one were made — everything inside the iframe, including every
/// advertisement, was invisible.
///
/// Navigating to the inner address instead makes the player the top-level
/// document, so its requests are filtered like any other. It also skips the
/// wrapper's own advertising entirely.
/// </summary>
public static class EmbeddedWebTarget
{
    /// <summary>
    /// Query parameters that carry a wrapped address. Deliberately a short
    /// list of names that mean "this is the page": a parameter that merely
    /// happens to contain a URL is usually a tracker, and following it would
    /// take the viewer somewhere they did not ask to go.
    /// </summary>
    private static readonly string[] TargetParameters =
        ["url", "u", "link", "src", "embed", "file"];

    /// <summary>
    /// Wrappers occasionally nest. The cap stops a crafted address from
    /// looping.
    /// </summary>
    private const int MaxDepth = 3;

    public static Uri Unwrap(Uri source, Func<Uri, bool> isAcceptable)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(isAcceptable);

        var current = source;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (!TryUnwrapOnce(current, isAcceptable, out var inner))
            {
                break;
            }

            current = inner;
        }

        return current;
    }

    private static bool TryUnwrapOnce(
        Uri source,
        Func<Uri, bool> isAcceptable,
        out Uri inner)
    {
        inner = source;
        var query = source.Query;
        if (query.Length <= 1)
        {
            return false;
        }

        foreach (var pair in query[1..].Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator];
            if (!TargetParameters.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string value;
            try
            {
                value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                continue;
            }

            // Only a different host counts. A wrapper pointing at itself is
            // not a wrapper, and following it would loop.
            if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
                candidate.Scheme is "http" or "https" &&
                !string.Equals(
                    candidate.IdnHost,
                    source.IdnHost,
                    StringComparison.OrdinalIgnoreCase) &&
                isAcceptable(candidate))
            {
                inner = candidate;
                return true;
            }
        }

        return false;
    }
}
