using System.Text;
using Android.Graphics;
using Android.Runtime;
using Android.Webkit;
using AndroidX.WebKit;
using Avalonia.Platform;
using AvaloniaWebView = Avalonia.Controls.NativeWebView;
using PlatformWebView = Android.Webkit.WebView;
using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Android.Web;

/// <summary>
/// Refuses blocked requests inside the system web view before they reach the
/// network.
///
/// Android's <c>shouldInterceptRequest</c> is the only place a host can answer
/// a web view's request itself, and it is what turns the filtering engine from
/// a suggestion the page could ignore into something it cannot. Reading the
/// current client and delegating to it keeps Avalonia's own navigation events
/// working; replacing it outright would silently break them.
///
/// The callback runs on the web view's network thread, not the UI thread, so
/// everything it touches is either immutable or published through a volatile
/// field. It also blocks that request while it runs, which is why the engine's
/// match path allocates nothing.
/// </summary>
public sealed class AndroidWebViewContentFilterHook : INativeWebViewFilterHook
{
    private FilteringWebViewClient? _client;
    private PlatformWebView? _webView;
    private IContentFilterSession? _session;
    private IScriptHandler? _documentStartScript;

    /// <summary>
    /// The web view the current client belongs to. Comparing against it is
    /// what detects an adapter that was destroyed and recreated.
    /// </summary>
    private nint _attachedTo;

    public event Action<EmbeddedWebMediaResponse>? MediaResponseReceived;

    public bool IsAttached => _client is not null;

    private void Detach()
    {
        if (_session is not null)
        {
            _session.DocumentOpened -= OnDocumentOpened;
        }

        // The old web view is gone; the script handler it owned went with it.
        _client = null;
        _webView = null;
        _session = null;
        _documentStartScript = null;
        _attachedTo = nint.Zero;
    }

    public string? AttachFailure { get; private set; }

    public bool TryAttach(
        AvaloniaWebView webView,
        IContentFilterSession session,
        IPlatformHandle? platformHandle = null)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(session);

