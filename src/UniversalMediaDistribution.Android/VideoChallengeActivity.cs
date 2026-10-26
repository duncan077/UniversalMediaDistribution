using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.WebKit;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Android;

[Activity(Label = "Video check", Theme = "@style/Theme.AppCompat.DayNight.NoActionBar", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
internal sealed class VideoChallengeActivity : Activity
{
    private sealed record Pending(ApiContentVideoSource Source)
    {
        internal TaskCompletionSource<ApiContentVideoSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal VideoChallengeActivity? Activity;
    }
    private static Pending? _pending;
    private Pending? _request;
    private WebView? _browser;
    private Button? _native;
    private TextView? _status;
    private ApiContentVideoSource? _candidate;
    private bool _probing;
    private readonly CancellationTokenSource _closing = new();

    internal static async Task<ApiContentVideoSource?> ShowAsync(Activity activity, ApiContentVideoSource source, CancellationToken token)
    {
        var request = new Pending(source);
        if (Interlocked.CompareExchange(ref _pending, request, null) is not null) return null;
        try
        {
            activity.StartActivity(new Intent(activity, typeof(VideoChallengeActivity)));
            return await request.Completion.Task.WaitAsync(TimeSpan.FromMinutes(2), token);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            request.Activity?.RunOnUiThread(() => request.Activity?.Finish());
            Interlocked.CompareExchange(ref _pending, null, request);
        }
    }

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        _request = Volatile.Read(ref _pending);
        if (_request is null) { Finish(); return; }
        _request.Activity = this;
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetFitsSystemWindows(true);
        _status = new TextView(this) { Text = VideoChallengeCapture.Instructions };
        _status.SetPadding(16, 16, 16, 16);
        _browser = new WebView(this);
        _native = new Button(this) { Text = "Use native player", Enabled = false, Focusable = true };
        var cancel = new Button(this) { Text = "Keep web player", Focusable = true };
        layout.AddView(_status);
        layout.AddView(_browser, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        layout.AddView(_native);
        layout.AddView(cancel);
        cancel.Click += (_, _) => Finish();
        _native.Click += async (_, _) => await ConfirmAsync();
        SetContentView(layout);
        WebChallengePump.Configure(_browser);
        _browser.Settings.JavaScriptCanOpenWindowsAutomatically = false;
        _browser.Settings.SetSupportMultipleWindows(true);
        _browser.SetWebChromeClient(new WebChromeClient());
        _browser.SetWebViewClient(new PlayerClient(_request.Source.PlaybackUri));
        if (WebViewFeature.IsFeatureSupported(WebViewFeature.DocumentStartScript))
            WebViewCompat.AddDocumentStartJavaScript(_browser, VideoChallengeCapture.Script, new List<string> { "*" });
        _browser.LoadUrl(_request.Source.PlaybackUri.AbsoluteUri);
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_closing.IsCancellationRequested && _browser is { } browser && _request is { } request)
            {
                await Task.Delay(750, _closing.Token);
                var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                browser.EvaluateJavascript("window.__umdVideoChallengeResult || ''", new Callback(completion));
                string? encoded;
                try { encoded = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), _closing.Token); }
                catch (TimeoutException) { continue; }
                if (encoded is null || encoded.Length > 32768) continue;
                try
                {
                    using var json = JsonDocument.Parse(encoded);
                    if (json.RootElement.ValueKind == JsonValueKind.String &&
                        VideoChallengeCapture.Parse(json.RootElement.GetString(), request.Source) is { } candidate && !_probing)
                    {
                        _candidate = candidate;
                        _native!.Enabled = true;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (System.OperationCanceledException) { }
        catch (Exception) { if (_status is not null) _status.Text = "Native capture is unavailable. You can keep the web player."; }
    }

    private async Task ConfirmAsync()
    {
        if (_candidate is not { } candidate || _probing) return;
        _probing = true;
        _native!.Enabled = false;
        _status!.Text = "Checking whether the video can play outside the browser…";
        try
        {
            if (await VideoChallengeMediaProbe.VerifyAsync(candidate, _closing.Token))
            {
                _request?.Completion.TrySetResult(candidate);
                Finish();
            }
            else _status.Text = "This video still needs its browser session. Choose Keep web player.";
        }
        catch (System.OperationCanceledException) { }
        catch (Exception) { _status.Text = "Native verification failed. You can keep the web player."; }
        finally { _probing = false; }
    }

    protected override void OnDestroy()
    {
        _closing.Cancel();
        _request?.Completion.TrySetResult(null);
        if (_browser is { } browser)
        {
            browser.StopLoading();
            (browser.Parent as ViewGroup)?.RemoveView(browser);
            browser.Destroy();
            _browser = null;
        }
        _closing.Dispose();
        base.OnDestroy();
    }

    private sealed class Callback(TaskCompletionSource<string?> completion) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => completion.TrySetResult(value?.ToString());
    }

    private sealed class PlayerClient(Uri original) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url?.ToString() is not { } value || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return true;
            return request.IsForMainFrame && !VideoChallengeCapture.AllowsNavigation(original, uri);
        }
        public override void OnPageStarted(WebView? view, string? url, global::Android.Graphics.Bitmap? favicon)
        {
            base.OnPageStarted(view, url, favicon);
            view?.EvaluateJavascript(VideoChallengeCapture.Script, null);
        }
        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            view?.EvaluateJavascript(VideoChallengeCapture.Script, null);
        }
    }
}
