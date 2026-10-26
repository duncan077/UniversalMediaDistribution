using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>
/// Refuses blocked requests inside WebView2 before they leave the machine.
///
/// This is what the injected policy could never do. A page that captures
/// <c>fetch</c> before our script runs, or that fires a request from a worker,
/// is filtered here regardless — <c>WebResourceRequested</c> lets a handler
/// answer a request with a response of its own choosing, and a request that is
/// answered is a request that was never made.
///
/// The handler runs on the UI thread, so it does exactly what the engine's
/// match path promises and no more: no allocation, no waiting.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WebView2ContentFilterHook : INativeWebViewFilterHook
{
    private RequestHandler? _handler;
    private ResponseHandler? _responseHandler;
    private readonly MediaRequestCorrelation _mediaRequests = new();
    private ICoreWebView2? _webView;
    private IContentFilterSession? _session;
    private string? _scriptId;
    private long _token;
    private long _responseToken;

    /// <summary>
    /// The <c>ICoreWebView2</c> the current registration belongs to. Comparing
    /// against it is what detects an adapter that was destroyed and recreated.
    /// </summary>
    private nint _attachedTo;

    public event Action<EmbeddedWebMediaResponse>? MediaResponseReceived;

    private void Detach()
    {
        if (_session is not null)
        {
            _session.DocumentOpened -= OnDocumentOpened;
        }

        _responseHandler?.Deactivate();
        _mediaRequests.Clear();

        // The old core is gone; there is nothing to unsubscribe from it, and
        // nothing that would succeed if we tried.
        _webView = null;
        _handler = null;
        _responseHandler = null;
        _session = null;
        _scriptId = null;
        _responseToken = 0;
        _attachedTo = nint.Zero;
    }

    /// <summary>
    /// True once requests are being filtered at the network layer. While this
    /// is false the injected policy is the only thing filtering, which is
    /// weaker — so a host that cares can tell the difference.
    /// </summary>
    public bool IsAttached => _handler is not null;

    /// <summary>
    /// Why the last attach attempt did not take. Null once attached. Every
    /// step below can fail for a reason worth naming; silently falling back
    /// makes "filtering does not work" impossible to diagnose.
    /// </summary>
    public string? AttachFailure { get; private set; }

    public int RequestsSeen => _handler?.Seen ?? 0;

    public int RequestsBlocked => _handler?.Blocked ?? 0;

    public bool TryAttach(
        NativeWebView webView,
        IContentFilterSession session,
        IPlatformHandle? platformHandle = null)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var handle = platformHandle ?? webView.TryGetPlatformHandle();
        if (handle is not IWindowsWebView2PlatformHandle webView2)
        {
            // WebView1, or an adapter that has not been created yet. The
            // caller retries on every navigation.
            return Fail(
                handle is null
                    ? "the platform handle is not available yet"
                    : $"the adapter is {handle.HandleDescriptor}, not WebView2");
        }

        if (webView2.CoreWebView2 == nint.Zero)
        {
            return Fail("the WebView2 core has not been created yet");
        }

        if (_handler is not null && webView2.CoreWebView2 == _attachedTo)
        {
            return true;
        }

        // A different core than the one we filtered before: the adapter was
        // recreated underneath us and the old registration went with it.
        Detach();

        var core = (ICoreWebView2)WebView2Interop.Wrappers
            .GetOrCreateObjectForComInstance(
                webView2.CoreWebView2,
                CreateObjectFlags.None);
        if (core is not ICoreWebView2_2 versioned)
        {
            return Fail("this WebView2 runtime does not expose ICoreWebView2_2");
        }

        var environmentResult = versioned.GetEnvironment(out var environment);
        if (environmentResult != 0 || environment is null)
        {
            // Without the environment there is no way to build the response
            // that stands in for a blocked request, and a filter that cannot
            // answer is not a filter.
            return Fail(
                $"the WebView2 environment was refused (0x{environmentResult:X8})");
        }

        var handler = new RequestHandler(
            session,
            environment,
            _mediaRequests);
        var subscribeResult = core.AddWebResourceRequested(handler, out _token);
        if (subscribeResult != 0)
        {
            return Fail(
                $"WebResourceRequested was refused (0x{subscribeResult:X8})");
        }

        // Every context, so a request made from a worker or an event source is
        // seen the same as one made from the document.
        core.AddWebResourceRequestedFilter("*", WebResourceContext.All);

        var responseHandler =
            new ResponseHandler(
                _mediaRequests,
                PublishMediaResponse);
        var responseSubscribeResult =
            versioned.AddWebResourceResponseReceived(
                responseHandler,
                out _responseToken);
        if (responseSubscribeResult == 0)
        {
            _responseHandler = responseHandler;
        }
        else
        {
            _responseToken = 0;
            System.Diagnostics.Trace.TraceWarning(
                "Embedded media response capture was refused by WebView2 " +
                $"(0x{responseSubscribeResult:X8}).");
        }

        _webView = core;
        _handler = handler;
        _session = session;
        _attachedTo = webView2.CoreWebView2;
        AttachFailure = null;
        session.DocumentOpened += OnDocumentOpened;
        if (session.DocumentUri is { } document)
        {
            OnDocumentOpened(document);
        }

        System.Diagnostics.Trace.TraceInformation(
            "Content filtering attached to WebView2.");

        if (EmbeddedWebDiagnostics.IsDevToolsRequested)
        {
            // Opened from here rather than left to a keystroke: F12 inside the
            // hosted page is not reliably ours to claim, and the host
            // application binds it to its own inspector.
            try
            {
                core.OpenDevToolsWindow();
            }
            catch (COMException)
            {
                // The runtime may refuse when developer tools are disabled by
                // policy. Filtering is attached either way.
            }
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
                $"Publishing an embedded media response failed: " +
                $"{exception.Message}");
        }
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

    private bool Fail(string reason)
    {
        if (!string.Equals(AttachFailure, reason, StringComparison.Ordinal))
        {
            AttachFailure = reason;
            System.Diagnostics.Trace.TraceWarning(
                $"Content filtering is not attached to WebView2: {reason}. " +
                "The injected policy is filtering instead.");
        }

        return false;
    }

    /// <summary>
    /// Replaces the cosmetic payload with the one for the page now being
    /// opened. Registering at document creation wins a race that injecting
    /// after a navigation completes does not; replacing rather than adding is
    /// what stops a viewer who tries three servers from accumulating three
    /// payloads.
    /// </summary>
    private void OnDocumentOpened(Uri document)
    {
        if (_webView is not { } core || _session is not { } session)
        {
            return;
        }

        try
        {
            _mediaRequests.Clear();
            if (_scriptId is { Length: > 0 } previous)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(previous);
                _scriptId = null;
            }

            var host = document.Host;
            if (host.Length > 0)
            {
                core.AddScriptToExecuteOnDocumentCreated(
                    session.BuildInjectionPayload(host),
                    new ScriptRegistrationHandler(id => _scriptId = id));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Every exception, not only COMException. This runs from a
            // dispatcher callback, so anything that escapes is unhandled on
            // the UI thread and takes the application with it — and a core
            // being torn down underneath us throws in more than one way.
            // Cosmetic filtering is best effort; network blocking, which is
            // what stops the popunders, is already attached.
            _scriptId = null;
            System.Diagnostics.Trace.TraceWarning(
                $"Cosmetic payload registration failed: {exception.Message}");
        }
    }

    [GeneratedComClass]
    private sealed partial class ScriptRegistrationHandler(
        Action<string?> onRegistered) :
        ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
    {
        public int Invoke(int errorCode, string? id)
        {
            if (errorCode == 0)
            {
                onRegistered(id);
            }

            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class RequestHandler(
        IContentFilterSession session,
        ICoreWebView2Environment environment,
        MediaRequestCorrelation mediaRequests) :
        ICoreWebView2WebResourceRequestedEventHandler
    {
        public int Seen { get; private set; }

        public int Blocked { get; private set; }

        public int Invoke(
            ICoreWebView2? sender,
            ICoreWebView2WebResourceRequestedEventArgs? args)
        {
            // A throw here would cross a COM boundary, where there is no
            // managed frame to catch it and the process ends. Every failure is
            // swallowed and treated as "allow": the alternative is not a
            // filtered request, it is a dead application.
            try
            {
                Filter(args);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Filtering a request failed: {exception.Message}");
            }

            return 0;
        }

        private void Filter(ICoreWebView2WebResourceRequestedEventArgs? args)
        {
            if (args is null ||
                !session.IsActive ||
                args.GetRequest(out var request) != 0 ||
                request is null ||
                request.GetUri(out var uriPointer) != 0 ||
                uriPointer == nint.Zero)
            {
                return;
            }

            string? url;
            try
            {
                url = Marshal.PtrToStringUni(uriPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(uriPointer);
            }

            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            Seen++;
            var type = args.GetResourceContext(out var context) == 0
                ? Map(context)
                : ResourceType.Other;
            var decision = session.Match(url, type);
            if (!decision.IsBlocking)
            {
                mediaRequests.Record(url, session.DocumentUri);
                return;
            }

            Blocked++;

            var body = decision.RedirectResource is { } resource
                ? session.Surrogate(resource)
                : null;
            if (environment.CreateWebResourceResponse(
                    nint.Zero,
                    body is null ? 403 : 200,
                    body is null ? "Blocked" : "OK",
                    body is null
                        ? string.Empty
                        : "Content-Type: text/plain\r\n",
                    out var response) == 0 &&
                response is not null)
            {
                args.SetResponse(response);
            }
        }

        /// <summary>
        /// WebView2's request contexts, in the terms filter lists are written
        /// in. <c>Fetch</c> joins <c>XmlHttpRequest</c> because filter syntax
        /// has one option for both.
        /// </summary>
        private static ResourceType Map(WebResourceContext context) =>
            context switch
            {
                WebResourceContext.Document => ResourceType.SubDocument,
                WebResourceContext.Stylesheet => ResourceType.StyleSheet,
                WebResourceContext.Image => ResourceType.Image,
                WebResourceContext.Media => ResourceType.Media,
                WebResourceContext.Font => ResourceType.Font,
                WebResourceContext.Script => ResourceType.Script,
                WebResourceContext.XmlHttpRequest =>
                    ResourceType.XmlHttpRequest,
                WebResourceContext.Fetch => ResourceType.XmlHttpRequest,
                WebResourceContext.TextTrack => ResourceType.Media,
                WebResourceContext.EventSource => ResourceType.Other,
                WebResourceContext.Websocket => ResourceType.WebSocket,
                WebResourceContext.Ping => ResourceType.Ping,
                _ => ResourceType.Other
            };
    }

    private sealed class MediaRequestCorrelation
    {
        private const int MaximumEntries = 64;
        private readonly object _gate = new();
        private readonly Dictionary<string, Uri> _documents =
            new(StringComparer.Ordinal);

        public void Record(string url, Uri? documentUri)
        {
            if (documentUri is null ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !IsPotentialNuploadMediaRequest(uri))
            {
                return;
            }

            lock (_gate)
            {
                if (_documents.Count >= MaximumEntries &&
                    !_documents.ContainsKey(url))
                {
                    _documents.Clear();
                }

                _documents[url] = documentUri;
            }
        }

        public bool TryTake(
            string url,
            out Uri documentUri)
        {
            lock (_gate)
            {
                if (_documents.Remove(url, out var value))
                {
                    documentUri = value;
                    return true;
                }
            }

            documentUri = null!;
            return false;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _documents.Clear();
            }
        }
    }

    [GeneratedComClass]
    private sealed partial class ResponseHandler(
        MediaRequestCorrelation mediaRequests,
        Action<EmbeddedWebMediaResponse> publish) :
        ICoreWebView2WebResourceResponseReceivedEventHandler
    {
        private volatile bool _active = true;

        public void Deactivate() => _active = false;

        public int Invoke(
            ICoreWebView2? sender,
            ICoreWebView2WebResourceResponseReceivedEventArgs? args)
        {
            // This callback crosses the same COM boundary as the request
            // filter. Nothing may escape it: capture is an optimization and a
            // malformed response must leave the WebView playing.
            try
            {
                Observe(args);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Inspecting an embedded media response failed: " +
                    $"{exception.Message}");
            }

            return 0;
        }

        private void Observe(
            ICoreWebView2WebResourceResponseReceivedEventArgs? args)
        {
            if (!_active ||
                args is null ||
                args.GetRequest(out var request) != 0 ||
                request is null ||
                args.GetResponse(out var response) != 0 ||
                response is null ||
                response.GetStatusCode(out var statusCode) != 0 ||
                !string.Equals(
                    ReadMethod(request),
                    "GET",
                    StringComparison.OrdinalIgnoreCase) ||
                ReadUri(request) is not { } requestUri)
            {
                return;
            }

            var isUnavailable = statusCode is
                (int)HttpStatusCode.NotFound or
                (int)HttpStatusCode.Gone;
            if (!isUnavailable &&
                statusCode is < 200 or >= 300)
            {
                return;
            }

            string? contentType = null;
            if (response.GetHeaders(out var responseHeaders) == 0 &&
                responseHeaders is not null)
            {
                contentType = ReadHeader(
                    responseHeaders,
                    "Content-Type");
            }

            if (!IsPotentialNuploadMediaRequest(requestUri) ||
                !isUnavailable &&
                !LooksLikeHls(requestUri, contentType))
            {
                return;
            }

            if (!mediaRequests.TryTake(
                    requestUri.AbsoluteUri,
                    out var documentUri))
            {
                return;
            }

            string? referrer = null;
            string? origin = null;
            string? userAgent = null;
            if (request.GetHeaders(out var requestHeaders) == 0 &&
                requestHeaders is not null)
            {
                referrer = ReadHeader(requestHeaders, "Referer");
                origin = ReadHeader(requestHeaders, "Origin");
                userAgent = ReadHeader(requestHeaders, "User-Agent");
            }

            publish(new EmbeddedWebMediaResponse(
                documentUri,
                requestUri,
                statusCode,
                contentType,
                referrer,
                origin,
                userAgent));
        }

        private static Uri? ReadUri(
            ICoreWebView2WebResourceRequest request)
        {
            if (request.GetUri(out var pointer) != 0 ||
                pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                var value = Marshal.PtrToStringUni(pointer);
                return Uri.TryCreate(
                    value,
                    UriKind.Absolute,
                    out var uri)
                    ? uri
                    : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        private static string? ReadMethod(
            ICoreWebView2WebResourceRequest request)
        {
            if (request.GetMethod(out var pointer) != 0 ||
                pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        private static string? ReadHeader(
            ICoreWebView2HttpRequestHeaders headers,
            string name)
        {
            if (headers.GetHeader(name, out var pointer) != 0 ||
                pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        private static string? ReadHeader(
            ICoreWebView2HttpResponseHeaders headers,
            string name)
        {
            if (headers.GetHeader(name, out var pointer) != 0 ||
                pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        private static bool LooksLikeHls(
            Uri uri,
            string? contentType)
        {
            if (uri.AbsolutePath.EndsWith(
                    ".m3u8",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var separator = contentType?.IndexOf(';') ?? -1;
            var mediaType = separator >= 0
                ? contentType![..separator]
                : contentType;
            return mediaType?.Contains(
                "mpegurl",
                StringComparison.OrdinalIgnoreCase) is true;
        }
    }
}
