using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia.Android;
using UniversalMediaDistribution.UI;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.Android;

[Activity(
    Label = "@string/app_name",
    Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "magnet")]
public sealed class MainActivity : AvaloniaMainActivity
{
    public override async void OnBackPressed()
    {
        try
        {
            if (Avalonia.Application.Current is App application &&
                await application.NavigateBackAsync())
            {
                ApplicationDiagnosticLog.Information(
                    "playback.android-back",
                    "activity=main; handled=in-app");
                return;
            }
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "playback.android-back",
                $"activity=main; failure={exception.GetType().Name}; " +
                $"message={exception.Message}");
        }

        ApplicationDiagnosticLog.Information(
            "playback.android-back",
            "activity=main; handled=finish");
        Finish();
    }
}
