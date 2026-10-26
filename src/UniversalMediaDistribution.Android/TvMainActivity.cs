using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;
using Avalonia.Threading;
using UniversalMediaDistribution.UI;

namespace UniversalMediaDistribution.Android;

[Activity(
    Label = "@string/app_name",
    Banner = "@drawable/tv_banner",
    Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
    Exported = true,
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
[IntentFilter(
    [Intent.ActionMain],
    Categories = [Intent.CategoryLeanbackLauncher])]
public sealed class TvMainActivity : AvaloniaMainActivity
{
    public override bool DispatchKeyEvent(KeyEvent? eventArgs)
    {
        if (eventArgs?.KeyCode is Keycode.DpadCenter &&
            Avalonia.Application.Current is App
            {
                IsLiveTvRemoteSelectionActive: true
            } application)
        {
            if (eventArgs.Action is KeyEventActions.Down &&
                eventArgs.RepeatCount == 0)
            {
                // Finish dispatching the physical key before resolving a
                // channel. Some providers perform synchronous setup before
                // their first await; doing that inside DispatchKeyEvent can
                // make Android report an input ANR.
                Dispatcher.UIThread.Post(
                    () => application.TryActivateSelectedLiveTvChannel(),
                    DispatcherPriority.Input);
            }

            return true;
        }

        return base.DispatchKeyEvent(eventArgs);
    }

    public override async void OnBackPressed()
    {
        try
        {
            if (Avalonia.Application.Current is App application &&
                await application.NavigateBackAsync())
            {
                return;
            }
        }
        catch (Exception exception)
        {
            // Nothing observes an override's task. A back-navigation handler
            // that throws would reach the main looper and end the process, so
            // a failed in-app navigation falls through to leaving the activity.
            global::Android.Util.Log.Warn(
                "UMD",
                $"Back navigation failed: {exception.Message}");
        }

        Finish();
    }
}
