using Android.Webkit;
using Avalonia.Threading;
using Java.Interop;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Android;

/// <summary>
/// Runs an authorized embed in Android's installed system WebView. The view is
/// attached as one pixel because Android suspends rendering and media for a
/// detached WebView; no user agent or browser fingerprint is replaced.
/// </summary>
internal sealed class AndroidBackgroundWebVideoPlayer :
    IBackgroundWebVideoPlayer
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    public async ValueTask PlayAsync(
        Uri source,
        CancellationToken cancellationToken = default)
    {
        BackgroundWebVideoPlayback.ValidateSource(source);
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

    [System.Diagnostics.CodeAnalysis.DynamicDependency(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(PlaybackMessageBridge))]
    private static async Task PlayOnUiThreadAsync(
        Uri source,
        CancellationToken cancellationToken)
    {
        if (AndroidCurrentActivity.Current is not { } activity ||
            activity.FindViewById<global::Android.Views.ViewGroup>(
                global::Android.Resource.Id.Content) is not { } root)
        {
            throw new InvalidOperationException(
                "Background video playback needs the Android app to be in the foreground.");
        }

        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var bridge = new PlaybackMessageBridge(started);
        using var client = new PlaybackWebViewClient();
        using var chrome = new WebChromeClient();
        var webView = new WebView(activity);
        client.PageReady += OnPageReady;

        void OnPageReady(WebView browser)
        {
            browser.EvaluateJavascript(
                BackgroundWebVideoPlayback.StartScript,
                null);
        }

        try
        {
            var settings = webView.Settings;
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;
            settings.DatabaseEnabled = true;
            webView.SetWebViewClient(client);
            webView.SetWebChromeClient(chrome);
            webView.AddJavascriptInterface(bridge, "umdBackgroundVideo");
            webView.OnResume();
            webView.ResumeTimers();

            var cookies = CookieManager.Instance;
            cookies?.SetAcceptCookie(true);
            cookies?.SetAcceptThirdPartyCookies(webView, true);

            root.AddView(
                webView,
                new global::Android.Views.ViewGroup.LayoutParams(1, 1));
            webView.LoadUrl(source.AbsoluteUri);

            await started.Task
                .WaitAsync(
                    BackgroundWebVideoPlayback.StartTimeout,
                    cancellationToken)
                .ConfigureAwait(true);
            ApplicationDiagnosticLog.Information(
                "playback.background-web",
                $"host={source.IdnHost}; platform=android; stage=playing; " +
                $"durationSeconds={BackgroundWebVideoPlayback.PlaybackDuration.TotalSeconds:0}");

            await Task.Delay(
                    BackgroundWebVideoPlayback.PlaybackDuration,
                    cancellationToken)
                .ConfigureAwait(true);
            ApplicationDiagnosticLog.Information(
                "playback.background-web",
                $"host={source.IdnHost}; platform=android; stage=completed");
        }
        finally
        {
            client.PageReady -= OnPageReady;
            webView.StopLoading();
            webView.LoadUrl("about:blank");
            webView.RemoveJavascriptInterface("umdBackgroundVideo");
            (webView.Parent as global::Android.Views.ViewGroup)
                ?.RemoveView(webView);
            webView.Destroy();
        }
    }

    private sealed class PlaybackWebViewClient : WebViewClient
    {
        public event Action<WebView>? PageReady;

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (view is not null)
            {
                PageReady?.Invoke(view);
            }
        }

        public override bool ShouldOverrideUrlLoading(
            WebView? view,
            IWebResourceRequest? request) => false;
    }

    private sealed class PlaybackMessageBridge(
        TaskCompletionSource started) : Java.Lang.Object
    {
        [JavascriptInterface]
        [Export("postMessage")]
        public void PostMessage(string? message)
        {
            if (string.Equals(
                    message,
                    BackgroundWebVideoPlayback.PlayingMessage,
                    StringComparison.Ordinal))
            {
                started.TrySetResult();
            }
        }
    }
}
