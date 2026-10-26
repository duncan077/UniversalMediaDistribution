using Android.Webkit;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Mobile;

namespace UniversalMediaDistribution.Android;

/// <summary>
/// What a browser came back with: the page it ended up on, the cookies it
/// collected getting there, and the identity it presented.
/// <para>
/// The user agent travels with the cookies because a clearance cookie is only
/// honoured for the browser that earned it — replaying one under this
/// application's own user agent is refused.
/// </para>
/// </summary>
internal sealed record WebChallengeLoad(
    WebChallengeDocument Document,
    string Cookies,
    string UserAgent);

/// <summary>
/// The end of a browser's attempt at a page.
/// </summary>
/// <param name="Load">
/// What it came back with, or <see langword="null"/> if it ran out of time.
/// </param>
/// <param name="Challenged">
/// Whether it was still looking at a bot check when time ran out. This is the
/// difference between a check that needs a person and one that is merely slow,
/// and so decides whether the user is interrupted.
/// </param>
internal sealed record WebChallengeOutcome(
    WebChallengeLoad? Load,
    bool Challenged);

/// <summary>
/// Drives a WebView until it is looking at a page rather than a bot check.
/// <para>
/// Shared by the silent attempt and the visible one so the two cannot disagree
/// about what counts as a solved check — only about who is watching.
/// </para>
/// </summary>
internal static class WebChallengePump
{
    internal const string LogTag = "UmdWebChallenge";

    internal const string ClearanceCookieName = "cf_clearance";

    /// <summary>
    /// Below this a page is still a stub or an error rather than content. It
    /// applies only to HTML: a JSON route legitimately answers in a few
    /// hundred bytes.
    /// </summary>
    private const int MinimumDocumentLength = 1000;

    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(750);

    private static readonly string ReportScript =
        $"JSON.stringify({WebChallengeDocument.ReportExpression});";

    /// <summary>
    /// Puts a WebView in the state a bot check expects of a browser.
    /// </summary>
    internal static void Configure(WebView webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        var settings = webView.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.DatabaseEnabled = true;

        // Without a client of its own a WebView hands redirects to whatever app
        // handles the URL, which for a check that ends in one means the answer
        // arrives in the user's browser instead of here.
        webView.SetWebViewClient(new WebViewClient());
        webView.OnResume();
        webView.ResumeTimers();

        var cookies = CookieManager.Instance;
        if (cookies is null)
        {
            return;
        }

        cookies.SetAcceptCookie(true);
        cookies.SetAcceptThirdPartyCookies(webView, true);
    }

    /// <summary>
    /// Loads the page and keeps asking what is on screen until it is content,
    /// or until the budget runs out.
    /// </summary>
    internal static async Task<WebChallengeOutcome> PumpAsync(
        WebView webView,
        Uri endpoint,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(endpoint);

        var cookies = CookieManager.Instance;
        var userAgent = webView.Settings.UserAgentString ?? string.Empty;
        webView.LoadUrl(endpoint.AbsoluteUri);

        var challenged = false;
        var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        while (Environment.TickCount64 < deadline &&
               !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken)
                .ConfigureAwait(true);
            if (await EvaluateAsync(webView).ConfigureAwait(true)
                is not { } document)
            {
                continue;
            }

            if (!document.IsBase64 && LooksLikeChallenge(document.Body))
            {
                challenged = true;
                continue;
            }

            var isPage = document.ContentType.Contains(
                "html",
                StringComparison.OrdinalIgnoreCase);
            if (document.Body.Length == 0 ||
                (isPage && document.Body.Length < MinimumDocumentLength))
            {
                continue;
            }

            // Written to disk here rather than at exit, so a check the user
            // solved survives even if this attempt is abandoned afterwards.
            var header = cookies?.GetCookie(endpoint.AbsoluteUri) ?? string.Empty;
            cookies?.Flush();
            global::Android.Util.Log.Info(
                LogTag,
                $"loaded length={document.Body.Length} " +
                $"type={document.ContentType} " +
                $"clearance={HasClearance(header)}");
            return new WebChallengeOutcome(
                new WebChallengeLoad(document, header, userAgent),
                Challenged: false);
        }

        return new WebChallengeOutcome(Load: null, challenged);
    }

    internal static bool HasClearance(string cookies) =>
        cookies.Contains(ClearanceCookieName, StringComparison.Ordinal);

    /// <summary>
    /// Whether the document is still the bot check rather than the page.
    /// </summary>
    private static bool LooksLikeChallenge(string body) =>
        body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("cf_chl", StringComparison.OrdinalIgnoreCase);

    private static Task<WebChallengeDocument?> EvaluateAsync(WebView webView)
    {
        var completion = new TaskCompletionSource<WebChallengeDocument?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        webView.EvaluateJavascript(ReportScript, new ScriptCallback(completion));
        return completion.Task;
    }

    /// <summary>
    /// Receives the script result, which arrives JSON-encoded and therefore
    /// needs unwrapping before the envelope inside it can be read.
    /// </summary>
    private sealed class ScriptCallback(
        TaskCompletionSource<WebChallengeDocument?> completion)
        : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            try
            {
                completion.TrySetResult(WebChallengeScriptResult.Parse(value?.ToString()));
            }
            catch (Exception)
            {
                completion.TrySetResult(null);
            }
        }
    }
}
