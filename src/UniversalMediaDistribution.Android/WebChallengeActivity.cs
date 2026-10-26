using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.Android;

/// <summary>
/// Shows the bot check to the user so they can answer it.
/// <para>
/// Some checks resolve on their own and the silent attempt handles those. The
/// rest ask for a deliberate act — a box ticked — and no amount of driving a
/// hidden browser will produce one. Answering it is the user's to do, so this
/// puts the check in front of them, waits, and closes itself the moment the
/// page behind it appears.
/// </para>
/// </summary>
[Activity(
    Label = "@string/web_challenge_title",
    Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
    Exported = false,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
internal sealed class WebChallengeActivity : Activity
{
    private const string EndpointExtra = "umd.web-challenge.endpoint";

    /// <summary>
    /// Long enough for someone to notice the screen, read a line, and tick a
    /// box — not long enough to be a trap if they walk away.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The attempt currently on screen. One at a time, which the solver's own
    /// gate already guarantees.
    /// </summary>
    private static TaskCompletionSource<WebChallengeLoad?>? _pending;

    private readonly CancellationTokenSource _closing = new();

    private WebView? _webView;

    /// <summary>
    /// Whether the check was actually answered, as opposed to the screen being
    /// closed on the way past it.
    /// </summary>
    private bool _answered;

    /// <summary>
    /// Puts the check on screen and waits for the user to answer it.
    /// </summary>
    /// <returns>
    /// What the browser ended up on, or <see langword="null"/> if the user
    /// backed out or ran out of time.
    /// </returns>
    internal static Task<WebChallengeLoad?> SolveAsync(
        Activity host,
        Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(endpoint);

        // A previous attempt that never completed must not strand its caller.
        _pending?.TrySetResult(null);
        var completion = new TaskCompletionSource<WebChallengeLoad?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;

        var intent = new Intent(host, typeof(WebChallengeActivity));
        intent.PutExtra(EndpointExtra, endpoint.AbsoluteUri);
        host.StartActivity(intent);
        return completion.Task;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var address = Intent?.GetStringExtra(EndpointExtra);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint))
        {
            Complete(null);
            Finish();
            return;
        }

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.LayoutParameters = new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);

        // Recent Android draws every window edge to edge, so without this the
        // first line of the explanation sits underneath the clock.
        layout.SetFitsSystemWindows(true);

        var padding = (int)(16 * Resources!.DisplayMetrics!.Density);
        var explanation = new TextView(this)
        {
            Text = GetString(
                Resource.String.web_challenge_message,
                endpoint.IdnHost)
        };
        explanation.SetPadding(padding, padding, padding, padding);
        layout.AddView(explanation);

        _webView = new WebView(this);
        _webView.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1f);
        layout.AddView(_webView);
        SetContentView(layout);

        WebChallengePump.Configure(_webView);
        _ = RunAsync(_webView, endpoint);
    }

    private async Task RunAsync(WebView webView, Uri endpoint)
    {
        global::Android.Util.Log.Info(
            WebChallengePump.LogTag,
            $"asking the user to answer the check for {endpoint.IdnHost}");
        ApplicationDiagnosticLog.Information(
            "web.challenge",
            $"host={endpoint.IdnHost}; stage=asking-user");

        WebChallengeOutcome outcome;
        try
        {
            outcome = await WebChallengePump
                .PumpAsync(webView, endpoint, Budget, _closing.Token)
                .ConfigureAwait(true);
        }
        catch (System.OperationCanceledException)
        {
            // The user closed the screen; OnDestroy answers the caller.
            return;
        }

        if (outcome.Load is null)
        {
            ApplicationDiagnosticLog.Warning(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=user-timed-out");
        }

        _answered = outcome.Load is not null;
        Complete(outcome.Load);
        Finish();
    }

    protected override void OnDestroy()
    {
        _closing.Cancel();

        // Closing without an answer is a decision, and the solver remembers it
        // so the user is not asked again on the very next request. Reported
        // here rather than from a back-press override because the platform
        // retired that one, and every route out of this screen lands here.
        if (!_answered)
        {
            ApplicationDiagnosticLog.Information(
                "web.challenge",
                "stage=declined-by-user");
        }

        // Whatever the reason for closing, the request waiting on this gets an
        // answer rather than hanging until its own timeout.
        Complete(null);
        _closing.Dispose();

        if (_webView is not null)
        {
            _webView.StopLoading();
            (_webView.Parent as ViewGroup)?.RemoveView(_webView);
            _webView.Destroy();
            _webView = null;
        }

        base.OnDestroy();
    }

    private static void Complete(WebChallengeLoad? load) =>
        _pending?.TrySetResult(load);
}
