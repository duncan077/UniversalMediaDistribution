using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Desktop.Web;

internal static class Program
{
    // Uses the production HTTP gate, browser solver and bitmap decoder.
    // Does not initialize the app's catalog or change its settings.
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 1 ||
            !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https")
        {
            Console.Error.WriteLine("Usage (Windows): ChallengeArtworkProbe <https-cover-url>");
            return 2;
        }

        // The dotnet host's default profile can point into Program Files.
        // Keep the probe profile beside its output, away from user browser data.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
                Path.Combine(AppContext.BaseDirectory, "webview-profile"));
        AppBuilder.Configure<Avalonia.Application>().UsePlatformDetect().SetupWithoutStarting();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                WebChallengeGate.Configure(new DesktopWebChallengeSolver());
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Referrer = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/");
                using var response = await WebChallengeGate.SendImageAsync(client, request, stop.Token);
                response.EnsureSuccessStatusCode();
                await using var bytes = await response.Content.ReadAsStreamAsync(stop.Token);
                using var bitmap = Bitmap.DecodeToWidth(bytes, 300);
                if (bitmap.PixelSize.Width < 1 || bitmap.PixelSize.Height < 1)
                    throw new InvalidDataException("The cover decoded to an empty bitmap.");
                Console.WriteLine($"PASS: {response.Content.Headers.ContentType}; decoded {bitmap.PixelSize}");
                result = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                WebChallengeGate.Configure(null);
                stop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return result;
    }
}