        // Reading the installed client needs API 26. Below that the injected
        // policy remains the enforcement mechanism; every Google TV device and
        // every current handset is well past that floor.
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return Fail("this Android version cannot expose the web view client");
        }

        if ((platformHandle ?? webView.TryGetPlatformHandle()) is not
                IAndroidWebViewPlatformHandle handle ||
            handle.WebKitWebView == nint.Zero)
        {
            return Fail("the web view has not been created yet");
        }

        if (_client is not null && handle.WebKitWebView == _attachedTo)
        {
            return true;
        }

        // A different web view than the one we filtered before: the control
        // was reparented and the adapter recreated, so the client we installed
        // went with the old one.
        Detach();

        var platformWebView = Java.Lang.Object.GetObject<PlatformWebView>(
            handle.WebKitWebView,
            JniHandleOwnership.DoNotTransfer);
        if (platformWebView is null)
        {
            return Fail("the web view handle could not be resolved");
        }

        Harden(platformWebView.Settings);
        var client = new FilteringWebViewClient(
            platformWebView.WebViewClient,
            session,
            SupportsDocumentStartScript,
            PublishMediaResponse);
        platformWebView.SetWebViewClient(client);
        _client = client;
        _webView = platformWebView;
        _session = session;
        _attachedTo = handle.WebKitWebView;
        AttachFailure = null;
        session.DocumentOpened += OnDocumentOpened;
        if (session.DocumentUri is { } document)
        {
            OnDocumentOpened(document);
        }

        return true;
    }

    private void PublishMediaResponse(
        EmbeddedWebMediaResponse observation)
    {
        try
        {
            MediaResponseReceived?.Invoke(observation);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Publishing an embedded media request failed: " +
                $"{exception.Message}");
        }
    }

    private bool Fail(string reason)
    {
        if (!string.Equals(AttachFailure, reason, StringComparison.Ordinal))
        {
            AttachFailure = reason;
            System.Diagnostics.Trace.TraceWarning(
                $"Content filtering is not attached to the web view: {reason}. " +
                "The injected policy is filtering instead.");
        }

        return false;
    }

    /// <summary>
    /// Whether the system web view can run a script before the page's own.
    /// The capability arrived in WebView 106; Play-certified Google TV devices
    /// and current handsets have it, older system web views do not.
    /// </summary>
    private static bool SupportsDocumentStartScript
    {
        get
        {
            try
            {
                return WebViewFeature.IsFeatureSupported(
                    WebViewFeature.DocumentStartScript);
            }
            catch (Exception exception) when (
                exception is Java.Lang.Throwable or
                    NotSupportedException or
                    TypeLoadException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Replaces the cosmetic payload with the one for the page being opened.
    /// Registering it here means it runs before the page's own scripts rather
    /// than after the advertisement has already drawn itself.
    /// </summary>
    private void OnDocumentOpened(Uri document)
    {
        if (_webView is not { } webView ||
            _session is not { } session ||
            !SupportsDocumentStartScript)
        {
            // The client injects at page-start instead, which is later but
            // still far earlier than waiting for the page to finish.
            return;
        }

        try
        {
            _documentStartScript?.Remove();
            _documentStartScript = null;

            var host = document.Host;
            if (host.Length == 0)
            {
                return;
            }

            // Scoped to the page's own origin: a payload built for one host
            // has no business running on another.
            _documentStartScript = WebViewCompat.AddDocumentStartJavaScript(
                webView,
                session.BuildInjectionPayload(host),
                new List<string>
                {
                    $"{document.Scheme}://{document.Authority}"
                });
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            // Every exception, not only the expected ones: this runs from a
            // dispatcher callback, and a web view being torn down underneath
            // us fails in more than one way. Cosmetic filtering is best
            // effort; request blocking is already attached.
            _documentStartScript = null;
            System.Diagnostics.Trace.TraceWarning(
                $"Cosmetic payload registration failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Settings the Avalonia wrapper does not expose. Multiple windows and
    /// script-opened windows are how these player hosts launch popunders, and
    /// nothing in an embedded player needs either.
    /// </summary>
    private static void Harden(WebSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        settings.SetSupportMultipleWindows(false);
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.SetGeolocationEnabled(false);
        settings.SaveFormData = false;
        settings.DatabaseEnabled = false;
        settings.MixedContentMode = MixedContentHandling.NeverAllow;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            settings.SafeBrowsingEnabled = true;
        }
    }

    private sealed class FilteringWebViewClient(
        WebViewClient? inner,
        IContentFilterSession session,
        bool hasDocumentStartScript,
        Action<EmbeddedWebMediaResponse> publishMediaResponse) :
        WebViewClient
    {
        private static readonly byte[] Empty = [];

        public override WebResourceResponse? ShouldInterceptRequest(
            PlatformWebView? view,
            IWebResourceRequest? request)
        {
            // Nothing may escape into the Java caller: there is no managed
            // frame above this to catch it, and the process ends. A failure
            // means the request is not filtered, never that playback stops.
            try
            {
                return FilterRequest(view, request);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Filtering a request failed: {exception.Message}");
                return Forward(view, request);
            }
        }

        private WebResourceResponse? FilterRequest(
            PlatformWebView? view,
            IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (request is null ||
                string.IsNullOrEmpty(url) ||
                !session.IsActive ||
                request.IsForMainFrame)
            {
                // The top-level document is decided by the navigation handler,
                // which can cancel it outright. Answering it here would leave
                // the viewer on a blank page with no explanation.
                return Forward(view, request);
            }

            var decision = session.Match(url, MapResourceType(request, url));
            if (!decision.IsBlocking)
            {
                ObserveMediaRequest(request, url);
                return Forward(view, request);
            }

            var surrogate = decision.RedirectResource is { } resource
                ? session.Surrogate(resource)
                : null;
            return surrogate is null
                ? new WebResourceResponse(
                    "text/plain",
                    "utf-8",
                    403,
                    "Blocked",
                    null,
                    new MemoryStream(Empty))
                : new WebResourceResponse(
                    "text/plain",
                    "utf-8",
                    200,
                    "OK",
                    null,
                    new MemoryStream(Encoding.UTF8.GetBytes(surrogate)));
        }

        private void ObserveMediaRequest(
            IWebResourceRequest request,
            string url)
        {
            if (!string.Equals(
                    request.Method,
                    "GET",
                    StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !IsPotentialNuploadMediaRequest(uri) ||
                session.DocumentUri is not { } documentUri)
            {
                return;
            }

            publishMediaResponse(new EmbeddedWebMediaResponse(
                documentUri,
                uri,
                StatusCode: null,
                ContentType: null,
                ReadHeader(request, "Referer"),
                ReadHeader(request, "Origin"),
                ReadHeader(request, "User-Agent")));
        }

        private static bool IsPotentialNuploadMediaRequest(Uri uri)
        {
            var isIbraHost =
                string.Equals(
                    uri.IdnHost,
                    "ibra.lat",
                    StringComparison.OrdinalIgnoreCase) ||
                uri.IdnHost.EndsWith(
                    ".ibra.lat",
                    StringComparison.OrdinalIgnoreCase);
            return isIbraHost &&
                   (uri.AbsolutePath.EndsWith(
                        ".m3u8",
                        StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath == "/" &&
                    (uri.Query.StartsWith(
                         "?s=",
                         StringComparison.Ordinal) ||
                     uri.Query.Contains(
                         "&s=",
                         StringComparison.Ordinal)));
        }

        private static string? ReadHeader(
            IWebResourceRequest request,
            string name) =>
            request.RequestHeaders?
                .FirstOrDefault(header => string.Equals(
                    header.Key,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                .Value;

        private WebResourceResponse? Forward(
            PlatformWebView? view,
            IWebResourceRequest? request) =>
            inner?.ShouldInterceptRequest(view, request);

        /// <summary>
        /// Android does not tell a client what kind of resource a request is
        /// for, so it is inferred from the <c>Accept</c> header the loader
        /// sends and from the path. These are hints: the engine protects media
        /// but applies rules to same-site subresources as well.
        /// </summary>
        private static ResourceType MapResourceType(
            IWebResourceRequest request,
            string url)
        {
            var accept = request.RequestHeaders?
                .FirstOrDefault(header => string.Equals(
                    header.Key,
                    "Accept",
                    StringComparison.OrdinalIgnoreCase))
                .Value ?? string.Empty;

            if (accept.Contains("text/css", StringComparison.Ordinal))
            {
                return ResourceType.StyleSheet;
            }

            if (accept.Contains("image/", StringComparison.Ordinal))
            {
                return ResourceType.Image;
            }

            if (accept.Contains("text/html", StringComparison.Ordinal))
            {
                return ResourceType.SubDocument;
            }

            var path = url.AsSpan();
            var query = path.IndexOf('?');
            if (query >= 0)
            {
                path = path[..query];
            }

            return path switch
            {
                _ when Ends(path, ".js") || Ends(path, ".mjs") =>
                    ResourceType.Script,
                _ when Ends(path, ".css") => ResourceType.StyleSheet,
                _ when Ends(path, ".png") || Ends(path, ".jpg") ||
                    Ends(path, ".jpeg") || Ends(path, ".gif") ||
                    Ends(path, ".webp") || Ends(path, ".svg") =>
                    ResourceType.Image,
                _ when Ends(path, ".woff") || Ends(path, ".woff2") ||
                    Ends(path, ".ttf") || Ends(path, ".otf") =>
                    ResourceType.Font,
                _ when Ends(path, ".mp4") || Ends(path, ".webm") ||
                    Ends(path, ".m3u8") || Ends(path, ".mpd") =>
                    ResourceType.Media,
                _ => ResourceType.XmlHttpRequest
            };
        }

        private static bool Ends(ReadOnlySpan<char> path, string extension) =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        public override void OnPageStarted(
            PlatformWebView? view,
            string? url,
            Bitmap? favicon)
        {
            inner?.OnPageStarted(view, url, favicon);
            if (view is null ||
                string.IsNullOrEmpty(url) ||
                hasDocumentStartScript)
            {
                // With a document-start script registered the payload has
                // already run, before the page's own scripts. Injecting again
                // here would only re-enter a guarded bootstrap.
                return;
            }

            // The fallback: later than document start, but still far earlier
            // than waiting for the page to finish, which is when the
            // advertisement has already drawn itself.
            try
            {
                var host = new Uri(url).Host;
                if (host.Length > 0)
                {
                    view.EvaluateJavascript(
                        session.BuildInjectionPayload(host),
                        null);
                }
            }
            catch (UriFormatException)
            {
            }
        }

        public override bool ShouldOverrideUrlLoading(
            PlatformWebView? view,
            IWebResourceRequest? request) =>
            // The request-shaped overload arrived in API 24. This client only
            // ever installs itself on 26 and later, but the guard is what says
            // so to the platform analyzer rather than to a reader.
            OperatingSystem.IsAndroidVersionAtLeast(24) &&
            inner?.ShouldOverrideUrlLoading(view, request) is true;

        public override void OnPageFinished(PlatformWebView? view, string? url) =>
            inner?.OnPageFinished(view, url);

        public override void OnPageCommitVisible(
            PlatformWebView? view,
            string? url) =>
            inner?.OnPageCommitVisible(view, url);

        public override void OnLoadResource(
            PlatformWebView? view,
            string? url) =>
            inner?.OnLoadResource(view, url);

        public override void DoUpdateVisitedHistory(
            PlatformWebView? view,
            string? url,
            bool isReload) =>
            inner?.DoUpdateVisitedHistory(view, url, isReload);

        public override void OnReceivedError(
            PlatformWebView? view,
            IWebResourceRequest? request,
            WebResourceError? error) =>
            inner?.OnReceivedError(view, request, error);

        public override void OnReceivedHttpError(
            PlatformWebView? view,
            IWebResourceRequest? request,
            WebResourceResponse? errorResponse) =>
            inner?.OnReceivedHttpError(view, request, errorResponse);
    }
}
