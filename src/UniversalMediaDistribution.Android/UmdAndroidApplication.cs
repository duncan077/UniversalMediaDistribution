using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Runtime;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Android;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.Android.Web;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;

namespace UniversalMediaDistribution.Android;

[Application(
    Label = "@string/app_name",
    AllowBackup = false,
    UsesCleartextTraffic = false)]
public sealed class UmdAndroidApplication :
    AvaloniaAndroidApplication<App>
{
    private AndroidSystemPolicyMonitor? _systemPolicyMonitor;
    private WebViewPlatformMediaPlayer? _mediaPlayerHost;
    private long _gpuCacheBytes = 48L * 1024 * 1024;

    public UmdAndroidApplication(
        nint javaReference,
        JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    internal MobileApplicationHost? MobileHost { get; private set; }

    internal AndroidPlatformMediaPlayer? PlatformMediaPlayer { get; private set; }

    internal bool IsTelevision { get; private set; }

    public override void OnCreate()
    {
        MemoryDiagnostics.SnapshotRecorded += WriteMemorySnapshot;
        var applicationDataDirectory = FilesDir?.AbsolutePath ??
            throw new InvalidOperationException("Android did not provide an app-private files directory.");

        IsTelevision =
#if UMD_ANDROID_TV
            true;
#else
            UiModeManager.FromContext(this)?.CurrentModeType is UiMode.TypeTelevision ||
            PackageManager?.HasSystemFeature(PackageManager.FeatureLeanback) is true;
#endif
        PlatformMediaPlayer = new AndroidPlatformMediaPlayer(this);
        // Read once and shared: the embedded player needs it to size its
        // injected payload, and the host needs it to pick a ruleset. Before
        // this the player never saw it and silently took the desktop-sized
        // budget on the televisions.
        var deviceTier = ReadDeviceTier();
        ImageMemoryPolicy.ConfigureAndroid(IsTelevision, deviceTier == DeviceTier.Constrained);
        if (IsTelevision || deviceTier == DeviceTier.Constrained)
            _gpuCacheBytes = 24L * 1024 * 1024;
        _mediaPlayerHost = new WebViewPlatformMediaPlayer(
            PlatformMediaPlayer,
            new AndroidWebViewContentFilterHook(),
            deviceTier: deviceTier,
            remoteInput: IsTelevision ? new AndroidWebViewRemoteInput() : null);
        MobileHost = MobileApplicationHost.Start(
            applicationDataDirectory,
            IsTelevision
                ? RuntimeProfile.AndroidTv
                : RuntimeProfile.Android,
            contentFilterDeviceTier: deviceTier,
            platformMediaPlayer: _mediaPlayerHost,
#if UMD_BACKGROUND_WEB_VIDEO
            backgroundWebVideoPlayer: new AndroidBackgroundWebVideoPlayer(),
#endif
            // The system browser answers bot checks this application's own
            // requests are refused by, and hands back the page it received.
            webChallengeSolver: new AndroidWebChallengeSolver(),
            applicationUpdateServiceFactory: settingsStore =>
                new AndroidApplicationUpdateService(
                    this,
                    settingsStore,
                    ReadInstalledVersion(),
                    AndroidUpdateTarget.ResolveRuntimeIdentifier(
                        IsTelevision,
                        RuntimeInformation.ProcessArchitecture)));
        // The challenge solver needs a window to run a check in, and only the
        // activity in front has one.
        AndroidCurrentActivity.Track(this);
        _systemPolicyMonitor = new AndroidSystemPolicyMonitor(this, MobileHost);
        _systemPolicyMonitor.Start();

        base.OnCreate();
    }

    /// <summary>
    /// Decides how much filtering this hardware can carry.
    ///
    /// Being a television is not the signal on its own — a Google TV Streamer
    /// has four gigabytes and a Chromecast HD has one and a half, and plenty of
    /// handsets are worse than either. Android's own low-RAM flag and the
    /// per-application memory class are what actually describe the device.
    /// </summary>
    private DeviceTier ReadDeviceTier()
    {
        try
        {
            if (GetSystemService(ActivityService) is not ActivityManager manager)
            {
                return IsTelevision ? DeviceTier.Constrained : DeviceTier.Standard;
            }

            return manager.IsLowRamDevice || manager.MemoryClass <= 192
                ? DeviceTier.Constrained
                : DeviceTier.Standard;
        }
        catch (Java.Lang.Throwable)
        {
            // When the device will not say, assume the smaller budget.
            return DeviceTier.Constrained;
        }
    }

    public override void OnTrimMemory(TrimMemory level)
    {
        // UI_HIDDEN/BACKGROUND still arrive on Android 14+. Older releases
        // also report foreground pressure through this callback.
        ImageMemoryPolicy.ReleaseUnusedImages();
        MemoryDiagnostics.Snapshot($"android-trim-{level}");
        base.OnTrimMemory(level);
    }

    private static void WriteMemorySnapshot(string message) =>
        global::Android.Util.Log.Info("UMD.Memory", message);

    public override void OnTerminate()
    {
        MemoryDiagnostics.SnapshotRecorded -= WriteMemorySnapshot;
        _systemPolicyMonitor?.Stop();
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
                PlatformMediaPlayer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _mediaPlayerHost = null;
            PlatformMediaPlayer = null;
        }
        finally
        {
            if (MobileHost is not null)
            {
                MobileHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
                MobileHost = null;
            }
        }

        base.OnTerminate();
    }

    private string ReadInstalledVersion()
    {
        try
        {
            return PackageManager
                ?.GetPackageInfo(PackageName ?? string.Empty, 0)
                ?.VersionName ?? "0.0.0";
        }
        catch (Exception)
        {
            return "0.0.0";
        }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = _gpuCacheBytes })
            .WithInterFont();
}
