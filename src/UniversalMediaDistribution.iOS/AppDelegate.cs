using Avalonia;
using Avalonia.iOS;
using Foundation;
using UIKit;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
    private MobileApplicationHost? _mobileHost;
    private IosSystemPolicyMonitor? _systemPolicyMonitor;
    private IosPlatformMediaPlayer? _platformMediaPlayer;
    private WebViewPlatformMediaPlayer? _mediaPlayerHost;
    private NSObject? _terminationObserver;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        _platformMediaPlayer ??= new IosPlatformMediaPlayer();
        _mediaPlayerHost ??= new WebViewPlatformMediaPlayer(
            _platformMediaPlayer);
        _mobileHost ??= MobileApplicationHost.Start(
            GetApplicationDataDirectory(),
            RuntimeProfile.Ios,
            platformMediaPlayer: _mediaPlayerHost);
        _systemPolicyMonitor ??= new IosSystemPolicyMonitor(_mobileHost);
        _systemPolicyMonitor.Start();
        _terminationObserver ??= NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.WillTerminateNotification,
            _ => DisposeHosts());

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    private void DisposeHosts()
    {
        _terminationObserver?.Dispose();
        _terminationObserver = null;
        _systemPolicyMonitor?.Dispose();
        _systemPolicyMonitor = null;

        try
        {
            if (_mediaPlayerHost is not null)
            {
                _mediaPlayerHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                _platformMediaPlayer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _mediaPlayerHost = null;
            _platformMediaPlayer = null;
        }
        finally
        {
            if (_mobileHost is not null)
            {
                _mobileHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _mobileHost = null;
            }
        }
    }

    private static string GetApplicationDataDirectory()
    {
        var directory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "iOS did not provide an app-private application-support directory.");
        }

        return directory;
    }
}
