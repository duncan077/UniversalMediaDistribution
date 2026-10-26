using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>
/// Runs an authorized embedded-video page in the installed WebView2 runtime.
/// The ordinary WebView2 profile, cookie jar, user agent, JavaScript runtime,
/// and media stack are left unchanged.
/// </summary>
internal sealed class DesktopBackgroundWebVideoPlayer :
    IBackgroundWebVideoPlayer
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    public async ValueTask PlayAsync(
        Uri source,
        CancellationToken cancellationToken = default)
    {
        BackgroundWebVideoPlayback.ValidateSource(source);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Background WebView2 playback is available only on Windows.");
        }

        await SessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread
                .InvokeAsync(() => PlayOnUiThreadAsync(source, cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            SessionGate.Release();
        }
    }

    private static async Task PlayOnUiThreadAsync(
        Uri source,
        CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var webView = new NativeWebView
        {
            Background = Brushes.Black
        };
        var window = new Window
        {
            Content = webView,
            Width = 640,
            Height = 360,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        async void OnNavigationCompleted(
            object? sender,
            WebViewNavigationCompletedEventArgs eventArgs)
        {
            if (!eventArgs.IsSuccess || sender is not NativeWebView browser)
            {
                return;
            }

            try
            {
                _ = await browser.InvokeScript(
                    BackgroundWebVideoPlayback.StartScript);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                ApplicationDiagnosticLog.Warning(
                    "playback.background-web",
                    $"host={source.IdnHost}; stage=script-failed; " +
                    $"failure={exception.GetType().Name}; " +
                    $"message={exception.Message}");
            }
        }

        void OnWebMessageReceived(
            object? sender,
            WebMessageReceivedEventArgs eventArgs)
        {
            if (string.Equals(
                    eventArgs.Body,
                    BackgroundWebVideoPlayback.PlayingMessage,
                    StringComparison.Ordinal))
            {
                started.TrySetResult();
            }
        }

        static void OnNewWindowRequested(
            object? sender,
            WebViewNewWindowRequestedEventArgs eventArgs) =>
            eventArgs.Handled = true;

        webView.NavigationCompleted += OnNavigationCompleted;
        webView.WebMessageReceived += OnWebMessageReceived;
        webView.NewWindowRequested += OnNewWindowRequested;

        try
        {
            window.Show();
            window.Position = new PixelPoint(-32000, -32000);

            // NativeWebView must be attached before Source is assigned or the
            // platform adapter can be created without a usable visual host.
            await Task.Yield();
            webView.Source = source;

            await started.Task
                .WaitAsync(
                    BackgroundWebVideoPlayback.StartTimeout,
                    cancellationToken)
                .ConfigureAwait(true);
            ApplicationDiagnosticLog.Information(
                "playback.background-web",
                $"host={source.IdnHost}; stage=playing; " +
                $"durationSeconds={BackgroundWebVideoPlayback.PlaybackDuration.TotalSeconds:0}");

            await Task.Delay(
                    BackgroundWebVideoPlayback.PlaybackDuration,
                    cancellationToken)
                .ConfigureAwait(true);
            ApplicationDiagnosticLog.Information(
                "playback.background-web",
                $"host={source.IdnHost}; stage=completed");
        }
        finally
        {
            webView.NavigationCompleted -= OnNavigationCompleted;
            webView.WebMessageReceived -= OnWebMessageReceived;
            webView.NewWindowRequested -= OnNewWindowRequested;
            try
            {
                webView.Stop();
                webView.Source = new Uri("about:blank");
            }
            catch
            {
                // Closing the native window below is the final cleanup path.
            }

            window.Close();
        }
    }
}
