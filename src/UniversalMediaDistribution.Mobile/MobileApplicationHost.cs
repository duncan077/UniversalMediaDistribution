using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Metadata;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.Torrenting.MonoTorrent;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.Updates;

namespace UniversalMediaDistribution.Mobile;

public sealed class MobileApplicationHost :
    IApplicationLifecycleHandler,
    IAsyncDisposable
{
    private readonly MonoTorrentEngine _torrentEngine;
    private readonly MobileTorrentLifecycleCoordinator _lifecycleCoordinator;
    private volatile bool _isNetworkAvailable = true;
    private volatile bool _isNetworkMetered;
    private volatile bool _isPowerSaving;
    private bool _disposed;

    private MobileApplicationHost(
        string applicationDataDirectory,
        RuntimeProfile runtimeProfile,
        IReadOnlyList<string> initialMagnetLinks,
        MobileExecutionPolicy? executionPolicy,
        IPlatformMediaPlayer? platformMediaPlayer,
        IBackgroundWebVideoPlayer? backgroundWebVideoPlayer,
        Func<IRemoteServerSettingsStore, IApplicationUpdateService>?
            applicationUpdateServiceFactory,
        DeviceTier contentFilterDeviceTier,
        IWebChallengeSolver? webChallengeSolver)
    {
        ApplicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        Directory.CreateDirectory(ApplicationDataDirectory);
        ApplicationDiagnosticLog.Initialize(
            ApplicationDataDirectory,
            runtimeProfile);

        var database = new SqliteCatalogDatabase(
            Path.Combine(ApplicationDataDirectory, "catalog.db"));
        var catalogRepository = new SqliteCatalogRepository(database);
        var magnetPreferences = new SqliteMagnetOpenPreferencesStore(database);
        var metadataProviderSettings = new SqliteMetadataProviderSettingsStore(database);
        var userMediaState = new SqliteUserMediaStateStore(database);
        var torrentDownloadStore = new SqliteTorrentDownloadStore(database);
        var torrentDownloadPreferences =
            new SqliteTorrentDownloadPreferencesStore(database);
        var remoteServerSettingsStore =
            new SqliteRemoteServerSettingsStore(database);
        var torrentOptions = MonoTorrentEngineOptions.Parse(
            Environment.GetEnvironmentVariable("UMD_MSE_POLICY"),
            Environment.GetEnvironmentVariable("UMD_FAST_RESUME_POLICY"));

        _torrentEngine = new MonoTorrentEngine(
            Path.Combine(ApplicationDataDirectory, "torrent-data"),
            torrentOptions);
        _lifecycleCoordinator = new MobileTorrentLifecycleCoordinator(
            _torrentEngine,
            executionPolicy);

        ApplicationComposition.Configure(
            runtimeProfile,
            catalogRepository,
            fullDatabaseBackupService: new SqliteDatabaseBackupService(database),
            torrentEngine: _torrentEngine,
            magnetOpenPreferencesStore: magnetPreferences,
            userMediaStateStore: userMediaState,
            initialMagnetLinks: initialMagnetLinks,
            metadataProviderSettingsStore: metadataProviderSettings,
            metadataProviders: MetadataProviderFactory.CreateDefault(),
            trackerCredentialProvider: new EnvironmentTrackerCredentialProvider(),
            applicationLifecycleHandler: this,
            platformMediaPlayer: platformMediaPlayer,
            backgroundWebVideoPlayer: backgroundWebVideoPlayer,
             webChallengeSolver: webChallengeSolver,
             contentSourceProviderSettingsStore:
                 new SqliteContentSourceProviderSettingsStore(database),
             contentSourceDailyUpdateStore:
                 new SqliteContentSourceDailyUpdateStore(database),
             torrentDownloadStore: torrentDownloadStore,
             torrentDownloadPreferencesStore:
                 torrentDownloadPreferences,
             remoteServerSettingsStore: remoteServerSettingsStore,
             userSyncStore:
                 new SqliteUserSyncStore(
                     database,
                     catalogRepository),
             applicationUpdateService:
                 applicationUpdateServiceFactory?.Invoke(
                     remoteServerSettingsStore),
             remoteSessionStore:
                 new SqliteRemoteSessionStore(database),
             apiTitleBindingStore:
                 new SqliteApiTitleBindingStore(database),
             contentFilterSettingsStore:
                 new SqliteContentFilterSettingsStore(database),
             contentFilterDirectory:
                 Path.Combine(ApplicationDataDirectory, "content-filters"),
             contentFilterDeviceTier: contentFilterDeviceTier,
             canRefreshContentFilters: CanRefreshContentFilters,
             userProfileStore: new SqliteUserProfileStore(database),
             activeProfile: database);
    }

    public string ApplicationDataDirectory { get; }

    /// <summary>
    /// Whether now is an acceptable moment to fetch filter lists. A dozen
    /// megabytes over a metered connection, or any download while the device
    /// is trying to save power, is not something a media client should do
    /// unasked.
    /// </summary>
    private bool CanRefreshContentFilters() =>
        _isNetworkAvailable && !_isNetworkMetered && !_isPowerSaving;

    public static MobileApplicationHost Start(
        string applicationDataDirectory,
        RuntimeProfile runtimeProfile,
        IReadOnlyList<string>? initialMagnetLinks = null,
        MobileExecutionPolicy? executionPolicy = null,
        IPlatformMediaPlayer? platformMediaPlayer = null,
        IBackgroundWebVideoPlayer? backgroundWebVideoPlayer = null,
        Func<IRemoteServerSettingsStore, IApplicationUpdateService>?
            applicationUpdateServiceFactory = null,
        DeviceTier contentFilterDeviceTier = DeviceTier.Standard,
        IWebChallengeSolver? webChallengeSolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentNullException.ThrowIfNull(runtimeProfile);

        return new MobileApplicationHost(
            applicationDataDirectory,
            runtimeProfile,
            initialMagnetLinks?.ToArray() ?? [],
            executionPolicy,
            platformMediaPlayer,
            backgroundWebVideoPlayer,
            applicationUpdateServiceFactory,
            contentFilterDeviceTier,
            webChallengeSolver);
    }

    public ValueTask EnterBackgroundAsync(CancellationToken cancellationToken = default) =>
        _lifecycleCoordinator.SetForegroundAsync(false, cancellationToken);

    public ValueTask EnterForegroundAsync(CancellationToken cancellationToken = default) =>
        _lifecycleCoordinator.SetForegroundAsync(true, cancellationToken);

    public ValueTask SetNetworkStateAsync(
        MobileNetworkState state,
        CancellationToken cancellationToken = default)
    {
        _isNetworkAvailable = state.IsAvailable;
        _isNetworkMetered = state.IsMetered;
        return _lifecycleCoordinator.SetNetworkStateAsync(
            state,
            cancellationToken);
    }

    public ValueTask SetPowerSavingAsync(
        bool isPowerSaving,
        CancellationToken cancellationToken = default)
    {
        _isPowerSaving = isPowerSaving;
        return _lifecycleCoordinator.SetPowerSavingAsync(
            isPowerSaving,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cleanupCompleted = false;
        try
        {
            await _lifecycleCoordinator.DisposeAsync();
            await _torrentEngine.DisposeAsync();
            cleanupCompleted = true;
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Error(
                "app.shutdown",
                "Mobile resource cleanup failed.",
                exception);
            throw;
        }
        finally
        {
            ApplicationDiagnosticLog.EndSession(
                $"cleanupCompleted={cleanupCompleted}");
        }
    }
}
