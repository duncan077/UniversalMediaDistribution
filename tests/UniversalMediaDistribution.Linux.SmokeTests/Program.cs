using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Desktop.Web;
using UniversalMediaDistribution.Playback.LibVLC;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.Linux.SmokeTests;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return 2;
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("Linux integration checks must be published with NativeAOT.");
        Console.WriteLine("PASS: NativeAOT runtime (dynamic code generation disabled)");
        return AppBuilder.Configure<SmokeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class SmokeApp : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.MainWindow = new Window { Width = 320, Height = 200, Title = "Linux integration checks" };
        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            try { await VerifyAsync(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                if (ApplicationDiagnosticLog.FilePath is { } path && File.Exists(path))
                    Console.Error.WriteLine(await File.ReadAllTextAsync(path));
                if (File.Exists("/tmp/umd-challenge-trace.log"))
                    Console.Error.WriteLine(await File.ReadAllTextAsync("/tmp/umd-challenge-trace.log"));
                exitCode = 1;
            }
            finally { desktop.Shutdown(exitCode); }
        });
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task VerifyAsync()
    {
        ApplicationDiagnosticLog.Initialize(Path.Combine(Path.GetTempPath(), "umd-smoke-log"), RuntimeProfile.Desktop);
        Environment.SetEnvironmentVariable("UMD_WEB_CHALLENGE_TRACE", "/tmp/umd-challenge-trace.log");
        // This path exercises the application's own plugin setup and .NET
        // P/Invoke resolution, in addition to the native-only playback check.
        await using (var player = new LibVlcPlatformMediaPlayer())
            Check(player.IsAvailable, "The application could not load bundled LibVLC");
        // The solver below verifies coexistence by actually using WebKit.
        // Let its adapter own the library lifetime instead of manually
        // loading and unloading GTK libraries before creating the browser.

        using var listener = new HttpListener();
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var root = new Uri($"http://127.0.0.1:{port}/");
        var videoRoot = new Uri($"http://umd-fixture.example.org:{port}/");
        listener.Prefixes.Add(root.AbsoluteUri);
        listener.Prefixes.Add(videoRoot.AbsoluteUri);
        listener.Start();
        var server = ServeAsync(listener);
        try
        {
            var solver = new DesktopWebChallengeSolver();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            var clearance = await solver.SolveAsync(new Uri(root, "clearance"), budget.Token);
            Check(clearance?.CookieHeader.Contains("cf_clearance=smoke-http-only", StringComparison.Ordinal) == true,
                "WebKit did not return the HttpOnly clearance cookie");
            Check(clearance!.UserAgent.Contains("AppleWebKit", StringComparison.Ordinal), "Clearance lost the browser user agent");
            Console.WriteLine("PASS: WebKitGTK HttpOnly clearance and browser user agent");

            var json = await solver.FetchDocumentAsync(new Uri(root, "json"), budget.Token);
            Check(json is { ContentType: "application/json", Body: "{\"titles\":[]}" }, "JSON endpoint was not recovered");
            Console.WriteLine("PASS: JSON response without HTML length requirement");

            var page = await solver.FetchDocumentAsync(new Uri(root, "delayed"), budget.Token);
            Check(page?.Body.Contains("ready-content", StringComparison.Ordinal) == true, "Challenge interstitial was returned as content");
            Console.WriteLine("PASS: waits until the challenge page becomes content");

            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await solver.FetchDocumentAsync(new Uri(root, "pending"), cancel.Token);
                throw new InvalidOperationException("Caller cancellation was swallowed");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            // The next request must acquire the solver gate and open a fresh
            // adapter; cancellation must not leave an active dialog behind.
            Check(await solver.FetchDocumentAsync(new Uri(root, "json"), budget.Token) is not null,
                "The solver did not recover after cancellation");
            Console.WriteLine("PASS: cancellation, cleanup and subsequent request");

            var image = await solver.FetchDocumentAsync(new Uri(root, "image"), budget.Token);
            Check(image is { IsBase64: true, ContentType: "image/png" }, "Image document was not recovered");
            Console.WriteLine("PASS: image document recovery");

            using var videoBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Check(await solver.FetchDocumentAsync(new Uri(videoRoot, "json"), videoBudget.Token) is not null,
                "WebKit cannot load the named fixture host");
            using (var localClient = new HttpClient())
                Check((await localClient.GetStringAsync(new Uri(videoRoot, "video"), videoBudget.Token)).Contains("<video", StringComparison.Ordinal),
                    "The video fixture host is not reachable");
            var capture = solver.ResolveVideoAsync(new ApiContentVideoSource("fixture", new Uri(videoRoot, "video"),
                kind: ApiContentVideoSourceKind.EmbeddedWeb), videoBudget.Token).AsTask();
            var desktop = (IClassicDesktopStyleApplicationLifetime)Current!.ApplicationLifetime!;
            while (!capture.IsCompleted)
            {
                await Task.Delay(250, videoBudget.Token);
                var button = desktop.Windows.Where(window => window.Title == "Video playback")
                    .SelectMany(window => window.GetVisualDescendants().OfType<Button>())
                    .FirstOrDefault(control => control.Content as string == "Use native player" && control.IsEnabled);
                if (button is not null)
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    break;
                }
            }
            var captured = await capture.WaitAsync(videoBudget.Token);
            Check(captured is { IsEmbeddedWeb: false } && captured.PlaybackUri.AbsolutePath == "/sample.mp4",
                "Visible video confirmation did not return the verified media");
            Check(!desktop.Windows.Any(window => window.Title == "Video playback"), "Video confirmation window was not closed");
            Console.WriteLine("PASS: playing video capture, explicit confirmation and native probe");
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { break; }
            var route = context.Request.Url!.AbsolutePath;
            Console.WriteLine($"Fixture request: {route}");
            var response = context.Response;
            if (route is "/sample.mp4" or "/image")
            {
                response.ContentType = route == "/image" ? "image/png" : "video/mp4";
                var media = route == "/image"
                    ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aMxkAAAAASUVORK5CYII=")
                    : await File.ReadAllBytesAsync("/sample.mp4");
                var start = 0;
                var end = media.Length - 1;
                response.Headers.Add("Accept-Ranges", "bytes");
                if (context.Request.Headers["Range"] is { } range && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var bounds = range[6..].Split('-');
                    start = int.Parse(bounds[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (bounds.Length > 1 && bounds[1].Length > 0)
                        end = Math.Min(end, int.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture));
                    if (start > end || start < 0)
                    {
                        response.StatusCode = 416;
                        response.Headers.Add("Content-Range", $"bytes */{media.Length}");
                        response.Close();
                        continue;
                    }
                    response.StatusCode = 206;
                    response.Headers.Add("Content-Range", $"bytes {start}-{end}/{media.Length}");
                }
                response.ContentLength64 = end - start + 1;
                try { await response.OutputStream.WriteAsync(media.AsMemory(start, end - start + 1)); }
                finally { response.Close(); }
                continue;
            }
            response.ContentType = route == "/json" ? "application/json" : "text/html";
            if (route == "/clearance") response.Headers.Add("Set-Cookie", "cf_clearance=smoke-http-only; Path=/; HttpOnly; SameSite=Lax");
            var body = route switch
            {
                "/json" => "{\"titles\":[]}",
                "/pending" => "<html><body>Just a moment</body></html>",
                "/delayed" => "<html><body>Just a moment<script>setTimeout(()=>{document.body.textContent='ready-content'.repeat(150)},1500)</script></body></html>",
                "/video" => "<html><body><video src='/sample.mp4' autoplay muted loop playsinline controls></video></body></html>",
                _ => "<html><body>" + new string('x', 1500) + "</body></html>"
            };
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            try { await response.OutputStream.WriteAsync(bytes); }
            finally { response.Close(); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
