using System.Collections.Concurrent;
using System.Net;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Remembers where a provider's site said it had moved to.
/// <para>
/// Sites change domain far more often than a shipped descriptor can be
/// updated, and until it is every request pays a redirect — or fails outright
/// once the old name stops answering. A permanent redirect is the site stating
/// the move itself, which is a better source for the address than anything
/// compiled in, so it is taken at its word for the rest of the session.
/// </para>
/// <para>
/// Only a move is followed, never a redirect to somewhere else: it has to be
/// permanent, to https, to a different host, and to the same path. A site
/// sending a search page to its home page is not announcing a new address.
/// </para>
/// </summary>
public static class ProviderBaseUrlRedirects
{
    /// <summary>
    /// Enough for every built-in provider to move several times over. Cleared
    /// wholesale rather than evicted one by one, because the map is tiny and
    /// relearned from the next request.
    /// </summary>
    private const int MaximumEntries = 128;

    /// <summary>
    /// How many moves are followed from one address. A site that redirects in
    /// a loop would otherwise be walked until the stack ran out.
    /// </summary>
    private const int MaximumChainLength = 3;

    private static readonly ConcurrentDictionary<string, string> Learned =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a move if that is what this response is announcing.
    /// </summary>
    internal static void Observe(
        HttpRequestMessage request,
        HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently or
                HttpStatusCode.PermanentRedirect) ||
            request.RequestUri is not { } requested ||
            response.Headers.Location is not { } location)
        {
            return;
        }

        var target = location.IsAbsoluteUri
            ? location
            : new Uri(requested, location);
        if (target.Scheme != Uri.UriSchemeHttps ||
            string.Equals(
                target.IdnHost,
                requested.IdnHost,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                target.AbsolutePath,
                requested.AbsolutePath,
                StringComparison.Ordinal) ||
            !HosterExtractorBase.IsSafePublicHttpUri(target))
        {
            return;
        }

        if (Learned.Count >= MaximumEntries)
        {
            Learned.Clear();
        }

        Learned[requested.IdnHost] =
            target.GetLeftPart(UriPartial.Authority) + "/";
    }

    /// <summary>
    /// Rewrites an address onto whichever host the site has since moved to,
    /// keeping the path it was configured with. Returns the original when
    /// nothing has been learned about it.
    /// </summary>
    public static string Apply(string baseUrl)
    {
        if (Learned.IsEmpty ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var current))
        {
            return baseUrl;
        }

        for (var hop = 0; hop < MaximumChainLength; hop++)
        {
            if (!Learned.TryGetValue(current.IdnHost, out var moved) ||
                !Uri.TryCreate(moved, UriKind.Absolute, out var target))
            {
                break;
            }

            current = new Uri(target, current.PathAndQuery);
        }

        return current.AbsoluteUri;
    }

    /// <summary>
    /// Forgets every learned move. For tests, and for a profile switch that
    /// should not inherit another session's discoveries.
    /// </summary>
    public static void Reset() => Learned.Clear();
}
