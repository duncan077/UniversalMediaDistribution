using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Backup;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.LiveTv;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;
using UniversalMediaDistribution.Reading;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.UI.Filtering;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.Remote;
using UniversalMediaDistribution.UI.Updates;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Composition;

public static class ApplicationComposition
{
    private static readonly TimeSpan ContentSourceHttpTimeout =
        TimeSpan.FromSeconds(25);
    private static RuntimeProfile? _runtimeProfile;
    private static ICatalogTransferStore? _catalogRepository;
    private static IFullDatabaseBackupService? _fullDatabaseBackupService;
    private static ITorrentEngine? _torrentEngine;
    private static IMagnetOpenPreferencesStore? _magnetOpenPreferencesStore;
    private static IUserMediaStateStore? _userMediaStateStore;
    private static IUserProfileStore? _userProfileStore;
    private static IActiveProfileAccessor? _activeProfile;
    private static IPlatformMediaPlayer? _platformMediaPlayer;
    private static IBackgroundWebVideoPlayer? _backgroundWebVideoPlayer;
    private static IPdfRenderer? _pdfRenderer;
    private static IMetadataProviderSettingsStore? _metadataProviderSettingsStore;
    private static IContentSourceProviderSettingsStore? _contentSourceProviderSettingsStore;
    private static IContentSourceDailyUpdateStore? _contentSourceDailyUpdateStore;
    private static ITrackerCredentialProvider? _trackerCredentialProvider;
    private static IReadOnlyList<ICatalogMetadataProvider> _metadataProviders = [];
    private static IReadOnlyList<IApiContentProvider> _apiContentProviders = [];
    private static HttpClient? _contentSourceHttpClient;
    private static IReadOnlyList<string> _initialMagnetLinks = [];
    private static IApplicationLifecycleHandler? _applicationLifecycleHandler;
    private static ITorrentDownloadStore? _torrentDownloadStore;
    private static ITorrentDownloadPreferencesStore?
        _torrentDownloadPreferencesStore;
    private static IApplicationUpdateService? _applicationUpdateService;
    private static IRemoteServerSettingsStore? _remoteServerSettingsStore;
    private static IUserSyncStore? _userSyncStore;
    private static IRemoteMediaServerClient? _remoteMediaServerClient;
    private static IRemoteSessionStore? _remoteSessionStore;
    private static IApiTitleBindingStore? _apiTitleBindingStore;
    private static ContentFilterCoordinator? _contentFilterCoordinator;
    private static IReadOnlyList<ILiveTvProvider> _additionalLiveTvProviders = [];

    public static void Configure(
        RuntimeProfile runtimeProfile,
        ICatalogTransferStore catalogRepository,
        IFullDatabaseBackupService? fullDatabaseBackupService = null,
        ITorrentEngine? torrentEngine = null,
        IMagnetOpenPreferencesStore? magnetOpenPreferencesStore = null,
        IUserMediaStateStore? userMediaStateStore = null,
        IReadOnlyList<string>? initialMagnetLinks = null,
        IPlatformMediaPlayer? platformMediaPlayer = null,
        IPdfRenderer? pdfRenderer = null,
        IMetadataProviderSettingsStore? metadataProviderSettingsStore = null,
        IReadOnlyList<ICatalogMetadataProvider>? metadataProviders = null,
        ITrackerCredentialProvider? trackerCredentialProvider = null,
        IApplicationLifecycleHandler? applicationLifecycleHandler = null,
        IContentSourceProviderSettingsStore? contentSourceProviderSettingsStore = null,
        IContentSourceDailyUpdateStore? contentSourceDailyUpdateStore = null,
        IReadOnlyList<IApiContentProvider>? apiContentProviders = null,
        HttpClient? contentSourceHttpClient = null,
        IWebChallengeSolver? webChallengeSolver = null,
        ITorrentDownloadStore? torrentDownloadStore = null,
        ITorrentDownloadPreferencesStore?
            torrentDownloadPreferencesStore = null,
        IApplicationUpdateService? applicationUpdateService = null,
        IRemoteServerSettingsStore? remoteServerSettingsStore = null,
        IUserSyncStore? userSyncStore = null,
        IRemoteMediaServerClient? remoteMediaServerClient = null,
        IRemoteSessionStore? remoteSessionStore = null,
        IApiTitleBindingStore? apiTitleBindingStore = null,
        IContentFilterSettingsStore? contentFilterSettingsStore = null,
        string? contentFilterDirectory = null,
        DeviceTier contentFilterDeviceTier = DeviceTier.Standard,
        Func<bool>? canRefreshContentFilters = null,
        IUserProfileStore? userProfileStore = null,
        IActiveProfileAccessor? activeProfile = null,
        IReadOnlyList<ILiveTvProvider>? additionalLiveTvProviders = null,
        IBackgroundWebVideoPlayer? backgroundWebVideoPlayer = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeProfile);
        ArgumentNullException.ThrowIfNull(catalogRepository);

