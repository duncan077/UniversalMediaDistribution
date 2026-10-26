using Android.App;
using Android.OS;
using Avalonia.Threading;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.Android;

/// <summary>
/// Remembers which activities are alive, and which of them is in front.
/// <para>
/// Android hands an <see cref="Activity"/> to whatever it starts, but code that
/// is not started by Android — a background fetch, say — has no way to ask for
/// one. Registering for the lifecycle callbacks is the platform's own answer to
/// that, and the only one that does not leak: an activity is forgotten as soon
/// as it is destroyed.
/// </para>
/// <para>
/// More than one is kept because the one in front is not always usable. Closing
/// an activity runs the next one's resume <em>after</em> the closing one's
/// pause, so for a moment the only activity the platform has named is on its
/// way out. Anything asking during that moment would otherwise be told there is
/// no window at all, when in fact the one underneath was never gone.
/// </para>
/// </summary>
internal sealed class AndroidCurrentActivity :
    Java.Lang.Object,
    global::Android.App.Application.IActivityLifecycleCallbacks
{
    private static readonly List<Activity> Live = [];
    private readonly HashSet<Activity> _visibleCatalogActivities = [];

    /// <summary>
    /// An activity with a usable window, preferring the one in front. Null only
    /// when the application genuinely has none — it is in the background.
    /// </summary>
    internal static Activity? Current
    {
        get
        {
            lock (Live)
            {
                for (var index = Live.Count - 1; index >= 0; index--)
                {
                    if (Live[index] is
                        { IsFinishing: false, IsDestroyed: false } activity)
                    {
                        return activity;
                    }
                }
            }

            return null;
        }
    }

    internal static void Track(global::Android.App.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.RegisterActivityLifecycleCallbacks(
            new AndroidCurrentActivity());
    }

    /// <summary>
    /// Moves an activity to the front of the list, so the most recently seen is
    /// the first one considered.
    /// </summary>
    private static void Promote(Activity activity)
    {
        lock (Live)
        {
            Live.Remove(activity);
            Live.Add(activity);
        }
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) =>
        Promote(activity);

    public void OnActivityStarted(Activity activity)
    {
        Promote(activity);
        if (activity is Avalonia.Android.AvaloniaMainActivity)
        {
            _visibleCatalogActivities.Add(activity);
            PublishArtworkActivity();
        }
    }

    public void OnActivityResumed(Activity activity) => Promote(activity);

    public void OnActivityDestroyed(Activity activity)
    {
        if (_visibleCatalogActivities.Remove(activity)) PublishArtworkActivity();
        lock (Live)
        {
            Live.Remove(activity);
        }
    }

    public void OnActivityPaused(Activity activity)
    {
    }

    public void OnActivitySaveInstanceState(Activity activity, Bundle outState)
    {
    }

    public void OnActivityStopped(Activity activity)
    {
        if (_visibleCatalogActivities.Remove(activity)) PublishArtworkActivity();
    }

    private void PublishArtworkActivity()
    {
        var active = _visibleCatalogActivities.Count > 0;
        // Native playback covers the Avalonia activity without detaching its
        // visual tree. Drop its posters before retaining a video decoder too.
        Dispatcher.UIThread.Post(() =>
        {
            ImageMemoryPolicy.SetArtworkActive(active);
            MemoryDiagnostics.Snapshot(active ? "catalog-visible" : "catalog-hidden");
        });
    }
}
