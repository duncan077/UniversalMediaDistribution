using Avalonia;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Metadata;
using UniversalMediaDistribution.Playback.LibVLC;
using UniversalMediaDistribution.Reading.Pdfium;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.Torrenting.MonoTorrent;
using UniversalMediaDistribution.Desktop.Web;
using UniversalMediaDistribution.UI;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.Updates;

namespace UniversalMediaDistribution.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var configuredDataDirectory = Environment.GetEnvironmentVariable(
            "UMD_APPLICATION_DATA_DIRECTORY");
        var applicationDataDirectory =
            string.IsNullOrWhiteSpace(configuredDataDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData,
                        Environment.SpecialFolderOption.Create),
                    "UniversalMediaDistribution")
                : Path.GetFullPath(configuredDataDirectory);
        var runtimeProfile =
            RuntimeProfile.Desktop.WithEnvironmentShellOverride();
        ApplicationDiagnosticLog.Initialize(
            applicationDataDirectory,
            runtimeProfile);
        var database = new SqliteCatalogDatabase(
            Path.Combine(applicationDataDirectory, "catalog.db"));
        var catalogRepository = new SqliteCatalogRepository(database);
        var databaseBackupService = new SqliteDatabaseBackupService(database);
        var magnetPreferences = new SqliteMagnetOpenPreferencesStore(database);
        var userMediaStateStore = new SqliteUserMediaStateStore(database);
        var metadataProviderSettingsStore =
            new SqliteMetadataProviderSettingsStore(database);
        var torrentDownloadStore =
            new SqliteTorrentDownloadStore(database);
        var torrentDownloadPreferencesStore =
            new SqliteTorrentDownloadPreferencesStore(database);
        var initialMagnetLinks = MagnetStartupRequestParser.Extract(args);
        var torrentOptions = MonoTorrentEngineOptions.Parse(
            Environment.GetEnvironmentVariable("UMD_MSE_POLICY"),
            Environment.GetEnvironmentVariable("UMD_FAST_RESUME_POLICY"));
        var torrentEngine = new MonoTorrentEngine(
            Path.Combine(applicationDataDirectory, "torrent-data"),
            torrentOptions);
        var mediaPlayer = new WebViewPlatformMediaPlayer(
            new LibVlcPlatformMediaPlayer(),
            OperatingSystem.IsWindows()
                ? new WebView2ContentFilterHook()
                : null,
            remoteInput: OperatingSystem.IsWindows() && runtimeProfile.IsTelevision
                ? new WebView2RemoteInput()
                : null,
            audioRelay: OperatingSystem.IsWindows()
                ? new WebView2AudioRelay()
                : null);
        var pdfRenderer = new PdfiumPdfRenderer();
        var desktopUpdateOptions = DesktopUpdateOptions.FromEnvironment(
            applicationDataDirectory);
        var remoteServerSettingsStore =
            new SqliteRemoteServerSettingsStore(database);
        IApplicationUpdateService updateService =
            desktopUpdateOptions is null
                ? UnavailableApplicationUpdateService.Instance
                : new DesktopApplicationUpdateService(
                    desktopUpdateOptions,
                    remoteServerSettingsStore);
        var exitCode = 0;
        var cleanupCompleted = false;

        try
        {
            ApplicationComposition.Configure(
                runtimeProfile,
                catalogRepository,
                databaseBackupService,
                torrentEngine,
                magnetPreferences,
                userMediaStateStore,
                initialMagnetLinks,
                mediaPlayer,
                pdfRenderer,
                metadataProviderSettingsStore,
                MetadataProviderFactory.CreateDefault(),
                new EnvironmentTrackerCredentialProvider(),
                // The system browser answers bot checks this application's
                // own requests are refused by, and hands back the page it
                // received. Inert until a host actually issues a challenge.
                webChallengeSolver: new DesktopWebChallengeSolver(),
#if UMD_BACKGROUND_WEB_VIDEO
                backgroundWebVideoPlayer:
                    new DesktopBackgroundWebVideoPlayer(),
#endif
                contentSourceProviderSettingsStore:
                     new SqliteContentSourceProviderSettingsStore(database),
                 contentSourceDailyUpdateStore:
                     new SqliteContentSourceDailyUpdateStore(database),
                 torrentDownloadStore: torrentDownloadStore,
                 torrentDownloadPreferencesStore:
                     torrentDownloadPreferencesStore,
                 applicationUpdateService: updateService,
                 remoteServerSettingsStore: remoteServerSettingsStore,
                 userSyncStore:
                     new SqliteUserSyncStore(
                         database,
                         catalogRepository),
                 remoteSessionStore:
                     new SqliteRemoteSessionStore(database),
                 apiTitleBindingStore:
                     new SqliteApiTitleBindingStore(database),
                 contentFilterSettingsStore:
                     new SqliteContentFilterSettingsStore(database),
                 contentFilterDirectory:
                     Path.Combine(
                         applicationDataDirectory,
                         "content-filters"),
                 userProfileStore:
                     new SqliteUserProfileStore(database),
                 activeProfile: database);

            exitCode = BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            var applicationShutdownTask =
                (Avalonia.Application.Current as App)?.ShutdownTask;
            SynchronizationContext.SetSynchronizationContext(null);
            cleanupCompleted = DesktopShutdownCoordinator.DisposeAsync(
                    applicationShutdownTask,
                    mediaPlayer,
                    torrentEngine,
                    TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
        }

        ApplicationDiagnosticLog.EndSession(
            $"exitCode={exitCode}; cleanupCompleted={cleanupCompleted}");
        if (!cleanupCompleted)
        {
            Environment.Exit(exitCode);
        }

        return exitCode;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
