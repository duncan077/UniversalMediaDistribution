using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Runtime.InteropServices.Marshalling;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>
/// Satisfies a host's browser check by letting a real browser engine answer
/// it, then hands the resulting cookies back so ordinary requests can carry
/// them.
/// <para>
/// Windows uses WebView2 request observation. Linux uses a WebKitGTK dialog
/// and Avalonia's cookie/script APIs, including HttpOnly clearance cookies.
/// </para>
/// </summary>
public sealed partial class DesktopWebChallengeSolver : IWebChallengeSolver
{
    /// <summary>
    /// The same budget an ordinary content-source request gets. A browser is
    /// slower than a socket, but not so much slower that it deserves to hold a
    /// user up for longer than the provider it stands in for would.
    /// </summary>
    private static readonly TimeSpan SolveTimeout = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// How long a single COM callback is waited on. They are asynchronous, so
    /// a lost one would otherwise hang the attempt with the window still open.
    /// </summary>
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The cookie a passed Cloudflare check leaves behind. Waiting for it
    /// specifically is what separates "the check completed" from "a page
    /// loaded", which for an interstitial are not the same moment.
    /// </summary>
    private const string ClearanceCookieName = "cf_clearance";

    /// <summary>
    /// Below this a document is still a stub or an error page rather than a
    /// page worth parsing.
    /// </summary>
    private const int MinimumDocumentLength = 1000;

    private static readonly SemaphoreSlim SolveGate = new(1, 1);

