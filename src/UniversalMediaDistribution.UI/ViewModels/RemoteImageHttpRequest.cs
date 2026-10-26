using System.Net.Http.Headers;

namespace UniversalMediaDistribution.UI.ViewModels;

internal static class RemoteImageHttpRequest
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/138.0.0.0 Safari/537.36";

    public static HttpRequestMessage Create(Uri uri, Uri? referrer = null)
    {
        ValidateHttpUri(uri, nameof(uri));
        if (referrer is not null)
        {
            ValidateHttpUri(referrer, nameof(referrer));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Referrer = referrer;
        return request;
    }

    private static void ValidateHttpUri(Uri uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "Remote images require an absolute HTTP or HTTPS URL without embedded credentials.",
                parameterName);
        }
    }
}