        _additionalLiveTvProviders = additionalLiveTvProviders ?? [];
        _runtimeProfile = runtimeProfile;
        _catalogRepository = catalogRepository;
        _fullDatabaseBackupService = fullDatabaseBackupService;
        _torrentEngine = torrentEngine;
        _magnetOpenPreferencesStore = magnetOpenPreferencesStore ??
            new InMemoryMagnetOpenPreferencesStore();
        _userMediaStateStore = userMediaStateStore ?? new InMemoryUserMediaStateStore();
        _userProfileStore = userProfileStore;
        _activeProfile = activeProfile;
        _platformMediaPlayer = platformMediaPlayer ?? new UnavailablePlatformMediaPlayer();
        _backgroundWebVideoPlayer = backgroundWebVideoPlayer ??
            UnavailableBackgroundWebVideoPlayer.Instance;
        _pdfRenderer = pdfRenderer ?? new UnavailablePdfRenderer();
        _metadataProviderSettingsStore = metadataProviderSettingsStore ??
            new InMemoryMetadataProviderSettingsStore();
        _metadataProviders = metadataProviders?.ToArray() ?? [];
        _contentSourceProviderSettingsStore = contentSourceProviderSettingsStore ??
            new InMemoryContentSourceProviderSettingsStore();
        _contentSourceDailyUpdateStore = contentSourceDailyUpdateStore ??
            new InMemoryContentSourceDailyUpdateStore();
        _apiContentProviders = apiContentProviders?.ToArray() ?? [];
        // Without a platform browser to drive, the gate never clears and a
        // challenged host surfaces its refusal exactly as before.
        WebChallengeGate.Configure(webChallengeSolver);
        _contentSourceHttpClient =
            contentSourceHttpClient ?? CreateContentSourceHttpClient();
        _trackerCredentialProvider =
            trackerCredentialProvider ?? UnavailableTrackerCredentialProvider.Instance;
        _initialMagnetLinks = initialMagnetLinks?.ToArray() ?? [];
        _applicationLifecycleHandler = applicationLifecycleHandler;
        _torrentDownloadStore =
            torrentDownloadStore ?? new InMemoryTorrentDownloadStore();
        _torrentDownloadPreferencesStore =
            torrentDownloadPreferencesStore ??
            new InMemoryTorrentDownloadPreferencesStore();
        _applicationUpdateService =
            applicationUpdateService ??
            UnavailableApplicationUpdateService.Instance;
        _remoteServerSettingsStore =
            remoteServerSettingsStore ??
            new InMemoryRemoteServerSettingsStore();
        _userSyncStore = userSyncStore;
        _remoteMediaServerClient =
            remoteMediaServerClient ??
            new HttpRemoteMediaServerClient();
        // Without a durable store the session lasts only for this run, which is
        // the right default for hosts that have no private storage.
        _remoteSessionStore =
            remoteSessionStore ?? new InMemoryRemoteSessionStore();
        _apiTitleBindingStore =
            apiTitleBindingStore ?? new InMemoryApiTitleBindingStore();