    public async ValueTask<WebChallengeClearance?> SolveAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return null;
        }

        // One browser at a time. Several challenged hosts in the same refresh
        // would otherwise each open their own.
        await SolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread
                .InvokeAsync(() => OperatingSystem.IsLinux()
                    ? SolveLinuxAsync(endpoint, cancellationToken)
                    : SolveOnUiThreadAsync(endpoint, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=solve-failed; " +
                $"failure={exception.GetType().Name}; " +
                $"message={exception.Message}");
            return null;
        }
        finally
        {
            SolveGate.Release();
        }
    }

    private static async Task<WebChallengeClearance?> SolveOnUiThreadAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        // Source is deliberately not set here. Assigning it while the control
        // is still detached creates the platform adapter before the visual
        // tree is ready, which is the hazard the embedded player works around
        // the same way: attach first, navigate after.
        var webView = new NativeWebView
        {
            Background = Brushes.Black
        };
        var window = new Window
        {
            Content = webView,
            Width = 900,
            Height = 700,
            ShowInTaskbar = false,
            CanResize = false,
            // Fully transparent and never focused. Setting only Position was
            // not enough: the startup location is applied during Show and puts
            // the window back on screen.
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        // Resolved once the adapter exists and then held: re-wrapping the same
        // native pointer on every poll hands out wrappers that each expect to
        // release it.
        ICoreWebView2? core = null;
        ClearanceObserver? observer = null;
        long token = 0;
        try
        {
            Mark("show");
            window.Show();
            window.Position = new PixelPoint(-32000, -32000);

            // Let the attach settle before anything asks the control to
            // navigate, for the same reason the player defers its own
            // navigation until the view model has placed it.
            await Task.Delay(PollInterval, cancellationToken)
                .ConfigureAwait(true);
            Mark("await-core");
            var deadline = Environment.TickCount64 +
                (long)SolveTimeout.TotalMilliseconds;
            while (core is null && Environment.TickCount64 < deadline &&
                   !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, cancellationToken)
                    .ConfigureAwait(true);
                core = TryResolveCore(webView);
            }

            if (core is not null)
            {
                Mark("core:ok");
                var completion =
                    new TaskCompletionSource<WebChallengeClearance>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                observer = new ClearanceObserver(endpoint.IdnHost, completion);

                // The filter has to be in place before the navigation that
                // triggers the check, or its requests are never offered.
                core.AddWebResourceRequestedFilter("*", WebResourceContext.All);
                if (core.AddWebResourceRequested(observer, out token) == 0)
                {
                    Mark("observer:registered");
                    core.Navigate(endpoint.AbsoluteUri);
                    Mark("navigate:ok");
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining > 0)
                    {
                        try
                        {
                            var clearance = await completion.Task
                                .WaitAsync(
                                    TimeSpan.FromMilliseconds(remaining),
                                    cancellationToken)
                                .ConfigureAwait(true);
                            Mark("clearance:ok");
                            ApplicationDiagnosticLog.Information(
                                "web.challenge",
                                $"host={endpoint.IdnHost}; stage=cleared");
                            return clearance;
                        }
                        catch (TimeoutException)
                        {
                            Mark(
                                $"clearance:timeout seen={observer.Seen} " +
                                $"host={observer.SeenForHost} " +
                                $"withCookie={observer.SeenWithCookie} " +
                                $"names=[{string.Join(",", observer.CookieNames)}]");
                        }
                    }
                }
            }

            ApplicationDiagnosticLog.Warning(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=timed-out; " +
                $"core={(core is null ? "never-created" : "created")}");
            return null;
        }
        finally
        {
            // Order matters: stop the page before the wrapper goes, and close
            // the window only once nothing holds the core.
            try
            {
                Mark("teardown:enter");
                if (core is not null && token != 0)
                {
                    core.RemoveWebResourceRequested(token);
                }

                core?.Navigate("about:blank");
            }
            catch
            {
                // Already gone; the close below is still what matters.
            }

            core = null;
            GC.KeepAlive(observer);
            observer = null;
            window.Close();
            Mark("teardown:ok");
        }
    }


    /// <summary>
    /// Writes a stage marker straight to disk. The application log is buffered
    /// and a process fault loses whatever is still in flight, which is exactly
    /// the record needed to find one.
    /// </summary>
    private static void Mark(string stage)
    {
        var path = Environment.GetEnvironmentVariable("UMD_WEB_CHALLENGE_TRACE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                path,
                $"{DateTime.UtcNow:HH:mm:ss.fff} {stage}{Environment.NewLine}");
        }
        catch
        {
            // Tracing must never be why something fails.
        }
    }

    private static ICoreWebView2? TryResolveCore(NativeWebView webView)
    {
        if (webView.TryGetPlatformHandle() is not
                IWindowsWebView2PlatformHandle handle ||
            handle.CoreWebView2 == nint.Zero)
        {
            return null;
        }

        // The adapter owns this pointer; the wrapper does not get it for
        // free. GetOrCreateObjectForComInstance does not consume a reference,
        // but the runtime callable wrapper it returns will release one when it
        // is collected. Without this AddRef that release lands on a reference
        // this code never held, the core is torn down early, and the fault
        // surfaces later inside the message loop rather than at any call here.
        Marshal.AddRef(handle.CoreWebView2);
        return (ICoreWebView2)WebView2Interop.Wrappers
            .GetOrCreateObjectForComInstance(
                handle.CoreWebView2,
                CreateObjectFlags.None);
    }


    /// <summary>
    /// Loads the page in the browser and returns what it rendered.
    /// <para>
    /// For a host that issues no clearance cookie there is nothing to copy
    /// into an ordinary request, so the page itself is the answer. The wait
    /// ends when the document stops looking like an interstitial and starts
    /// looking like content.
    /// </para>
    /// </summary>
    public async ValueTask<WebChallengeDocument?> FetchDocumentAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return null;
        }

        await SolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread
                .InvokeAsync(() => OperatingSystem.IsLinux()
                    ? FetchLinuxAsync(endpoint, cancellationToken)
                    : FetchOnUiThreadAsync(endpoint, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=fetch-failed; " +
                $"failure={exception.GetType().Name}; " +
                $"message={exception.Message}");
            return null;
        }
        finally
        {
            SolveGate.Release();
        }
    }

    private static async Task<WebChallengeDocument?> FetchOnUiThreadAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        var webView = new NativeWebView { Background = Brushes.Black };
        var window = new Window
        {
            Content = webView,
            Width = 900,
            Height = 700,
            ShowInTaskbar = false,
            CanResize = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        ICoreWebView2? core = null;
        try
        {
            Mark($"fetch:show url={endpoint.AbsoluteUri}");
            window.Show();
            window.Position = new PixelPoint(-32000, -32000);
            await Task.Delay(PollInterval, cancellationToken)
                .ConfigureAwait(true);

            var deadline = Environment.TickCount64 +
                (long)DocumentTimeout.TotalMilliseconds;
            while (core is null && Environment.TickCount64 < deadline &&
                   !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, cancellationToken)
                    .ConfigureAwait(true);
                core = TryResolveCore(webView);
            }

            if (core is null)
            {
                Mark("fetch:no-core");
                return null;
            }

            var loggedShort = false;
            var documents = Channel.CreateUnbounded<string>();
            void OnMessage(object? sender, WebMessageReceivedEventArgs args)
            {
                if (args.Body is { Length: > 0 } message)
                {
                    documents.Writer.TryWrite(message);
                }
            }

            webView.WebMessageReceived += OnMessage;
            try
            {
                // Registered through the same call the content filter uses to
                // install its own script, so no new interop is involved.
                core.AddScriptToExecuteOnDocumentCreated(
                    DocumentReporterScript,
                    null);
                Mark("fetch:navigate");
                core.Navigate(endpoint.AbsoluteUri);

                while (Environment.TickCount64 < deadline &&
                       !cancellationToken.IsCancellationRequested)
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    string message;
                    try
                    {
                        using var attemptWindow = new CancellationTokenSource(
                            TimeSpan.FromMilliseconds(remaining));
                        using var linked = CancellationTokenSource
                            .CreateLinkedTokenSource(
                                attemptWindow.Token,
                                cancellationToken);
                        message = await documents.Reader
                            .ReadAsync(linked.Token)
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (ParseReport(message) is not { } document)
                    {
                        continue;
                    }

                    var html = document.Body;
                    if (!document.IsBase64 && LooksLikeChallenge(html))
                    {
                        Mark($"fetch:challenge-pending length={html.Length}");
                        continue;
                    }

                    // Only a page is expected to be substantial. A JSON or
                    // plain-text route legitimately answers in a few hundred
                    // bytes, and gating those on length threw away good
                    // responses until the attempt timed out.
                    var isPage = document.ContentType.Contains(
                        "html",
                        StringComparison.OrdinalIgnoreCase);
                    if (isPage && html.Length < MinimumDocumentLength)
                    {
                        // The length alone says nothing about why. Record the
                        // start of it once so a stub page can be identified.
                        if (!loggedShort)
                        {
                            loggedShort = true;
                            // Char casts avoid escape sequences entirely.
                            var preview = html[..Math.Min(300, html.Length)]
                                .Replace((char)13, (char)32)
                                .Replace((char)10, (char)32);
                            Mark(
                                $"fetch:too-short length={html.Length} " +
                                $"body={preview}");
                        }

                        continue;
                    }

                    if (html.Length == 0)
                    {
                        continue;
                    }

                    Mark(
                        $"fetch:ok length={html.Length} " +
                        $"type={document.ContentType}");
                    ApplicationDiagnosticLog.Information(
                        "web.challenge",
                        $"host={endpoint.IdnHost}; stage=document; " +
                        $"length={html.Length}; type={document.ContentType}");
                    return document;
                }
            }
            finally
            {
                webView.WebMessageReceived -= OnMessage;
            }

            Mark("fetch:timeout");
            ApplicationDiagnosticLog.Warning(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=document-timed-out");
            return null;
        }
        finally
        {
            Mark("fetch:teardown");
            try
            {
                core?.Navigate("about:blank");
            }
            catch
            {
                // Already gone.
            }

            core = null;
            window.Close();
        }
    }

    /// <summary>
    /// Script installed before any page script runs, which pushes the rendered
    /// document out over the web-message channel.
    /// <para>
    /// Reading the document by calling into the browser needs a completion
    /// handler declared by hand, and hand-declared handlers are what faulted
    /// the process twice. Having the page post its own content instead uses
    /// only interop this application already had, and the reply arrives on
    /// Avalonia's managed event rather than through COM.
    /// </para>
    /// </summary>
    internal static readonly string DocumentReporterScript =
        $$"""
        (function () {
            function post() {
                try {
                    var report = {{WebChallengeDocument.ReportExpression}};
                    if (report) window.chrome.webview.postMessage(JSON.stringify(report));
                } catch (error) {
                }
            }

            document.addEventListener('DOMContentLoaded', post);
            window.addEventListener('load', post);
            setInterval(post, 1200);
        })();
        """;


    /// <summary>
    /// Reads the envelope the injected reporter posts, which pairs the body
    /// with the media type the browser saw.
    /// </summary>
    private static WebChallengeDocument? ParseReport(string message)
        => WebChallengeDocument.ParseReport(message);

    /// <summary>
    /// Whether the document is still the bot check rather than the page.
    /// </summary>
    private static bool LooksLikeChallenge(string html) =>
        html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf_chl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Watches the browser's own outgoing requests and reports the headers it
    /// sends once the check has been satisfied.
    /// <para>
    /// The clearance cookie is HttpOnly, so page script cannot see it, and
    /// reading the cookie jar means hand-declaring a COM surface this
    /// application does not otherwise use. The browser attaches the cookie to
    /// every request it makes anyway, and the header path is already exercised
    /// by the content filter — so the answer is read from a request rather
    /// than from the jar. The same request carries the user agent the cookie
    /// was issued against, which is the other half of what clearance needs.
    /// </para>
    /// </summary>
    [GeneratedComClass]
    private sealed partial class ClearanceObserver(
        string host,
        TaskCompletionSource<WebChallengeClearance> completion) :
        ICoreWebView2WebResourceRequestedEventHandler
    {
        public int Seen { get; private set; }

        public int SeenForHost { get; private set; }

        public int SeenWithCookie { get; private set; }

        /// <summary>
        /// Cookie names the host has set so far. Cloudflare's in-progress
        /// challenge cookies look different from the clearance it issues on
        /// success, so this distinguishes "still working" from "never started".
        /// </summary>
        public HashSet<string> CookieNames { get; } = new(StringComparer.Ordinal);

        public int Invoke(
            ICoreWebView2? sender,
            ICoreWebView2WebResourceRequestedEventArgs? args)
        {
            try
            {
                Seen++;
                if (args is null ||
                    args.GetRequest(out var request) != 0 ||
                    request is null ||
                    request.GetHeaders(out var headers) != 0 ||
                    headers is null)
                {
                    return 0;
                }

                var uri = ReadString(request.GetUri);
                if (uri is null ||
                    !Uri.TryCreate(uri, UriKind.Absolute, out var requestUri) ||
                    !string.Equals(
                        requestUri.IdnHost,
                        host,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                SeenForHost++;
                var cookie = ReadHeader(headers, "Cookie");
                if (cookie is not null)
                {
                    SeenWithCookie++;
                    foreach (var pair in cookie.Split(
                                 ';',
                                 StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries))
                    {
                        var equals = pair.IndexOf('=', StringComparison.Ordinal);
                        CookieNames.Add(equals > 0 ? pair[..equals] : pair);
                    }
                }

                if (cookie is null ||
                    !cookie.Contains(ClearanceCookieName, StringComparison.Ordinal))
                {
                    // Still the interstitial, or a request made before it
                    // cleared.
                    return 0;
                }

                var userAgent = ReadHeader(headers, "User-Agent");
                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    completion.TrySetResult(
                        new WebChallengeClearance(host, cookie, userAgent));
                }
            }
            catch
            {
                // A managed exception crossing back into COM would take the
                // process with it.
            }

            return 0;
        }

        private static string? ReadHeader(
            ICoreWebView2HttpRequestHeaders headers,
            string name) =>
            headers.Contains(name, out var present) == 0 && present != 0 &&
            headers.GetHeader(name, out var pointer) == 0 &&
            pointer != nint.Zero
                ? ReadAndFree(pointer)
                : null;

        private delegate int StringGetter(out nint pointer);

        private static string? ReadString(StringGetter getter) =>
            getter(out var pointer) == 0 && pointer != nint.Zero
                ? ReadAndFree(pointer)
                : null;

        private static string? ReadAndFree(nint pointer)
        {
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }
}
