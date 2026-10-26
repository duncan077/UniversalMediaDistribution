using System.Text.Json;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Small shared observer for a visible, user-confirmed hoster challenge.</summary>
public static class VideoChallengeCapture
{
    public const string Instructions = "Complete the site check, press Play, then choose Use native player when your video is playing. Close or go Back to keep the web player.";

    /// <summary>JavaScript players that can be captured on explicit server selection,
    /// without requiring an HTTP challenge response first.</summary>
    public static bool SupportsInteractiveCapture(Uri uri) =>
        HosterExtractorBase.IsSafePublicHttpUri(uri) &&
        (IsUpnShare(uri) || IsFramexEmbed(uri) || new FilemoonHosterExtractor().Matches(uri));

    private static bool IsFramexEmbed(Uri uri) => uri.IdnHost is "framextv.xyz" or "www.framextv.xyz" &&
        uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal);

    private static bool IsUpnShare(Uri uri) =>
        uri.IdnHost is "uns.bio" or "upns.pro" ||
        uri.IdnHost.EndsWith(".uns.bio", StringComparison.Ordinal) ||
        uri.IdnHost.EndsWith(".upns.pro", StringComparison.Ordinal);

    public static bool AllowsNavigation(Uri original, Uri target) =>
        HosterExtractorBase.IsSafePublicHttpUri(target) &&
        (target.IdnHost == original.IdnHost || SupportsInteractiveCapture(target) || new DoodstreamHosterExtractor().Matches(target));

    public static ApiContentVideoSource? Parse(string? message, ApiContentVideoSource original)
    {
        if (message is null || message.Length > 16384) return null;
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("umdVideoChallenge", out var marker) || marker.ValueKind != JsonValueKind.True ||
                !TryString(root, "url", out var url) || !TryString(root, "document", out var document) ||
                !TryString(root, "userAgent", out var userAgent) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var media) || !HosterExtractorBase.IsSafePublicHttpUri(media) ||
                !Uri.TryCreate(document, UriKind.Absolute, out var referrer) || !HosterExtractorBase.IsSafePublicHttpUri(referrer)) return null;
            var mime = HosterMimeTypeResolver.MimeTypeFromUrl(media);
            if (mime is null && TryString(root, "type", out var type) &&
                type is "video/mp4" or "video/webm" or "application/vnd.apple.mpegurl") mime = type;
            if (mime is not ("video/mp4" or "video/webm" or "application/vnd.apple.mpegurl")) return null;
            return new ApiContentVideoSource(original.Label, media, mime, referrer,
                ApiContentVideoSourceKind.DirectMedia,
                // HLS uses the existing header-preserving loopback proxy on
                // Windows. Plain VLC file requests cannot preserve Origin, so
                // probe those without it as well.
                httpOrigin: mime == "application/vnd.apple.mpegurl" ? new Uri(referrer.GetLeftPart(UriPartial.Authority) + "/") : null,
                userAgent: userAgent,
                subtitles: FramexTvApiContentProvider.ParseSubtitles(root).Concat(original.Subtitles)
                    .DistinctBy(t => t.Source.AbsoluteUri).Take(100).ToArray());
        }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            (value = property.GetString() ?? string.Empty).Length is > 0 and <= 8192;
    }

    // This observes normal playback. It never clicks or solves a CAPTCHA,
    // reads cookies, reads decryption keys, or stores data outside this page.
    public const string Script = """
        (() => {
          if (window.__umdVideoChallengeInstalled) return;
          window.__umdVideoChallengeInstalled = true;
          let manifest = null;
          function observe(value, type) {
            try { const u = new URL(value, location.href);
              if (/^https?:$/.test(u.protocol) && (/\.m3u8$/i.test(u.pathname) || /mpegurl/i.test(type || ''))) manifest = u.href;
            } catch (_) {}
          }
          const fetchOriginal = window.fetch;
          if (fetchOriginal) window.fetch = function(input, init) {
            observe(typeof input === 'string' ? input : input && input.url);
            return fetchOriginal.apply(this, arguments).then(response => {
              try { if (response.ok) observe(response.url, response.headers.get('content-type')); } catch (_) {}
              return response;
            });
          };
          const openOriginal = XMLHttpRequest.prototype.open;
          XMLHttpRequest.prototype.open = function(method, url) {
            observe(url);
            if (this.addEventListener) this.addEventListener('load', () => {
              try { if (this.status >= 200 && this.status < 300) observe(this.responseURL, this.getResponseHeader('content-type')); } catch (_) {}
            }, { once: true });
            return openOriginal.apply(this, arguments);
          };
          function publish(value) {
            if (!value || value.umdVideoChallenge !== true) return;
            window.__umdVideoChallengeResult = JSON.stringify(value);
            try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(window.__umdVideoChallengeResult); } catch (_) {}
            if (window.parent !== window) window.parent.postMessage({ umdVideoChallengeFrame: value }, '*');
          }
          window.addEventListener('message', e => {
            if (Array.from(document.querySelectorAll('iframe')).some(f => f.contentWindow === e.source) &&
                e.data && e.data.umdVideoChallengeFrame) publish(e.data.umdVideoChallengeFrame);
          });
          setInterval(() => {
            const video = Array.from(document.querySelectorAll('video')).find(v => !v.paused && v.readyState >= 2 && v.currentTime > 0 && v.videoWidth > 0 && !v.mediaKeys);
            if (!video) return;
            let url = video.currentSrc;
            if (!/^https?:\/\//.test(url)) {
              if (!manifest) performance.getEntriesByType('resource').forEach(r => observe(r.name));
              url = manifest;
            }
            const subtitles = Array.from(video.querySelectorAll('track[src]')).slice(0, 100)
              .filter(t => t.kind === 'subtitles' || t.kind === 'captions')
              .map(t => ({ url: t.src, label: t.label || t.srclang || 'Subtitles', lang: t.srclang || 'und', isDefault: t.default }));
            if (url) publish({ umdVideoChallenge: true, url, document: location.href, userAgent: navigator.userAgent, subtitles,
              type: url === manifest ? 'application/vnd.apple.mpegurl' : /\.webm(?:\?|$)/i.test(url) ? 'video/webm' : 'video/mp4' });
          }, 750);
        })();
        """;
}
