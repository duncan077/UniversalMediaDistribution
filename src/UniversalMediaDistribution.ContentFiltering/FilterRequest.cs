namespace UniversalMediaDistribution.ContentFiltering;

/// <summary>
/// One request to decide on. A ref struct over spans the caller already holds,
/// so describing a request costs nothing — which matters when the description
/// happens on a web view's network thread for every subresource of a page.
/// </summary>
public readonly ref struct FilterRequest
{
    public FilterRequest(
        ReadOnlySpan<char> url,
        ReadOnlySpan<char> documentUrl = default,
        ResourceType type = ResourceType.Other,
        RequestMethod method = RequestMethod.None)
    {
        Url = url;
        DocumentUrl = documentUrl;
        Type = type;
        Method = method;
    }

    public ReadOnlySpan<char> Url { get; }

    /// <summary>
    /// The page the request belongs to. Empty when it is not known, which is
    /// treated as third-party: there is nothing for the request to be
    /// first-party to.
    /// </summary>
    public ReadOnlySpan<char> DocumentUrl { get; }

    public ResourceType Type { get; }

    public RequestMethod Method { get; }
}
