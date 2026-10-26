using System.Collections.Concurrent;
using Android.Webkit;
using Avalonia.Threading;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.Android;

/// <summary>
/// Answers a host's bot check with the system browser engine, then hands back
/// either the clearance it earned or the page it received.
/// <para>
/// It tries quietly first. A check that resolves on its own does so in a few
/// seconds and the user never learns it happened. One that asks for a
/// deliberate act cannot be answered that way at all, and so is handed to the
/// person sitting in front of the application rather than waited out.
/// </para>
/// </summary>
public sealed partial class AndroidWebChallengeSolver : IWebChallengeSolver
{
    /// <summary>
    /// How long the hidden attempt is given before the check is judged to need
    /// a person.
    /// <para>
    /// Checks that resolve themselves do it well inside this. Waiting longer
    /// only delays the moment the user could have answered it.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SilentBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a host is left alone after the user declines its check, so that
    /// declining means something rather than deferring by one request.
    /// </summary>
    private static readonly TimeSpan DeclineCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// One browser at a time, so several challenged hosts in one refresh do not
    /// each build their own — and, more importantly, do not each take over the
    /// screen.
    /// </summary>
    private static readonly SemaphoreSlim LoadGate = new(1, 1);

    private static readonly ConcurrentDictionary<string, long> Declined =
        new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<WebChallengeClearance?> SolveAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var result = await LoadAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);
        if (result is null ||
            !WebChallengePump.HasClearance(result.Cookies))
        {
            return null;
        }

        return new WebChallengeClearance(
            endpoint.IdnHost,
            result.Cookies,
            result.UserAgent);
    }

    public async ValueTask<WebChallengeDocument?> FetchDocumentAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var result = await LoadAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);
        return result?.Document;
    }

    private static async Task<WebChallengeLoad?> LoadAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        await LoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread
                .InvokeAsync(() => LoadOnUiThreadAsync(endpoint, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=load-failed; " +
                $"failure={exception.GetType().Name}; " +
                $"message={exception.Message}");
            return null;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    private static async Task<WebChallengeLoad?> LoadOnUiThreadAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        var outcome = await SilentAttemptAsync(endpoint, cancellationToken)
            .ConfigureAwait(true);
        if (outcome.Load is not null)
        {
            ApplicationDiagnosticLog.Information(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=document; " +
                $"length={outcome.Load.Document.Body.Length}; " +
                $"type={outcome.Load.Document.ContentType}");
            return outcome.Load;
        }

        if (!outcome.Challenged)
        {
            // Out of time without ever seeing a check: a slow or unreachable
            // host, which showing to the user would not help.
            global::Android.Util.Log.Warn(
                WebChallengePump.LogTag,
                $"timed out for {endpoint.IdnHost}");
            ApplicationDiagnosticLog.Warning(
                "web.challenge",
                $"host={endpoint.IdnHost}; stage=document-timed-out");
            return null;
        }

        return await AskUserAsync(endpoint).ConfigureAwait(true);
    }

    /// <summary>
    /// The hidden attempt: a browser the user never sees, answering the checks
    /// that need no one.
    /// </summary>
    private static async Task<WebChallengeOutcome> SilentAttemptAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        if (global::Android.App.Application.Context is not { } context)
        {
            return new WebChallengeOutcome(Load: null, Challenged: false);
        }

        var webView = new WebView(context);
        Attach(webView);
        try
        {
            WebChallengePump.Configure(webView);
            global::Android.Util.Log.Info(
                WebChallengePump.LogTag,
                $"loading {endpoint.AbsoluteUri}");
            return await WebChallengePump
                .PumpAsync(webView, endpoint, SilentBudget, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            webView.StopLoading();
            (webView.Parent as global::Android.Views.ViewGroup)?.RemoveView(webView);
            webView.Destroy();
        }
    }

    /// <summary>
    /// Hands a check that needs a deliberate answer to the user.
    /// </summary>
    private static async Task<WebChallengeLoad?> AskUserAsync(Uri endpoint)
    {
        var host = endpoint.IdnHost;
        if (IsDeclined(host))
        {
            global::Android.Util.Log.Info(
                WebChallengePump.LogTag,
                $"{host} check was declined recently; not asking again");
            return null;
        }

        if (AndroidCurrentActivity.Current is not { } activity)
        {
            // Nothing is on screen to interrupt. A background refresh is not
            // worth waking someone for, and the next one they ask for will.
            global::Android.Util.Log.Info(
                WebChallengePump.LogTag,
                $"{host} needs a person, but the app is in the background");
            return null;
        }

        var load = await WebChallengeActivity
            .SolveAsync(activity, endpoint)
            .ConfigureAwait(true);
        if (load is null)
        {
            Declined[host] = Environment.TickCount64;
            return null;
        }

        Declined.TryRemove(host, out _);
        ApplicationDiagnosticLog.Information(
            "web.challenge",
            $"host={host}; stage=answered-by-user; " +
            $"length={load.Document.Body.Length}; " +
            $"clearance={WebChallengePump.HasClearance(load.Cookies)}");
        return load;
    }

    private static bool IsDeclined(string host)
    {
        if (!Declined.TryGetValue(host, out var at))
        {
            return false;
        }

        if (Environment.TickCount64 - at <
            (long)DeclineCooldown.TotalMilliseconds)
        {
            return true;
        }

        Declined.TryRemove(host, out _);
        return false;
    }

    /// <summary>
    /// Puts the hidden browser in the window as a single pixel.
    /// <para>
    /// A detached WebView is a hidden one, and the platform throttles a hidden
    /// page's timers and never gives it a frame — so a check that waits on
    /// animation callbacks, as these do, sits at the interstitial forever.
    /// Being in the window is what makes it run. One pixel in the corner is the
    /// smallest thing that still counts as being in the window.
    /// </para>
    /// </summary>
    private static void Attach(WebView webView)
    {
        if (AndroidCurrentActivity.Current is not { } activity ||
            activity.FindViewById<global::Android.Views.ViewGroup>(
                global::Android.Resource.Id.Content) is not { } root)
        {
            // In the background there is no window to join. The load is still
            // worth attempting: a host that answers without a check needs no
            // frames to do it.
            global::Android.Util.Log.Warn(
                WebChallengePump.LogTag,
                "no activity in front; running detached");
            return;
        }

        root.AddView(
            webView,
            new global::Android.Views.ViewGroup.LayoutParams(1, 1));
    }
}
