using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Do not hand a browser-only session to a native player that has no browser cookies.</summary>
public static class VideoChallengeMediaProbe
{
    // Feature-length playlists with fully rewritten CDN URLs can exceed 128 KiB.
    // Keep probing bounded, below the native player's 4 MiB playlist limit.
    private const int MaximumPlaylistBytes = 1024 * 1024;
    public static async ValueTask<bool> VerifyAsync(ApiContentVideoSource source, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return await VerifyAsync(client, source, cancellationToken);
    }

    internal static async ValueTask<bool> VerifyAsync(HttpClient client, ApiContentVideoSource source, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var current = source.PlaybackUri;
            var hls = source.MimeType == "application/vnd.apple.mpegurl";
            var visited = new HashSet<Uri>();
            for (var hop = 0; hop < 5 && visited.Add(current); hop++)
            {
                using var response = await SendAsync(client, current, source, !hls, budget.Token);
                if (response is null) return false;
                if (!hls)
                {
                    var type = response.Content.Headers.ContentType?.MediaType;
                    if (type is "application/json" || type?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true) return false;
                    await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
                    var bytes = new byte[8];
                    var read = await stream.ReadAsync(bytes, budget.Token);
                    return read > 0 && bytes[0] is not ((byte)'<' or (byte)'{');
                }
                var bytesText = await HosterExtractorBase.ReadBoundedResponseBytesAsync(response.Content, current, MaximumPlaylistBytes, budget.Token);
                var text = Encoding.UTF8.GetString(bytesText);
                if (!text.AsSpan().TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)) return false;
                var isChild = false;
                string? target = null;
                foreach (var range in text.AsSpan().Split('\n'))
                {
                    var line = text.AsSpan()[range].Trim();
                    if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) isChild = true;
                    if (!line.IsEmpty && line[0] != '#') { target = line.ToString(); break; }
                }
                var final = response.RequestMessage?.RequestUri ?? current;
                if (target is null || !Uri.TryCreate(final, target, out current!)) return false;
                hls = isChild;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (InvalidDataException) { }
        return false;
    }

    private static async Task<HttpResponseMessage?> SendAsync(HttpClient client, Uri uri, ApiContentVideoSource source, bool range, CancellationToken token)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            if (!HosterExtractorBase.IsSafePublicHttpUri(uri)) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Referrer = source.Referrer;
            if (source.UserAgent is { } agent) request.Headers.TryAddWithoutValidation("User-Agent", agent);
            if (source.HttpOrigin is { } origin) request.Headers.TryAddWithoutValidation("Origin", origin.GetLeftPart(UriPartial.Authority));
            if (range) request.Headers.Range = new RangeHeaderValue(0, 7);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || !Uri.TryCreate(uri, location, out uri!)) return null;
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            response.Dispose();
            return null;
        }
        return null;
    }
}