        // A refresh must not compete with the video the viewer is watching, so
        // the host's own conditions are combined with "no embedded page is
        // open" before any list is fetched.
        var embeddedPlayer = _platformMediaPlayer as WebViewPlatformMediaPlayer;
        _contentFilterCoordinator = new ContentFilterCoordinator(
            contentFilterDeviceTier,
            contentFilterSettingsStore,
            contentFilterDirectory,
            canRefresh: () =>
                (canRefreshContentFilters?.Invoke() ?? true) &&
                embeddedPlayer?.FilterSession.IsActive is not true,
            isMobileHost: runtimeProfile.IsTouchPrimary ||
                runtimeProfile.IsTelevision);
    }

    public static ContentFilterCoordinator ContentFilters =>
        _contentFilterCoordinator ??=
            new ContentFilterCoordinator(DeviceTier.Standard);

    public static IBackgroundWebVideoPlayer BackgroundWebVideoPlayer =>
        _backgroundWebVideoPlayer ??
        UnavailableBackgroundWebVideoPlayer.Instance;

    internal static HttpClientHandler CreateContentSourceHttpHandler() =>
        new()
        {
            // Extractors validate every redirect they intentionally follow.
            // Letting the primary handler redirect underneath them would make
            // those checks ineffective.
            AllowAutoRedirect = false
        };

    internal static IApplicationLifecycleHandler? ApplicationLifecycleHandler =>
        _applicationLifecycleHandler;

    public static MainViewModel CreateMainViewModel()
    {
        var runtimeProfile = _runtimeProfile ??
            throw new InvalidOperationException("The platform host must configure the application before Avalonia starts.");
        var catalogRepository = _catalogRepository ??
            throw new InvalidOperationException("The platform host must provide a catalog repository before Avalonia starts.");

        var catalogBrowseService = new CatalogBrowseService(catalogRepository);
        var catalogTransferService = new CatalogJsonTransferService(catalogRepository);
        var torrentSessionRegistry = new TorrentSessionRegistry();
        var torrentActivityCoordinator = new TorrentActivityCoordinator(
            _torrentEngine);
        var torrentDownloadLibrary = new TorrentDownloadLibrary(
            torrentActivityCoordinator,
            _torrentDownloadStore ?? new InMemoryTorrentDownloadStore(),
            _torrentDownloadPreferencesStore ??
                new InMemoryTorrentDownloadPreferencesStore(),
            _torrentEngine as ITorrentDownloadStorage);
        var magnetOpenService = new MagnetOpenService(
            _torrentEngine,
            catalogTransferService,
            _magnetOpenPreferencesStore ?? new InMemoryMagnetOpenPreferencesStore(),
            torrentSessionRegistry,
            _trackerCredentialProvider);
        var mediaSessionService = new TorrentMediaSessionService(
            _torrentEngine,
            torrentSessionRegistry,
            _trackerCredentialProvider,
            torrentActivityCoordinator);
        var metadataProviderSettingsStore = _metadataProviderSettingsStore ??
            new InMemoryMetadataProviderSettingsStore();
        var catalogMetadataRefreshService = new CatalogMetadataRefreshService(
            catalogRepository,
            metadataProviderSettingsStore,
            _metadataProviders);
        var contentSourceHttpClient =
            _contentSourceHttpClient ?? CreateContentSourceHttpClient();
        var hosterResolver = new VideoHosterResolver(contentSourceHttpClient);
        var apiContentProviders = _apiContentProviders
            .Select(static provider =>
                new ApiContentProviderRegistration(provider))
            .ToList();

        foreach (var provider in BuiltInApiContentProviders.Create(contentSourceHttpClient, hosterResolver))
        {
            if (apiContentProviders.All(existing => existing.Descriptor.Id != provider.Descriptor.Id))
                apiContentProviders.Add(provider);
        }

        var contentSourceProviderSettingsStore =
            _contentSourceProviderSettingsStore ??
            new InMemoryContentSourceProviderSettingsStore();
        // A head may contribute its own providers - a platform head with a source the
        // others cannot reach, or a build seeded with known streams for testing.
        IReadOnlyList<ILiveTvProvider> liveTvProviders =
        [
            .. _additionalLiveTvProviders,
            new M3uLiveTvProvider(
                contentSourceHttpClient,
                new LiveTvProviderDescriptor(
                    "iptv-org-argentina",
                    "GitHub Argentina",
                    new Uri(
                        "https://iptv-org.github.io/iptv/countries/ar.m3u"),
                    IsEnabledByDefault: false)),
            new M3uLiveTvProvider(
                contentSourceHttpClient,
                new LiveTvProviderDescriptor(
                    "ip-tv-app-argentina",
                    "IP-TV App Argentina",
                    new Uri("https://ip-tv.app/Argentina"),
                    IsEnabledByDefault: false)),
            new StreamflixHtmlLiveTvProvider(
                contentSourceHttpClient,
                new LiveTvProviderDescriptor(
                    "cablevision-hd",
                    "CableVisionHD",
                    new Uri(
                        "https://www.cablevisionhd.com/parrilla-directo.php"),
                    IsEnabledByDefault: false),
                hosterResolver),
            new StreamflixHtmlLiveTvProvider(
                contentSourceHttpClient,
                new LiveTvProviderDescriptor(
                    "tv-por-internet-hd",
                    "TV por Internet HD",
                    new Uri("https://www.tvporinternet.org/"),
                    IsEnabledByDefault: false),
                hosterResolver),
            new StreamflixHtmlLiveTvProvider(
                contentSourceHttpClient,
                new LiveTvProviderDescriptor(
                    "tv-libre-futbol",
                    "TV Libre Fútbol",
                    new Uri(
                        "https://www.librefutbol2.com/parrilla-directo.php")),
                hosterResolver)
        ];
        var contentSourceCatalogService = new ContentSourceCatalogService(
            catalogBrowseService,
            catalogTransferService,
            contentSourceProviderSettingsStore,
            contentSourceHttpClient,
            apiContentProviders,
            _contentSourceDailyUpdateStore ??
                new InMemoryContentSourceDailyUpdateStore(),
            timeProvider: null,
            _apiTitleBindingStore ?? new InMemoryApiTitleBindingStore(),
            liveTvProviders.Select(static provider => provider.Descriptor),
            hideAdultContent: () => ContentFilters.HideAdultContent);
        var liveTvCatalogService = new LiveTvCatalogService(
            liveTvProviders,
            contentSourceProviderSettingsStore);

        return new MainViewModel(
            catalogBrowseService,
            catalogTransferService,
            _fullDatabaseBackupService,
            catalogMetadataRefreshService,
            metadataProviderSettingsStore,
            magnetOpenService,
            _userMediaStateStore ?? new InMemoryUserMediaStateStore(),
            mediaSessionService,
            _pdfRenderer ?? new UnavailablePdfRenderer(),
            _platformMediaPlayer ?? new UnavailablePlatformMediaPlayer(),
            _initialMagnetLinks,
            runtimeProfile,
            contentSourceCatalogService,
            liveTvCatalogService,
            torrentActivityCoordinator,
            torrentDownloadLibrary,
            _applicationUpdateService ??
                UnavailableApplicationUpdateService.Instance,
            _remoteServerSettingsStore ??
                new InMemoryRemoteServerSettingsStore(),
            _userSyncStore,
            _remoteMediaServerClient ??
                new HttpRemoteMediaServerClient(),
            catalogRepository as ICatalogRecoveryStatus,
            _remoteSessionStore ?? new InMemoryRemoteSessionStore(),
            ContentFilters,
            hosterResolver,
            _userProfileStore,
            _activeProfile);
    }

    private static HttpClient CreateContentSourceHttpClient() =>
        new(CreateContentSourceHttpHandler())
        {
            Timeout = ContentSourceHttpTimeout
        };
}
