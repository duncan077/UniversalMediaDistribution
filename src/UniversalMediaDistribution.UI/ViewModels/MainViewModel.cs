using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Reading;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Filtering;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.Remote;
using UniversalMediaDistribution.UI.Threading;
using UniversalMediaDistribution.UI.Updates;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed class ApiVideoServerOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    public ApiVideoServerOptionViewModel(
        ApiContentVideoSource source,
        Func<ApiContentVideoSource, Task> selectAsync)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(selectAsync);
        SelectCommand = new AsyncRelayCommand(() => selectAsync(Source));
    }

    public ApiContentVideoSource Source { get; }

    public string Label => Source.Label;

    public bool IsSelected
    {
        get => _isSelected;
        private set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    public string DisplayLabel
    {
        get
        {
            var kind = Source.IsEmbeddedWeb
                ? $" · {UiTextCatalog.Get("EmbeddedVideoServerShort")}"
                : string.Empty;
            return IsSelected
                ? $"✓ {Label}{kind}"
                : $"{Label}{kind}";
        }
    }

    public IAsyncRelayCommand SelectCommand { get; }

    internal void SetSelected(bool value) => IsSelected = value;
}

public partial class MainViewModel(
    CatalogBrowseService catalogBrowseService,
    CatalogJsonTransferService catalogTransferService,
    IFullDatabaseBackupService? fullDatabaseBackupService,
    CatalogMetadataRefreshService catalogMetadataRefreshService,
    IMetadataProviderSettingsStore metadataProviderSettingsStore,
    MagnetOpenService magnetOpenService,
    IUserMediaStateStore userMediaStateStore,
    TorrentMediaSessionService mediaSessionService,
    IPdfRenderer pdfRenderer,
    IPlatformMediaPlayer platformMediaPlayer,
    IReadOnlyList<string> initialMagnetLinks,
    RuntimeProfile runtimeProfile,
    ContentSourceCatalogService contentSourceCatalogService,
    LiveTvCatalogService liveTvCatalogService,
    TorrentActivityCoordinator torrentActivityCoordinator,
    TorrentDownloadLibrary torrentDownloadLibrary,
    IApplicationUpdateService applicationUpdateService,
    IRemoteServerSettingsStore remoteServerSettingsStore,
    IUserSyncStore? userSyncStore,
    IRemoteMediaServerClient remoteMediaServerClient,
    ICatalogRecoveryStatus? catalogRecoveryStatus = null,
    IRemoteSessionStore? remoteSessionStore = null,
    ContentFilterCoordinator? contentFilterCoordinator = null,
    VideoHosterResolver? videoHosterResolver = null,
    IUserProfileStore? userProfileStore = null,
    IActiveProfileAccessor? activeProfile = null) : ViewModelBase
{
    public const string DonationWalletAddress =
        "0x333f33497816cf4Cb6b4e13cfE5C9Bb11508B6b5";

    private readonly FriendService _friendService =
        new(remoteMediaServerClient);

    private readonly WatchPartyService _watchPartyService =
        new(remoteMediaServerClient, platformMediaPlayer);

    /// <summary>
    /// Who can use this installation, and who is using it now.
    /// <para>
    /// The picker only appears when there is more than one: a device with a
    /// single profile has nothing to choose between, and asking anyway would
    /// put a screen in front of every launch to no purpose.
    /// </para>
    /// </summary>
    public ObservableCollection<ProfileTileViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherProfiles))]
    private ProfileTileViewModel? _currentProfile;

    [ObservableProperty]
    private bool _isProfilePickerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigateBack))]
    private bool _isDonationPromptVisible = true;

    [ObservableProperty]
    private bool _isDonationAddressCopied;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    public bool SupportsProfiles =>
        userProfileStore is not null && activeProfile is not null;

    public bool HasOtherProfiles => Profiles.Count > 1;

    public string CurrentProfileName =>
        CurrentProfile?.Name ?? string.Empty;

    public string DonationAddress => DonationWalletAddress;

    private int _detailFriendProgressGeneration;

    /// <summary>
    /// How long a saved position waits before the snapshot is uploaded.
    /// <para>
    /// A snapshot is the whole state, not a delta: measured against a library
    /// of 1,774 titles it came to 2.7 MiB, of which 92% was catalog and 8% the
    /// progress this exists to carry. The reconciler also pulls the remote
    /// snapshot before pushing, so a cycle moves roughly twice that. Minutes,
    /// not seconds, is the only defensible interval for something that
    /// expensive; closing a reader flushes it early so finishing a sitting
    /// still publishes promptly.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ProgressSyncDelay =
        TimeSpan.FromMinutes(5);

    /// <summary>
    /// A patch is kilobytes where a snapshot is megabytes, so it can afford to
    /// go out while a sitting is still in progress rather than after it.
    /// </summary>
    private static readonly TimeSpan ProgressPatchDelay =
        TimeSpan.FromSeconds(20);

    private readonly Lock _progressSyncGate = new();
    private CancellationTokenSource? _progressSyncCancellation;
    private readonly HashSet<Guid> _movedCatalogItems = [];
    private bool _supportsProgressPatch;
    private bool _supportsDeltaSync;

    /// <summary>
    /// The document this device last proved the server holds — what a delta
    /// upload measures "changed here" against. In memory only: the first
    /// reconcile of a session runs the full path once and seeds it.
    /// </summary>
    private UserSyncDocument? _syncBaseline;
    private long _syncBaselineRevision;

    /// <summary>
    /// The upload currently on its way to the server, so a profile switch can
    /// wait for it. Read only there; everywhere else an upload is deliberately
    /// left to finish on its own.
    /// </summary>
    private Task _progressUpload = Task.CompletedTask;

    private IUserMediaStateStore? _progressSyncingStore;

    /// <summary>
    /// Every saved position passes through here, so an upload follows reading
    /// without anyone pressing anything. Built on first use because a field
    /// initializer cannot reach the instance method it has to call.
    /// </summary>
    private IUserMediaStateStore UserMediaState =>
        _progressSyncingStore ??= new ProgressSyncingUserMediaStateStore(
            userMediaStateStore,
            ScheduleProgressSync);

    private static readonly TimeSpan UpdatePromptLifetime =
        TimeSpan.FromSeconds(30);

    private static readonly TimeSpan TelevisionUpdatePromptLifetime =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProviderRefreshInterval =
        TimeSpan.FromHours(1);

    /// <summary>
    /// How long the embedded player surface gets to be laid out before
    /// playback starts regardless. Long next to a layout pass, short enough
    /// that a surface which never arrives does not hold up the video.
    /// </summary>
    private static readonly TimeSpan EmbeddedSurfaceTimeout =
        TimeSpan.FromSeconds(2);

    /// <summary>Roughly one display frame between layout checks.</summary>
    private static readonly TimeSpan EmbeddedSurfacePollInterval =
        TimeSpan.FromMilliseconds(16);

    // Sized above everything that can be alive at once, or the trim evicts
    // cards the shelf cache is still holding and the reuse key stops meaning
    // anything: three browse sections at fourteen rows of twelve is 504, plus
    // home's five rows and a flat page. At 512 that tipped over on the fourth
    // section, and returning to the first rebuilt every card view model,
    // re-decoded every poster and dropped television focus out of the row.
    // The bitmaps live in a separately byte-bounded cache, so this only has to
    // cover the view models.
    private const int CatalogCardCacheCapacity = 1024;

    private bool _initialized;
    private CancellationTokenSource? _updatePromptTimeout;
    private bool _isUpdatePromptPinned;
    private MediaType? _selectedMediaType;
    // Movies split into two shelves: anime and everything else. Null means the
    // section does not split, so both are shown.
    private MediaSubtypeGroup? _selectedSubtypeGroup;
    private CancellationTokenSource? _playbackMonitorCancellation;
    // Opening a catalog item keeps loading after the user has moved on, so each
    // open owns a scope that the next navigation cancels. Without it a slow
    // provider refresh finishes late and replaces whatever the user is looking
    // at now with the item they already left.
    private CancellationTokenSource? _detailsLoadCancellation;
    private Task? _playbackMonitorTask;
    private CatalogContentIdentity? _activePlaybackIdentity;
    private IReadOnlyList<CatalogCard> _catalogCards = [];

    /// <summary>
    /// Every title in the library, regardless of which section is open.
    /// <para>
    /// The browse list is fetched per media type, so a page for books holds no
    /// films and a favourites-only view built from it could only ever show the
    /// favourites of whichever section happened to be open. Starring is not a
    /// property of a section, so asking for favourites is answered from here
    /// instead. Filled the first time it is needed and reused after.
    /// </para>
    /// </summary>
    private IReadOnlyList<CatalogCard> _allCatalogCards = [];
    private Task? _allCatalogCardsLoad;
    private ReaderDevicePreferences _readerDevicePreferences =
        ReaderDevicePreferences.Default;
    private HashSet<Guid> _favoriteCatalogItemIds = [];
    private Dictionary<Guid, CatalogProgressSummary>
        _catalogProgressByItemId =
            [];
    private Dictionary<Guid, CatalogProgressSummary>
        _catalogRecentActivityByItemId =
            [];
    private Dictionary<string, CatalogProgressSummary>
        _catalogProgressByContentKey =
            new Dictionary<string, CatalogProgressSummary>(
                StringComparer.Ordinal);

    /// <summary>
    /// The newest chapter or episode of every title, so a card can name it
    /// whether or not the viewer has started reading.
    /// </summary>
    private IReadOnlyDictionary<Guid, CatalogLatestUnit> _latestUnitsByItemId =
        new Dictionary<Guid, CatalogLatestUnit>();
    private IReadOnlyList<UserMediaList> _userMediaLists = [];
    private bool _isRebuildingProviderFilters;
    private bool _isRebuildingCatalogFilters;
    private string? _providerChoiceTargetStableKey;
    private string _browseSectionTitle = UiTextCatalog.Get("SectionAll");
    private IReadOnlyList<ContentSourceCatalogSearchResult> _searchResults = [];
    private readonly Dictionary<CatalogCardReuseKey, CachedCatalogCard>
        _catalogCardCache = [];
    // Every provider save serializes the whole list, so two in flight at once
    // let the later one write a snapshot taken before the earlier one landed.
    private readonly SemaphoreSlim _contentSourceSaveGate = new(1, 1);
    private readonly Dictionary<CatalogHomeSectionKind, CatalogSectionViewModel>
        _catalogSectionCache = [];
    // Keyed by section as well as by shelf: movies, anime and books each have
    // a "Drama" row, and handing all three the same view model let the second
    // one reuse the first one's container — and with it the sideways scroll
    // position that container was left at.
    private readonly Dictionary<
        (string Section, CatalogBrowseShelfKey Shelf),
        CatalogSectionViewModel> _browseShelfCache = [];

    // Cached so a rebuilt shelf keeps the same command instance and
    // CatalogSectionViewModel.Update stays a no-op when nothing moved.
    private readonly Dictionary<string, IRelayCommand>
        _genreShelfCommands = new(StringComparer.Ordinal);
    private long _catalogCardCacheClock;
    private int _searchGeneration;
    private IReadOnlyList<ApiContentArtworkPolicy> _artworkPolicies = [];
    private IReadOnlyList<Uri> _trustedArtworkOrigins = [];
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _apiUnitLookupCancellation;
    private CancellationTokenSource? _dailyRefreshCancellation;
    private Task? _dailyRefreshTask;
    private DateTimeOffset? _lastDailyRefreshStartedAt;
    private CancellationTokenSource? _providerRefreshSchedulerCancellation;
    private Task? _providerRefreshSchedulerTask;
    private bool _hasEnabledApiContentProviders;
    private ReaderChapterNavigation? _readerChapterNavigation;
    private ReaderChapterNavigation? _playerEpisodeNavigation;
    private ApiContentVideoSelection? _activeApiVideoSelection;
    private readonly object _shutdownGate = new();
    private readonly CoalescedDispatcherAction _downloadRefresh = new();
    private readonly Dictionary<TorrentActivityId, TorrentActivitySnapshot>
        _torrentActivities = [];
    private readonly HashSet<TorrentActivityId>
        _dismissedTorrentActivities = [];
    private CancellationTokenSource? _updateCheckCancellation;
    private IReadOnlyList<Guid> _pendingDownloadDeletion = [];
    private RemoteServerSettings _remoteServerSettings = new();
    private long _remoteSyncRevision;
    private bool _hasRemoteSnapshot;
    private RemoteSharedList? _lastPublishedSharedList;
    private Task? _shutdownTask;
    private CancellationTokenSource? _liveTvLoadCancellation;
    private IReadOnlyList<LiveTvChannelViewModel> _allLiveTvChannels = [];
    private bool _hasLoadedLiveTv;
    private bool _isRebuildingLiveTvFilters;

    [ObservableProperty]
    private string _sectionTitle = UiTextCatalog.Get("SectionAll");

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _movieCount;

    [ObservableProperty]
    private int _bookCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLiveTvChannels))]
    [NotifyPropertyChangedFor(nameof(IsAnythingLoading))]
    private bool _isBusy;

    /// <summary>
    /// Whether any shell-wide work is running, for the bar pinned to the top of
    /// the window.
    /// <para>
    /// The progress bar used to live in the catalog header, which scrolls. A
    /// request started further down the page therefore reported itself somewhere
    /// the reader could not see, and the only way to find out whether anything
    /// was happening was to scroll back up. Work that belongs to one row — a
    /// provider lookup, a page decode — still reports on that row instead; this
    /// is for work that belongs to the whole window.
    /// </para>
    /// </summary>
    public bool IsAnythingLoading => IsBusy || IsRemoteSyncBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogOpen))]
    [NotifyPropertyChangedFor(nameof(IsCatalogOrSettingsOpen))]
    [NotifyPropertyChangedFor(nameof(CanNavigateBack))]
    [NotifyPropertyChangedFor(nameof(ShellTitle))]
    [NotifyPropertyChangedFor(nameof(ShellSubtitle))]
    [NotifyPropertyChangedFor(nameof(IsDetailsOpen))]
    [NotifyPropertyChangedFor(nameof(ShowSettingsPanels))]
    [NotifyPropertyChangedFor(nameof(ShowSettingsFileActions))]
    [NotifyPropertyChangedFor(nameof(ShowApplicationUpdateSettings))]
    [NotifyPropertyChangedFor(nameof(ShowSettingsBackup))]
    [NotifyPropertyChangedFor(nameof(ShowRemoteSyncSettings))]
    [NotifyPropertyChangedFor(nameof(IsAllSection))]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowBrowseShelves))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogViewToggle))]
    [NotifyPropertyChangedFor(nameof(ShowFlatCatalogList))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyPropertyChangedFor(nameof(IsMoviesSection))]
    [NotifyPropertyChangedFor(nameof(IsAnimeSection))]
    [NotifyPropertyChangedFor(nameof(IsBooksSection))]
    [NotifyPropertyChangedFor(nameof(IsBrowseSection))]
    [NotifyPropertyChangedFor(nameof(IsLiveTvSection))]
    [NotifyPropertyChangedFor(nameof(IsUserListsSection))]
    [NotifyPropertyChangedFor(nameof(IsDownloadsSection))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogOverviewTools))]
    [NotifyPropertyChangedFor(nameof(ShowNoUserLists))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyUserList))]
    private bool _isSettingsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogOpen))]
    [NotifyPropertyChangedFor(nameof(IsCatalogOrSettingsOpen))]
    [NotifyPropertyChangedFor(nameof(CanNavigateBack))]
    [NotifyPropertyChangedFor(nameof(ShellTitle))]
    [NotifyPropertyChangedFor(nameof(ShellSubtitle))]
    [NotifyPropertyChangedFor(nameof(IsAllSection))]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowBrowseShelves))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogViewToggle))]
    [NotifyPropertyChangedFor(nameof(ShowFlatCatalogList))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyPropertyChangedFor(nameof(IsMoviesSection))]
    [NotifyPropertyChangedFor(nameof(IsAnimeSection))]
    [NotifyPropertyChangedFor(nameof(IsBooksSection))]
    [NotifyPropertyChangedFor(nameof(IsBrowseSection))]
    [NotifyPropertyChangedFor(nameof(IsLiveTvSection))]
    [NotifyPropertyChangedFor(nameof(IsUserListsSection))]
    [NotifyPropertyChangedFor(nameof(IsDownloadsSection))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogOverviewTools))]
    private bool _isDownloadsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogOpen))]
    [NotifyPropertyChangedFor(nameof(IsCatalogOrSettingsOpen))]
    [NotifyPropertyChangedFor(nameof(IsLiveTvPageVisible))]
    [NotifyPropertyChangedFor(nameof(ShowNoLiveTvChannels))]
    [NotifyPropertyChangedFor(nameof(CanNavigateBack))]
    [NotifyPropertyChangedFor(nameof(ShellTitle))]
    [NotifyPropertyChangedFor(nameof(ShellSubtitle))]
    [NotifyPropertyChangedFor(nameof(IsAllSection))]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowBrowseShelves))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogViewToggle))]
    [NotifyPropertyChangedFor(nameof(ShowFlatCatalogList))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyPropertyChangedFor(nameof(IsMoviesSection))]
    [NotifyPropertyChangedFor(nameof(IsAnimeSection))]
    [NotifyPropertyChangedFor(nameof(IsBooksSection))]
    [NotifyPropertyChangedFor(nameof(IsBrowseSection))]
    [NotifyPropertyChangedFor(nameof(IsLiveTvSection))]
    [NotifyPropertyChangedFor(nameof(IsUserListsSection))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogOverviewTools))]
    private bool _isLiveTvOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavigationToggleText))]
    [NotifyPropertyChangedFor(nameof(NavigationToggleGlyph))]
    private bool _isNavigationCollapsed;

    [ObservableProperty]
    private string? _errorMessage;

    private string? _statusMessage;

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            SetProperty(ref _statusMessage, value);
            OnStatusMessageChanged(value);
        }
    }

    [ObservableProperty]
    private string _magnetLinkInput = string.Empty;

    [ObservableProperty]
    private bool _openMagnetsAutomatically;

    [ObservableProperty]
    private bool _importTorrentInfoJson = true;

    [ObservableProperty]
    private bool _treatExternalMagnetsAsPrivate;

    [ObservableProperty]
    private bool _deleteTorrentDataWhenSessionEnds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedContentFilterModeDescription))]
    private ContentFilterModeOptionViewModel? _selectedContentFilterMode;

    [ObservableProperty]
    private bool _hideAdultContent;

    [ObservableProperty]
    private string _contentFilterStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadDeleteConfirmationText))]
    private bool _isDownloadDeleteConfirmationVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate))]
    [NotifyPropertyChangedFor(nameof(AvailableUpdateTitle))]
    [NotifyPropertyChangedFor(nameof(AvailableUpdateNotes))]
    [NotifyPropertyChangedFor(nameof(AvailableUpdateSize))]
    private ApplicationUpdateInfo? _availableUpdate;

    [ObservableProperty]
    private bool _isUpdatePromptVisible;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateDownloadProgressPercent))]
    private double _updateDownloadProgress;

    [ObservableProperty]
    private string _remoteServiceUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteAccountSummary))]
    private string _remoteUsername = string.Empty;

    [ObservableProperty]
    private string _remotePassword = string.Empty;

    [ObservableProperty]
    private string _remoteDisplayName = string.Empty;

    [ObservableProperty]
    private string _remoteDeviceName = Environment.MachineName;

    [ObservableProperty]
    private bool _remoteOnlineCatalogEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteAccountSummary))]
    [NotifyPropertyChangedFor(nameof(CanUseRemoteSync))]
    [NotifyPropertyChangedFor(nameof(CanRestoreRemoteSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanPublishSelectedUserList))]
    private bool _isRemoteSignedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseRemoteSync))]
    [NotifyPropertyChangedFor(nameof(CanRestoreRemoteSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanPublishSelectedUserList))]
    [NotifyPropertyChangedFor(nameof(IsAnythingLoading))]
    private bool _isRemoteSyncBusy;

    [ObservableProperty]
    private bool _isRemoteRestoreConfirmationVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteAccountSummary))]
    private string _remoteAccountDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteSnapshotSummary))]
    private DateTimeOffset? _remoteSnapshotUpdatedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteSharedListUrl))]
    private string _remoteSharedListUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingMagnetDisplay))]
    private string? _pendingMagnetLink;

    [ObservableProperty]
    private string _tmdbApiKey = string.Empty;

    [ObservableProperty]
    private string _myAnimeListClientId = string.Empty;

    [ObservableProperty]
    private string _metadataLanguage = GetDefaultMetadataLanguage();

    [ObservableProperty]
    private CatalogDetailsViewModel? _selectedDetails;

    [ObservableProperty]
    private bool _isCatalogItemDeleteConfirmationVisible;

    [ObservableProperty]
    private ProviderFilterViewModel? _selectedProviderFilter;

    [ObservableProperty]
    private CatalogCategoryFilterViewModel? _selectedCategoryFilter;

    [ObservableProperty]
    private CatalogStatusFilterViewModel? _selectedStatusFilter;

    [ObservableProperty]
    private CatalogYearFilterViewModel? _selectedYearFilter;

    [ObservableProperty]
    private CatalogGenreFilterViewModel? _selectedGenreFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteItemsFilterText))]
    [NotifyPropertyChangedFor(nameof(HasCatalogFilters))]
    [NotifyPropertyChangedFor(nameof(HasAnyCatalogSelection))]
    private bool _showFavoritesOnly;

    /// <summary>
    /// Narrows a user list to the titles the viewer has starred. Separate from
    /// <see cref="ShowFavoritesOnly"/>, which is about favourite providers.
    /// </summary>
    [ObservableProperty]
    private bool _showFavoriteTitlesOnly;

    /// <summary>
    /// Narrows the catalog to one of the viewer's lists. Null shows all.
    /// </summary>
    [ObservableProperty]
    private UserMediaListViewModel? _selectedCatalogListFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    [NotifyPropertyChangedFor(nameof(HasSearchCriteria))]
    [NotifyPropertyChangedFor(nameof(HasAnyCatalogSelection))]
    private string _searchQuery = string.Empty;

    /// <summary>
    /// Sticky across pages on purpose: someone who wants the paged grid wants
    /// it on movies and on books, and having to ask again on every page would
    /// make the toggle feel broken.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowseShelves))]
    [NotifyPropertyChangedFor(nameof(ShowFlatCatalogList))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyPropertyChangedFor(nameof(CatalogViewToggleText))]
    private bool _prefersCatalogGrid;

    [ObservableProperty]
    private string _liveTvSearchQuery = string.Empty;

    [ObservableProperty]
    private LiveTvFilterOptionViewModel? _selectedLiveTvProviderFilter;

    [ObservableProperty]
    private LiveTvFilterOptionViewModel? _selectedLiveTvGroupFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTvChannelSummary))]
    private int _liveTvTotalChannelCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTvChannelSummary))]
    private int _liveTvLoadedProviderCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveTvFailures))]
    private string _liveTvFailureSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowBrowseShelves))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogViewToggle))]
    [NotifyPropertyChangedFor(nameof(ShowFlatCatalogList))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    private bool _isSearchActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogPageSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoPreviousCatalogPage))]
    [NotifyPropertyChangedFor(nameof(CanGoNextCatalogPage))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCatalogPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCatalogPageCommand))]
    private int _catalogPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogPageSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoNextCatalogPage))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyCanExecuteChangedFor(nameof(NextCatalogPageCommand))]
    private int _catalogPageCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogPageSummary))]
    private int _catalogResultCount;

    [ObservableProperty]
    private string _newTorrentProviderName = string.Empty;

    [ObservableProperty]
    private string _newTorrentProviderBaseUrl = string.Empty;

    [ObservableProperty]
    private ImageReaderViewModel? _reader;

    [ObservableProperty]
    private EpubReaderViewModel? _epubReader;

    [ObservableProperty]
    private bool _isEmbeddedMediaOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerFullscreenActionText))]
    private bool _isPlayerFullscreen;

    [ObservableProperty]
    private string? _activeMediaTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSection))]
    [NotifyPropertyChangedFor(nameof(IsFriendsSection))]
    [NotifyPropertyChangedFor(nameof(IsMoviesSection))]
    [NotifyPropertyChangedFor(nameof(IsAnimeSection))]
    [NotifyPropertyChangedFor(nameof(IsBooksSection))]
    [NotifyPropertyChangedFor(nameof(IsUserListsSection))]
    [NotifyPropertyChangedFor(nameof(IsCatalogOpen))]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowBrowseShelves))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogViewToggle))]
    [NotifyPropertyChangedFor(nameof(ShowFlatCatalogList))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogOverviewTools))]
    private bool _isFriendsOpen;

    [ObservableProperty]
    private string _friendUsernameQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFriends))]
    private bool _isFriendsBusy;

    /// <summary>
    /// The account-wide switch. Off until deliberately turned on, so nothing
    /// is shared by simply having friends.
    /// </summary>
    [ObservableProperty]
    private bool _shareProgressWithFriends;

    [ObservableProperty]
    private bool _discoverableByUsername = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSection))]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowBrowseShelves))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogViewToggle))]
    [NotifyPropertyChangedFor(nameof(ShowFlatCatalogList))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogPagination))]
    [NotifyPropertyChangedFor(nameof(IsMoviesSection))]
    [NotifyPropertyChangedFor(nameof(IsAnimeSection))]
    [NotifyPropertyChangedFor(nameof(IsBooksSection))]
    [NotifyPropertyChangedFor(nameof(IsBrowseSection))]
    [NotifyPropertyChangedFor(nameof(IsLiveTvSection))]
    [NotifyPropertyChangedFor(nameof(IsUserListsSection))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogOverviewTools))]
    [NotifyPropertyChangedFor(nameof(ShowNoUserLists))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyUserList))]
    private bool _isUserListsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedUserList))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyUserList))]
    [NotifyPropertyChangedFor(nameof(CanPublishSelectedUserList))]
    private UserMediaListViewModel? _selectedUserList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailUserListTarget))]
    [NotifyPropertyChangedFor(nameof(SelectedItemIsInDetailUserList))]
    [NotifyPropertyChangedFor(nameof(SelectedItemUserListActionText))]
    private UserMediaListViewModel? _selectedDetailUserList;

    [ObservableProperty]
    private string _newUserListName = string.Empty;

    [ObservableProperty]
    private string _renameUserListName = string.Empty;

    [ObservableProperty]
    private bool _isDeleteUserListConfirmationVisible;

    [ObservableProperty]
    private TorrentActivitySnapshot? _activeTorrentActivity;

    public ObservableCollection<CatalogCardViewModel> Items { get; } = [];

    /// <summary>
    /// Home shelves, one per kind. Empty on every other page.
    /// </summary>
    public ObservableCollection<CatalogSectionViewModel> HomeSections { get; } = [];

    /// <summary>
    /// The rows on a media-type page — recently added, recently updated, then
    /// one per genre the library holds enough of. Empty everywhere else.
    /// </summary>
    public ObservableCollection<CatalogSectionViewModel> BrowseShelves { get; } = [];

    public ObservableCollection<ProviderFilterViewModel> ProviderFilters { get; } = [];

    public ObservableCollection<CatalogCategoryFilterViewModel>
        CategoryFilters
    { get; } = [];

    public ObservableCollection<CatalogStatusFilterViewModel>
        StatusFilters
    { get; } = [];

    public ObservableCollection<CatalogYearFilterViewModel>
        YearFilters
    { get; } = [];

    /// <summary>
    /// Search criteria, not a view of what came back: the choices are the
    /// shared genre keys, so a genre can be asked for before any result
    /// mentions it.
    /// </summary>
    public ObservableCollection<CatalogGenreFilterViewModel>
        GenreFilters
    { get; } = [];

    public ObservableCollection<UserMediaListViewModel> UserLists { get; } = [];

    /// <summary>
    /// The lists the open title is already on, so it can be taken off one
    /// without first hunting for it in the picker.
    /// </summary>
    public ObservableCollection<UserMediaListViewModel>
        SelectedItemUserLists
    { get; } = [];

    public bool HasSelectedItemUserLists => SelectedItemUserLists.Count > 0;

    public ObservableCollection<TorrentDownloadItemViewModel> Downloads
    {
        get;
    } = [];

    public ObservableCollection<ContentSourceProviderRowViewModel>
        ContentSourceProviders
    { get; } = [];

    /// <summary>
    /// The same providers arranged by kind for the settings page. Saving still
    /// reads <see cref="ContentSourceProviders"/>; these hold the same rows, so
    /// a switch pressed here is the switch that gets saved.
    /// </summary>
    public ObservableCollection<ContentSourceProviderGroupViewModel>
        ContentSourceProviderGroups
    { get; } = [];

    public ObservableCollection<ApiVideoServerOptionViewModel>
        ActiveVideoServers
    { get; } = [];

    public ObservableCollection<ApiContentProviderOptionViewModel>
        ApiProviderChoices
    { get; } = [];

    /// <summary>
    /// Identifies the entry whose inline provider picker has just appeared.
    /// The view observes this separately from the choices collection so it can
    /// reveal the newly-expanded part of a virtualized entry after layout.
    /// </summary>
    public string? ProviderChoiceTargetStableKey =>
        _providerChoiceTargetStableKey;

    public ObservableCollection<LiveTvChannelViewModel>
        LiveTvChannels
    { get; } = [];

    [ObservableProperty]
    private LiveTvChannelViewModel? _selectedLiveTvChannel;

    public bool IsLiveTvRemoteSelectionActive { get; set; }

    public ObservableCollection<LiveTvFilterOptionViewModel>
        LiveTvProviderFilters
    { get; } = [];

    public ObservableCollection<LiveTvFilterOptionViewModel>
        LiveTvGroupFilters
    { get; } = [];

    public string PlatformName { get; } =
        LocalizePlatformName(runtimeProfile);

    public string TorrentEngineName { get; } = runtimeProfile.TorrentCapabilities.DisplayName;

    public bool SupportsMyAnimeListMetadata { get; } =
        catalogMetadataRefreshService.ProviderNames.Contains(
            "MyAnimeList",
            StringComparer.Ordinal);

    public string MetadataProviderAvailabilityText =>
        SupportsMyAnimeListMetadata
            ? UiTextCatalog.Get("MetadataAvailabilityAll")
            : UiTextCatalog.Get("MetadataAvailabilityNative");

    public string PeerTransport { get; } = runtimeProfile.TorrentCapabilities.PeerTransport switch
    {
        Torrenting.PeerTransport.TcpUdp => "TCP / UDP",
        Torrenting.PeerTransport.WebRtc => "WebRTC",
        _ => UiTextCatalog.Get("Unknown")
    };

    public string PrivacySummary { get; } = runtimeProfile.TorrentCapabilities.SupportsMessageStreamEncryption
        ? UiTextCatalog.Get("PrivacyMse")
        : UiTextCatalog.Get("PrivacyDtls");

    public bool IsTelevision { get; } = runtimeProfile.IsTelevision;

    /// <summary>
    /// True on phones and tablets, where fingers - not a mouse - drive the UI.
    /// </summary>
    public bool IsTouchPrimary { get; } = runtimeProfile.IsTouchPrimary;

    public bool ShowOperationalPanels => !IsTelevision;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool SupportsFullDatabaseBackup => fullDatabaseBackupService is not null;

    public bool ShowFullDatabaseBackup =>
        SupportsFullDatabaseBackup && !IsTelevision;

    public bool SupportsApplicationUpdates =>
        applicationUpdateService.IsAvailable;

    public bool ShowApplicationUpdateSettings =>
        IsSettingsOpen && SupportsApplicationUpdates;

    public bool HasAvailableUpdate => AvailableUpdate is not null;

    public string AvailableUpdateTitle =>
        AvailableUpdate is null
            ? string.Empty
            : UiTextCatalog.Format(
                "UpdateAvailableTitle",
                AvailableUpdate.Version);

    public string AvailableUpdateNotes =>
        string.IsNullOrWhiteSpace(AvailableUpdate?.ReleaseNotes)
            ? UiTextCatalog.Get("UpdateAvailableDefaultNotes")
            : AvailableUpdate.ReleaseNotes!;

    public string AvailableUpdateSize =>
        AvailableUpdate is null
            ? string.Empty
            : UiTextCatalog.Format(
                "UpdateDownloadSize",
                FormatBytes(AvailableUpdate.SizeBytes));

    public double UpdateDownloadProgressPercent =>
        Math.Clamp(UpdateDownloadProgress, 0, 1) * 100;

    public bool SupportsRemoteSync =>
        userSyncStore is not null;

    public bool CanUseRemoteSync =>
        SupportsRemoteSync &&
        IsRemoteSignedIn &&
        !IsRemoteSyncBusy;

    public bool CanRestoreRemoteSnapshot =>
        CanUseRemoteSync && _hasRemoteSnapshot;

    public bool CanPublishSelectedUserList =>
        CanUseRemoteSync && SelectedUserList is not null;

    public string RemoteAccountSummary =>
        IsRemoteSignedIn
            ? UiTextCatalog.Format(
                "RemoteAccountSignedIn",
                RemoteAccountDisplayName,
                RemoteUsername)
            : UiTextCatalog.Get("RemoteAccountSignedOut");

    public string RemoteSnapshotSummary =>
        !_hasRemoteSnapshot ||
        RemoteSnapshotUpdatedAt is null
            ? UiTextCatalog.Get("RemoteSnapshotEmpty")
            : UiTextCatalog.Format(
                "RemoteSnapshotAvailable",
                _remoteSyncRevision,
                RemoteSnapshotUpdatedAt.Value.ToLocalTime()
                    .ToString("g"));

    public bool HasRemoteSharedListUrl =>
        !string.IsNullOrWhiteSpace(RemoteSharedListUrl);

    public bool IsMagnetEngineAvailable => magnetOpenService.IsEngineAvailable;

    public bool HasPendingMagnet => !string.IsNullOrWhiteSpace(PendingMagnetLink);

    public bool HasSelectedProvider =>
        SelectedProviderFilter?.ContributorKey is not null;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public string ShellTitle => IsDownloadsOpen
        ? UiTextCatalog.Get("DownloadsTitle")
        : IsLiveTvOpen
            ? UiTextCatalog.Get("LiveTvTitle")
        : IsSettingsOpen
            ? UiTextCatalog.Get("SettingsTitle")
            : UiTextCatalog.Get("CatalogTitle");

    public string ShellSubtitle => IsDownloadsOpen
        ? UiTextCatalog.Get("DownloadsSubtitle")
        : IsLiveTvOpen
            ? UiTextCatalog.Get("LiveTvSubtitle")
        : IsSettingsOpen
            ? UiTextCatalog.Get("SettingsSubtitle")
            : UiTextCatalog.Get("CatalogSubtitle");

    public string NavigationToggleText => UiTextCatalog.Get(
        IsNavigationCollapsed
            ? "ExpandMenu"
            : "CollapseMenu");

    public string NavigationToggleGlyph =>
        IsNavigationCollapsed ? "›" : "‹";

    public string FavoriteItemsFilterText =>
        (ShowFavoritesOnly ? "\u2605 " : "\u2606 ") +
        UiTextCatalog.Get("FavoriteTitles");

    public bool HasUserLists => UserLists.Count > 0;

    public bool HasSelectedUserList => SelectedUserList is not null;

    public bool HasDetailUserListTarget =>
        SelectedDetails is not null &&
        SelectedDetailUserList is not null;

    public bool SelectedItemIsInDetailUserList =>
        SelectedDetails is { } details &&
        SelectedDetailUserList?.Contains(
            details.Details.Identity.CatalogItemId) is true;

    public string SelectedItemUserListActionText =>
        UiTextCatalog.Get(
            SelectedItemIsInDetailUserList
                ? "RemoveFromList"
                : "AddToList");

    public bool SelectedItemIsFavorite =>
        SelectedDetails is { } details &&
        _favoriteCatalogItemIds.Contains(
            details.Details.Item.Id);

    public string SelectedItemFavoriteActionText =>
        UiTextCatalog.Get(
            SelectedItemIsFavorite
                ? "RemoveFavoriteItem"
                : "AddFavoriteItem");

    public string SelectedItemFavoriteGlyph =>
        SelectedItemIsFavorite ? "\u2605" : "\u2606";

    public string PendingMagnetDisplay =>
        MagnetLink.TryParse(PendingMagnetLink, out var magnet)
            ? magnet!.CatalogIdentityUri + " · " +
              UiTextCatalog.Get("TrackerDetailsHidden")
            : UiTextCatalog.Get("MagnetDetailsHidden");

    /// <summary>
    /// Settings covers the page rather than drawing over a live copy of it:
    /// the selected title is kept so closing returns to it, but the page
    /// itself stands down. Leaving it mounted put a second focusable surface
    /// behind the scrim and let background refreshes rebuild it unseen.
    /// </summary>
    public bool IsDetailsOpen =>
        SelectedDetails is not null &&
        !IsSettingsOpen &&
        Reader is null && EpubReader is null && !IsEmbeddedMediaOpen;

    /// <summary>
    /// Whether the shelves and the grid belong on screen.
    /// <para>
    /// Friends is excluded because it is a page in its own right, not a filter
    /// over the catalog the way lists are. Left in, the whole library rendered
    /// underneath the friend list.
    /// </para>
    /// </summary>
    public bool IsCatalogOpen =>
        !IsSettingsOpen &&
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        !IsFriendsOpen &&
        SelectedDetails is null &&
        Reader is null &&
        EpubReader is null &&
        !IsEmbeddedMediaOpen;

    public bool IsCatalogOrSettingsOpen =>
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        (IsSettingsOpen ||
         (SelectedDetails is null &&
          Reader is null &&
          EpubReader is null &&
          !IsEmbeddedMediaOpen));

    public bool ShowSettingsPanels =>
        IsSettingsOpen;

    public bool ShowSettingsFileActions =>
        IsSettingsOpen && ShowOperationalPanels;

    public bool ShowSettingsBackup =>
        IsSettingsOpen && ShowFullDatabaseBackup;

    public bool ShowRemoteSyncSettings =>
        IsSettingsOpen && SupportsRemoteSync;

    public bool ShowCatalogOverviewTools =>
        IsCatalogOpen && !IsUserListsOpen;

    public bool IsAllSection =>
        !IsSettingsOpen &&
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        !IsUserListsOpen &&
        !IsFriendsOpen &&
        _selectedMediaType is null;

    /// <summary>
    /// Whether friends can be reached at all: a signed-in session on a server
    /// carrying the feature, and not a television. The remote is a poor way to
    /// type a name, and progress sharing is a decision better made where the
    /// keyboard is.
    /// </summary>
    public bool SupportsFriends =>
        !IsTelevision && _friendService.IsAvailable;

    /// <summary>
    /// Whether the friends navigation entry shows at all. A television hides
    /// friend management but still joins watch parties — a six-character code
    /// is the kind of typing a remote can do — so the entry appears there
    /// whenever a signed-in server carries parties.
    /// </summary>
    public bool ShowFriendsNav =>
        SupportsFriends || SupportsWatchParties;

    /// <summary>
    /// What the friends entry calls itself. On a television the page holds
    /// only the watch party, so naming it "Friends" would promise a page that
    /// is not there.
    /// </summary>
    public string FriendsNavLabel =>
        UiTextCatalog.Get(
            SupportsFriends ? "NavFriends" : "WatchPartyTitle");

    public bool IsFriendsSection =>
        !IsSettingsOpen &&
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        IsFriendsOpen;

    public ObservableCollection<FriendViewModel> Friends { get; } = [];

    public ObservableCollection<FriendRequestViewModel> FriendRequests
    {
        get;
    } = [];

    /// <summary>
    /// How far each friend has reached in the open title.
    /// </summary>
    public ObservableCollection<FriendProgressViewModel> DetailFriendProgress
    {
        get;
    } = [];

    public bool ShowDetailFriendProgress =>
        IsDetailsOpen && DetailFriendProgress.Count > 0;

    public bool HasFriends => Friends.Count > 0;

    public bool HasFriendRequests => FriendRequests.Count > 0;

    public bool ShowNoFriends =>
        IsFriendsOpen &&
        SupportsFriends &&
        !IsFriendsBusy &&
        Friends.Count == 0 &&
        FriendRequests.Count == 0;

    /// <summary>
    /// Republishes everything derived from the two collections. They are
    /// rebuilt rather than mutated in place, and a collection change is not a
    /// property change, so nothing bound to their emptiness updates on its own.
    /// </summary>
    private void NotifyFriendCollectionsChanged()
    {
        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(HasFriendRequests));
        OnPropertyChanged(nameof(ShowNoFriends));
    }

    /// <summary>
    /// Home groups its cards into shelves; a search replaces them with one
    /// ranked list, because relevance beats the shelf a title belongs to.
    /// </summary>
    public bool ShowHomeSections =>
        IsCatalogOpen &&
        IsAllSection &&
        !IsSearchActive &&
        HomeSections.Count > 0;

    /// <summary>
    /// Movies, anime and books share one layout: rows, the way home reads.
    /// </summary>
    public bool IsBrowseSection =>
        IsMoviesSection || IsAnimeSection || IsBooksSection;

    /// <summary>
    /// A media-type page is rows by default and a grid on request. The grid is
    /// still the only view that pages through the whole library, so it stays
    /// one press away rather than being replaced.
    /// </summary>
    public bool ShowBrowseShelves =>
        IsCatalogOpen &&
        IsBrowseSection &&
        !IsSearchActive &&
        !PrefersCatalogGrid &&
        BrowseShelves.Count > 0;

    public bool ShowCatalogViewToggle =>
        IsCatalogOpen && IsBrowseSection && !IsSearchActive;

    public string CatalogViewToggleText =>
        UiTextCatalog.Get(PrefersCatalogGrid
            ? "CatalogViewRows"
            : "CatalogViewGrid");

    public bool ShowFlatCatalogList =>
        IsCatalogOpen && !ShowHomeSections && !ShowBrowseShelves;

    public int CatalogPageSize => CatalogPagination.DefaultPageSize;

    public bool CanGoPreviousCatalogPage => CatalogPage > 1;

    public bool CanGoNextCatalogPage => CatalogPage < CatalogPageCount;

    public bool ShowCatalogPagination =>
        ShowFlatCatalogList && CatalogPageCount > 1;

    public string CatalogPageSummary => UiTextCatalog.Format(
        "CatalogPageSummary",
        CatalogPage,
        CatalogPageCount,
        CatalogResultCount);

    public bool IsMoviesSection =>
        !IsSettingsOpen &&
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        !IsUserListsOpen &&
        !IsFriendsOpen &&
        _selectedMediaType is MediaType.Movie &&
        _selectedSubtypeGroup is MediaSubtypeGroup.LiveAction;

    public bool IsAnimeSection =>
        !IsSettingsOpen &&
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        !IsUserListsOpen &&
        !IsFriendsOpen &&
        _selectedMediaType is MediaType.Movie &&
        _selectedSubtypeGroup is MediaSubtypeGroup.Anime;

    public bool IsBooksSection =>
        !IsSettingsOpen &&
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        !IsUserListsOpen &&
        !IsFriendsOpen &&
        _selectedMediaType is MediaType.Book;

    public bool IsLiveTvSection => IsLiveTvOpen;

    public bool IsUserListsSection =>
        !IsSettingsOpen &&
        !IsDownloadsOpen &&
        !IsLiveTvOpen &&
        !IsFriendsOpen &&
        IsUserListsOpen;

    public bool IsDownloadsSection => IsDownloadsOpen;

    public bool IsLiveTvPageVisible =>
        IsLiveTvOpen && !IsEmbeddedMediaOpen;

    public bool HasLiveTvChannels => LiveTvChannels.Count > 0;

    public bool ShowNoLiveTvChannels =>
        IsLiveTvPageVisible && !IsBusy && !HasLiveTvChannels;

    public bool HasLiveTvFailures =>
        !string.IsNullOrWhiteSpace(LiveTvFailureSummary);

    public string LiveTvChannelSummary =>
        UiTextCatalog.Format(
            "LiveTvChannelSummary",
            LiveTvChannels.Count,
            LiveTvTotalChannelCount,
            LiveTvLoadedProviderCount);

    public bool HasDownloads => Downloads.Count > 0;

    public bool HasSelectedDownloads =>
        Downloads.Any(static download => download.IsSelected);

    public int DownloadCount => Downloads.Count;

    public int ActiveDownloadCount =>
        Downloads.Count(static download => download.IsActive);

    public int SelectedDownloadCount =>
        Downloads.Count(static download => download.IsSelected);

    public bool AllDownloadsSelected =>
        Downloads.Count > 0 &&
        Downloads.All(static download => download.IsSelected);

    public string DownloadStorageSummary
    {
        get
        {
            var storedBytes = torrentDownloadLibrary.Records
                .GroupBy(
                    static record =>
                        record.StorageKey ?? record.Id.ToString("D"),
                    StringComparer.Ordinal)
                .Sum(static group =>
                    group.Max(static record => record.StoredBytes));
            return UiTextCatalog.Format(
                "DownloadsStorageSummary",
                FormatBytes(storedBytes));
        }
    }

    public string DownloadSelectionSummary =>
        UiTextCatalog.Format(
            "DownloadsSelectionSummary",
            SelectedDownloadCount,
            DownloadCount);

    public string DownloadDeleteConfirmationText =>
        UiTextCatalog.Format(
            "DownloadsDeleteConfirmation",
            _pendingDownloadDeletion.Count);

    public bool ShowNoUserLists =>
        IsUserListsSection && !HasUserLists;

    public bool ShowEmptyUserList =>
        IsUserListsSection &&
        SelectedUserList is not null &&
        Items.Count == 0;

    public bool IsReaderOpen => Reader is not null && !IsEmbeddedMediaOpen;

    public bool IsEpubReaderOpen => EpubReader is not null && !IsEmbeddedMediaOpen;

    public bool HasReaderChapterNavigation =>
        Reader is not null && _readerChapterNavigation is not null;

    public string ReaderChapterLabel => _readerChapterNavigation is { } navigation
        ? string.Format(UiTextCatalog.Get("ReaderChapterShort"), navigation.Chapters[navigation.CurrentIndex].DisplayNumber)
        : Reader?.Title ?? UiTextCatalog.Get("BackToManga");

    public bool CanOpenPreviousReaderChapter =>
        HasReaderChapterNavigation &&
        _readerChapterNavigation!.CurrentIndex > 0;

    public bool CanOpenNextReaderChapter =>
        HasReaderChapterNavigation &&
        _readerChapterNavigation!.CurrentIndex <
        _readerChapterNavigation.Chapters.Count - 1;

    /// <summary>
    /// Whether the player can step between episodes of the open title, so a
    /// series is watched without returning to the details page between each
    /// one.
    /// </summary>
    public bool HasPlayerEpisodeNavigation =>
        IsEmbeddedMediaOpen && _playerEpisodeNavigation is not null;

    public bool CanOpenPreviousEpisode =>
        HasPlayerEpisodeNavigation &&
        _playerEpisodeNavigation!.CurrentIndex > 0;

    public bool CanOpenNextEpisode =>
        HasPlayerEpisodeNavigation &&
        _playerEpisodeNavigation!.CurrentIndex <
        _playerEpisodeNavigation.Chapters.Count - 1;

    /// <summary>
    /// Whether <see cref="NavigateBackAsync"/> would handle a back gesture.
    ///
    /// Also the answer for callers that have to accept or decline the gesture
    /// synchronously and act afterwards, rather than awaiting the navigation. Every
    /// branch of NavigateBackAsync decides on state already known, so this can answer
    /// for it.
    ///
    /// Which makes the two a pair: a branch added there and not here is a back gesture
    /// that quietly closes the app instead of doing what it was meant to.
    /// </summary>
    public bool CanNavigateBack =>
        IsDonationPromptVisible ||
        IsEmbeddedMediaOpen ||
        Reader is not null ||
        EpubReader is not null ||
        SelectedDetails is not null ||
        IsSettingsOpen ||
        IsDownloadsOpen ||
        IsLiveTvOpen;

    public bool IsMediaPlaybackAvailable => platformMediaPlayer.IsAvailable;

    public bool HasActiveVideoServers => ActiveVideoServers.Count > 0;

    public bool HasActiveTorrentActivity =>
        ActiveTorrentActivity is not null;

    public bool IsTorrentProgressIndeterminate =>
        ActiveTorrentActivity?.IsIndeterminate is not false;

    public double TorrentProgressPercent =>
        Math.Clamp(ActiveTorrentActivity?.Progress ?? 0, 0, 1) * 100;

    public bool CanPauseActiveTorrent =>
        ActiveTorrentActivity?.CanPause is true;

    public bool CanResumeActiveTorrent =>
        ActiveTorrentActivity?.CanResume is true;

    public bool CanCancelActiveTorrent =>
        ActiveTorrentActivity?.CanCancel is true;

    public string TorrentActivityPhaseText =>
        UiTextCatalog.Get(ActiveTorrentActivity?.Phase switch
        {
            TorrentActivityPhase.Adding => "TorrentActivityAdding",
            TorrentActivityPhase.FetchingMetadata =>
                "TorrentActivityMetadata",
            TorrentActivityPhase.CheckingFiles =>
                "TorrentActivityChecking",
            TorrentActivityPhase.FindingPeers =>
                "TorrentActivityFindingPeers",
            TorrentActivityPhase.Downloading =>
                "TorrentActivityDownloading",
            TorrentActivityPhase.Ready => "TorrentActivityReady",
            TorrentActivityPhase.Seeding => "TorrentActivitySeeding",
            TorrentActivityPhase.Paused => "TorrentActivityPaused",
            TorrentActivityPhase.Failed => "TorrentActivityFailed",
            TorrentActivityPhase.Removing => "TorrentActivityRemoving",
            _ => "TorrentActivityAdding"
        });

    public string TorrentActivityProgressText
    {
        get
        {
            var activity = ActiveTorrentActivity;
            if (activity is null)
            {
                return string.Empty;
            }

            if (activity.Progress is { } progress)
            {
                var percentage = Math.Clamp(progress, 0, 1) * 100;
                if (activity.WantedDownloadedBytes is { } downloaded &&
                    activity.WantedBytes is { } total)
                {
                    return UiTextCatalog.Format(
                        "TorrentActivityProgressWithSize",
                        percentage,
                        FormatBytes(downloaded),
                        FormatBytes(total));
                }

                return UiTextCatalog.Format(
                    "TorrentActivityProgress",
                    percentage);
            }

            return TorrentActivityPhaseText;
        }
    }

    public string TorrentActivityMetricsText
    {
        get
        {
            var activity = ActiveTorrentActivity;
            if (activity is null)
            {
                return string.Empty;
            }

            var parts = new List<string>(3);
            if (activity.DownloadBytesPerSecond > 0)
            {
                parts.Add(
                    UiTextCatalog.Format(
                        "TorrentActivitySpeed",
                        FormatBytes(activity.DownloadBytesPerSecond)));
            }

            parts.Add(
                UiTextCatalog.Format(
                    "TorrentActivityPeers",
                    activity.ConnectedPeers));
            if (activity.EstimatedTimeRemaining is { } remaining &&
                remaining >= TimeSpan.Zero &&
                remaining < TimeSpan.FromDays(1))
            {
                parts.Add(
                    UiTextCatalog.Format(
                        "TorrentActivityEta",
                        FormatDuration(remaining)));
            }

            return string.Join(" | ", parts);
        }
    }

    public string TorrentActivityDetailText =>
        ActiveTorrentActivity?.Detail ??
        ActiveTorrentActivity?.TorrentName ??
        string.Empty;

    public Control? EmbeddedMediaView => platformMediaPlayer.EmbeddedView;

    public bool CanActivateWebPlayerCenter =>
        platformMediaPlayer is WebViewPlatformMediaPlayer { CanActivateRemoteCenter: true };

    public bool TryActivateWebPlayerCenter() =>
        platformMediaPlayer is WebViewPlatformMediaPlayer player &&
        player.TryActivateRemoteCenter();

    public string PlayerFullscreenActionText =>
        UiTextCatalog.Get(
            IsPlayerFullscreen
                ? "PlayerExitFullscreen"
                : "PlayerEnterFullscreen");

    public string MagnetEngineStatus => magnetOpenService.IsEngineAvailable
        ? UiTextCatalog.Format(
            "TorrentAdapterConnected",
            TorrentEngineName)
        : UiTextCatalog.Format(
            "TorrentAdapterDisconnected",
            TorrentEngineName);

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        if (!string.IsNullOrWhiteSpace(value))
        {
            ApplicationDiagnosticLog.Error("ui.error", value);
        }
    }

    private void OnStatusMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasStatus));
        var generation = ++_statusGeneration;
        if (!string.IsNullOrWhiteSpace(value))
        {
            _ = ExpireStatusAsync(generation);
        }
    }

    partial void OnActiveTorrentActivityChanged(
        TorrentActivitySnapshot? value)
    {
        OnPropertyChanged(nameof(HasActiveTorrentActivity));
        OnPropertyChanged(nameof(IsTorrentProgressIndeterminate));
        OnPropertyChanged(nameof(TorrentProgressPercent));
        OnPropertyChanged(nameof(CanPauseActiveTorrent));
        OnPropertyChanged(nameof(CanResumeActiveTorrent));
        OnPropertyChanged(nameof(CanCancelActiveTorrent));
        OnPropertyChanged(nameof(TorrentActivityPhaseText));
        OnPropertyChanged(nameof(TorrentActivityProgressText));
        OnPropertyChanged(nameof(TorrentActivityMetricsText));
        OnPropertyChanged(nameof(TorrentActivityDetailText));
    }

    partial void OnPendingMagnetLinkChanged(string? value) => OnPropertyChanged(nameof(HasPendingMagnet));

    partial void OnSelectedProviderFilterChanged(ProviderFilterViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedProvider));
        NotifyCatalogFilterSelectionChanged();
        if (!_isRebuildingProviderFilters &&
            !_isRebuildingCatalogFilters)
        {
            ApplyCatalogItems(resetPage: true);
        }
    }

    partial void OnSelectedCategoryFilterChanged(
        CatalogCategoryFilterViewModel? value)
    {
        NotifyCatalogFilterSelectionChanged();
        if (!_isRebuildingCatalogFilters)
        {
            ApplyCatalogItems(resetPage: true);
        }
    }

    partial void OnSelectedStatusFilterChanged(
        CatalogStatusFilterViewModel? value)
    {
        NotifyCatalogFilterSelectionChanged();
        if (!_isRebuildingCatalogFilters)
        {
            ApplyCatalogItems(resetPage: true);
        }
    }

    partial void OnHideAdultContentChanged(bool value)
    {
        if (value &&
            SelectedDetails?.Details.Item is { } item &&
            AdultContentCategoryFilter.Matches(
                item.Categories,
                item.Genres))
        {
            SelectedDetails = null;
        }

        if (ContentSourceProviders.Count == 0)
        {
            return;
        }

        RebuildProviderFilters(
            SelectedProviderFilter?.ContributorKey,
            resetPage: true);
    }

    /// <summary>
    /// The pickers that narrow results rather than send a search still decide
    /// whether the clear action has anything to offer.
    /// </summary>
    private void NotifyCatalogFilterSelectionChanged()
    {
        OnPropertyChanged(nameof(HasCatalogFilters));
        OnPropertyChanged(nameof(HasAnyCatalogSelection));
    }

    partial void OnSelectedYearFilterChanged(
        CatalogYearFilterViewModel? value)
    {
        if (!_isRebuildingCatalogFilters)
        {
            ApplySearchCriteriaChange();
        }
    }

    partial void OnSelectedGenreFilterChanged(
        CatalogGenreFilterViewModel? value)
    {
        if (!_isRebuildingCatalogFilters)
        {
            ApplySearchCriteriaChange();
        }
    }

    /// <summary>
    /// Genre and year are search criteria, so changing one asks the sources
    /// again instead of narrowing what already came back — which is the only
    /// way to reach a title no earlier search returned. Once nothing is left
    /// to search for, the browse grid comes back.
    /// </summary>
    private void ApplySearchCriteriaChange()
    {
        OnPropertyChanged(nameof(HasSearchCriteria));
        OnPropertyChanged(nameof(HasAnyCatalogSelection));
        if (HasSearchCriteria)
        {
            // Execute deliberately skips CanExecute, because a criteria change
            // must supersede the run in flight rather than be dropped while it
            // finishes. Overlapping bodies are made harmless by the generation
            // check in SearchCatalogAsync: only the newest run publishes.
            SearchCatalogCommand.Execute(null);
            return;
        }

        if (IsSearchActive)
        {
            ClearSearch();
            return;
        }

        ApplyCatalogItems(resetPage: true);
    }

    partial void OnShowFavoriteTitlesOnlyChanged(bool value) =>
        ApplyCatalogItems(resetPage: true);

    partial void OnSelectedCatalogListFilterChanged(
        UserMediaListViewModel? value)
    {
        if (!_isRebuildingCatalogFilters)
        {
            ApplyCatalogItems(resetPage: true);
        }
    }

    partial void OnShowFavoritesOnlyChanged(bool value)
    {
        if (_isRebuildingCatalogFilters)
        {
            return;
        }

        // Show what is already here straight away, then fill in the favourites
        // this section never loaded. Waiting for the library before redrawing
        // would make the toggle feel like it had missed the press.
        ApplyCatalogItems(resetPage: true);

        // Only the whole-library page answers from the whole library, so only
        // it needs the whole library fetched. A media-type page already holds
        // every card it can show, and pulling the rest would be a second load
        // of everything to display none of it.
        if (value && IsAllSection)
        {
            _ = LoadAllCatalogCardsAsync();
        }
    }

    /// <summary>
    /// Fetches the whole library once, so favourites can be answered without
    /// regard to which section is open. Runs at most once per catalog load.
    /// </summary>
    private Task LoadAllCatalogCardsAsync()
    {
        if (_allCatalogCards.Count > 0)
        {
            return Task.CompletedTask;
        }

        return _allCatalogCardsLoad ??= LoadAllCatalogCardsCoreAsync();
    }

    private async Task LoadAllCatalogCardsCoreAsync()
    {
        try
        {
            var overview = await OffUiThread.ReadAsync(
                () => catalogBrowseService.GetOverviewAsync());
            _allCatalogCards = overview.Items
                .Where(card => !ShouldHideAdultContent(card))
                .ToArray();
            if (ShowFavoritesOnly)
            {
                ApplyCatalogItems(resetPage: true);
            }
        }
        catch (Exception exception)
        {
            // The section's own favourites are already on screen, so this cost
            // the titles from elsewhere rather than the view.
            ApplicationDiagnosticLog.Warning(
                "catalog.favorites-load-failed",
                exception.Message);
        }
        finally
        {
            _allCatalogCardsLoad = null;
        }
    }

    /// <summary>
    /// The two views are built from the same filtered cards but only one of
    /// them is populated at a time, so switching has to rebuild.
    /// </summary>
    partial void OnPrefersCatalogGridChanged(bool value) =>
        ApplyCatalogItems(resetPage: true);

    /// <summary>
    /// Closing settings rebuilds the page it covered. An import or a sync
    /// landing while the layer was up runs the flat-grid path — settings is
    /// not a catalog section — which empties the shelves, and nothing else
    /// puts them back, so home came back as a bare paged grid.
    /// </summary>
    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (!value)
        {
            ApplyCatalogItems();
        }
    }

    partial void OnLiveTvSearchQueryChanged(string value) =>
        ApplyLiveTvFilters();

    partial void OnSelectedLiveTvProviderFilterChanged(
        LiveTvFilterOptionViewModel? value)
    {
        if (!_isRebuildingLiveTvFilters)
        {
            ApplyLiveTvFilters();
        }
    }

    partial void OnSelectedLiveTvGroupFilterChanged(
        LiveTvFilterOptionViewModel? value)
    {
        if (!_isRebuildingLiveTvFilters)
        {
            ApplyLiveTvFilters();
        }
    }

    partial void OnSelectedUserListChanged(UserMediaListViewModel? value)
    {
        RenameUserListName = value?.Name ?? string.Empty;
        IsDeleteUserListConfirmationVisible = false;
        if (_lastPublishedSharedList?.ListId != value?.Id)
        {
            _lastPublishedSharedList = null;
            RemoteSharedListUrl = string.Empty;
        }

        if (IsUserListsOpen)
        {
            SectionTitle = value?.Name ?? UiTextCatalog.Get("UserListsTitle");
            ApplyCatalogItems(resetPage: true);
        }
    }

    partial void OnSelectedDetailUserListChanged(
        UserMediaListViewModel? value)
    {
        OnPropertyChanged(nameof(HasDetailUserListTarget));
        OnPropertyChanged(nameof(SelectedItemIsInDetailUserList));
        OnPropertyChanged(nameof(SelectedItemUserListActionText));
        RefreshSelectedItemUserLists();
    }

    partial void OnSelectedDetailsChanged(CatalogDetailsViewModel? value)
    {
        DetailsFeedback.Message = null;
        IsCatalogItemDeleteConfirmationVisible = false;
        if (value is not null)
        {
            SelectedDetailUserList =
                IsUserListsOpen && SelectedUserList is not null
                    ? SelectedUserList
                    : SelectedDetailUserList ?? UserLists.FirstOrDefault();
        }
        else
        {
            CancelApiUnitLookup();
            ClearApiProviderChoices();
        }

        OnPropertyChanged(nameof(IsDetailsOpen));
        OnPropertyChanged(nameof(IsCatalogOpen));
        OnPropertyChanged(nameof(IsCatalogOrSettingsOpen));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(ShowCatalogOverviewTools));
        OnPropertyChanged(nameof(HasDetailUserListTarget));
        OnPropertyChanged(nameof(SelectedItemIsInDetailUserList));
        OnPropertyChanged(nameof(SelectedItemUserListActionText));
        RefreshSelectedItemUserLists();
        OnPropertyChanged(nameof(SelectedItemIsFavorite));
        OnPropertyChanged(nameof(SelectedItemFavoriteActionText));
        OnPropertyChanged(nameof(SelectedItemFavoriteGlyph));
        _ = LoadDetailFriendProgressAsync(value);
    }

    /// <summary>
    /// Fills the friend strip for the open title.
    /// <para>
    /// Deliberately not awaited by the caller: the page is worth showing before
    /// the answer arrives, and a server that is slow or gone must not hold up
    /// the title. A generation counter drops a reply that lands after the
    /// viewer has moved on, so one title never shows another's progress.
    /// </para>
    /// </summary>
    private async Task LoadDetailFriendProgressAsync(
        CatalogDetailsViewModel? details)
    {
        var generation = ++_detailFriendProgressGeneration;
        if (DetailFriendProgress.Count > 0)
        {
            DetailFriendProgress.Clear();
            OnPropertyChanged(nameof(ShowDetailFriendProgress));
        }

        if (details is null || !SupportsFriends)
        {
            return;
        }

        try
        {
            var progress = await _friendService.GetProgressAsync(
                details.Details.Item.MediaType,
                details.Details.Item.Name);
            if (generation != _detailFriendProgressGeneration)
            {
                return;
            }

            foreach (var entry in progress
                         .OrderByDescending(static value => value.UpdatedAt))
            {
                DetailFriendProgress.Add(new FriendProgressViewModel(entry));
            }

            OnPropertyChanged(nameof(ShowDetailFriendProgress));
        }
        catch (Exception exception)
        {
            // A friend strip that cannot load is not worth an error banner over
            // the title the viewer actually asked for.
            ApplicationDiagnosticLog.Warning(
                "friends.progress-unavailable",
                exception.Message);
        }
    }

    partial void OnReaderChanged(ImageReaderViewModel? value)
    {
        IsReaderSettingsOpen = false;
        OnPropertyChanged(nameof(IsReaderOpen));
        OnPropertyChanged(nameof(IsDetailsOpen));
        OnPropertyChanged(nameof(IsCatalogOpen));
        OnPropertyChanged(nameof(IsCatalogOrSettingsOpen));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(ShowCatalogOverviewTools));
        NotifyReaderChapterNavigationChanged();
    }

    partial void OnEpubReaderChanged(EpubReaderViewModel? value)
    {
        OnPropertyChanged(nameof(IsEpubReaderOpen));
        OnPropertyChanged(nameof(IsDetailsOpen));
        OnPropertyChanged(nameof(IsCatalogOpen));
        OnPropertyChanged(nameof(IsCatalogOrSettingsOpen));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(ShowCatalogOverviewTools));
    }

    partial void OnIsEmbeddedMediaOpenChanged(bool value)
    {
        if (!value && IsPlayerFullscreen)
        {
            IsPlayerFullscreen = false;
        }

        OnPropertyChanged(nameof(IsDetailsOpen));
        OnPropertyChanged(nameof(IsCatalogOpen));
        OnPropertyChanged(nameof(IsCatalogOrSettingsOpen));
        OnPropertyChanged(nameof(IsReaderOpen));
        OnPropertyChanged(nameof(IsEpubReaderOpen));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(ShowCatalogOverviewTools));
        OnPropertyChanged(nameof(IsLiveTvPageVisible));
        OnPropertyChanged(nameof(ShowNoLiveTvChannels));
        // The stepper is deliberately not cleared here. Switching video server
        // closes and reopens playback, and discarding it on the way through
        // left the buttons gone until the next episode was resolved. Nothing
        // reads it while the player is shut, and opening always rewrites it.
        NotifyPlayerEpisodeNavigationChanged();
    }

    partial void OnIsPlayerFullscreenChanged(bool value)
    {
        if (!value &&
            platformMediaPlayer is IPlatformMediaPlayerTransitions transitions)
        {
            transitions.RequestEmbeddedFullscreenExit();
        }
    }

    [ObservableProperty]
    private bool _isReaderSettingsOpen;

    public async Task<bool> NavigateBackAsync()
    {
        if (IsDonationPromptVisible)
        {
            DismissDonationPrompt();
            return true;
        }

        if (IsEmbeddedMediaOpen && IsPlayerFullscreen)
        {
            IsPlayerFullscreen = false;
            return true;
        }

        if (IsEmbeddedMediaOpen)
        {
            await CloseActivePlaybackAsync();
            return true;
        }

        // Settings is the topmost surface whenever it is up, so it answers
        // Back first. Tested after the details check it dismissed the title
        // underneath instead — the one thing keeping the title was meant to
        // prevent — and left the layer itself on screen. It has to stay ahead
        // of the reader check too, now that settings opens over an open
        // chapter instead of closing it: Back would otherwise shut the chapter
        // behind the layer and leave the layer up.
        if (IsSettingsOpen)
        {
            IsSettingsOpen = false;
            return true;
        }

        if (IsReaderSettingsOpen)
        {
            IsReaderSettingsOpen = false;
            return true;
        }

        if (Reader is not null || EpubReader is not null)
        {
            CloseReaders();
            return true;
        }

        if (SelectedDetails is not null)
        {
            // Back must invalidate provider progress just like the on-screen
            // close action. Otherwise a late reply reopens the title after the
            // viewer has already returned to the catalog or menu.
            CancelDetailsLoad();
            SelectedDetails = null;
            await ReloadCatalogUserStateAsync();
            ApplyCatalogItems();
            return true;
        }

        if (IsDownloadsOpen)
        {
            IsDownloadsOpen = false;
            return true;
        }

        if (IsLiveTvOpen)
        {
            await LoadHomeAsync();
            return true;
        }

        return false;
    }

    public Task? ShutdownTask
    {
        get
        {
            lock (_shutdownGate)
            {
                return _shutdownTask;
            }
        }
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownGate)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _watchPartyService.StateChanged += OnWatchPartyStateChanged;
        if (platformMediaPlayer is WebViewPlatformMediaPlayer embeddedPlayer)
        {
            embeddedPlayer.RemoteActivationAllowed = () =>
                IsTelevision && IsEmbeddedMediaOpen &&
                !IsDonationPromptVisible && !IsUpdatePromptVisible;
            // Re-read at injection time, not here: the party may pause while
            // the page is still loading.
            embeddedPlayer.ShouldStartEmbeddedPlaybackPaused =
                () => _watchPartyWantsPausedStart;
        }

        magnetOpenService.ManifestImportCompleted +=
            OnManifestImportCompleted;
        torrentActivityCoordinator.ActivityChanged +=
            OnTorrentActivityChanged;
        torrentDownloadLibrary.RecordChanged +=
            OnTorrentDownloadRecordChanged;
        torrentDownloadLibrary.PersistenceFailed +=
            OnTorrentDownloadPersistenceFailed;
        if (platformMediaPlayer is IPlatformMediaPlayerTransitions transitions)
        {
            transitions.PlaybackSurfaceChanged +=
                OnPlaybackSurfaceChanged;
            transitions.NativePlaybackStarted +=
                OnNativePlaybackStarted;
            transitions.EmbeddedFullscreenChanged +=
                OnEmbeddedFullscreenChanged;
            transitions.EmbeddedFullscreenExitRequested +=
                OnEmbeddedFullscreenExitRequested;
        }

        foreach (var activity in torrentActivityCoordinator.Activities)
        {
            _torrentActivities[activity.Id] = activity;
        }
        SelectActiveTorrentActivity();
        Exception? catalogUserStateLoadError = null;
        Exception? userListsLoadError = null;
        Exception? downloadsLoadError = null;
        if (contentFilterCoordinator is { } filters)
        {
            try
            {
                // Adopts the stored preference and the ruleset compiled last
                // time. Nothing is downloaded here; the refresh waits for a
                // moment that is not competing with the first catalog load.
                await Task.Run(() => filters.InitializeAsync());
                LoadContentFilterSettings();
                filters.ScheduleStartupRefresh();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    $"Content filtering failed to initialize: {exception.Message}");
            }
        }

        try
        {
            await torrentDownloadLibrary.InitializeAsync();
            var downloadPreferences =
                await torrentDownloadLibrary.LoadPreferencesAsync();
            DeleteTorrentDataWhenSessionEnds =
                downloadPreferences.SessionEndBehavior is
                    TorrentSessionEndBehavior.DeleteDownloads;
            RefreshDownloads();
        }
        catch (Exception exception)
        {
            downloadsLoadError = exception;
        }

        // Before any per-person state is read, so it is read for the right
        // person. Everything below this line belongs to a profile.
        await LoadProfilesAsync();

        var catalogUserStateLoaded = false;
        try
        {
            await ReloadCatalogUserStateAsync();
            catalogUserStateLoaded = true;
        }
        catch (Exception exception)
        {
            catalogUserStateLoadError = exception;
        }

        try
        {
            await ReloadUserListsAsync();
        }
        catch (Exception exception)
        {
            userListsLoadError = exception;
        }

        // Provider settings must be available before Home starts its
        // background discovery pass. Starting both together could race the
        // persisted enabled-provider list on slower Android TV storage.
        await InitializeContentSourceSettingsAsync();

        // The state was just read above and nothing between here and there
        // consumes or invalidates it, so Home does not read it a second time.
        // If that read failed, Home still retries it exactly as it used to.
        await LoadHomeAsync(reloadUserState: !catalogUserStateLoaded);
        if (userListsLoadError is not null && !HasError)
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "UserListsLoadFailed",
                userListsLoadError.Message));
        }
        else if (catalogUserStateLoadError is not null && !HasError)
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "CatalogUserStateLoadFailed",
                catalogUserStateLoadError.Message));
        }
        else if (downloadsLoadError is not null && !HasError)
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "DownloadsLoadFailed",
                downloadsLoadError.Message));
        }

        try
        {
            _readerDevicePreferences =
                await UserMediaState.LoadReaderDevicePreferencesAsync();
        }
        catch
        {
            // Defaults are fine when the preference cannot be read.
        }

        await InitializeMetadataSettingsAsync();
        await InitializeRemoteServerSettingsAsync();
        await InitializeMagnetIntakeAsync();

        if (catalogRecoveryStatus?.LastRecovery is { } recovery)
        {
            StatusMessage = UiTextCatalog.Format(
                "CatalogRecovered",
                recovery.RecoveryDirectory);
        }

        if (SupportsApplicationUpdates)
        {
            _ = CheckForUpdatesCoreAsync(silent: true);
        }
    }

    public Task ReceiveExternalMagnetAsync(string magnetUri) =>
        HandleMagnetAsync(magnetUri, userConfirmed: false);

    /// <summary>
    /// How long shutdown waits for one cancelled background task. Cancellation
    /// is requested first, but a provider request already in flight does not
    /// always stop at once, and the desktop update helper gives up on a process
    /// that will not exit — silently skipping the update. Abandoning the wait is
    /// safe: the task holds no unsaved state, and the process is going away.
    /// </summary>
    private static readonly TimeSpan BackgroundTaskShutdownTimeout =
        TimeSpan.FromSeconds(20);

    private static async Task AwaitBackgroundTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(BackgroundTaskShutdownTimeout);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            ApplicationDiagnosticLog.Warning(
                "shutdown.background-task",
                "A cancelled background task did not finish within " +
                $"{BackgroundTaskShutdownTimeout.TotalSeconds:F0}s and was " +
                "left to end with the process.");
        }
    }

    private async Task ShutdownCoreAsync()
    {
        magnetOpenService.ManifestImportCompleted -=
            OnManifestImportCompleted;
        torrentActivityCoordinator.ActivityChanged -=
            OnTorrentActivityChanged;
        torrentDownloadLibrary.RecordChanged -=
            OnTorrentDownloadRecordChanged;
        torrentDownloadLibrary.PersistenceFailed -=
            OnTorrentDownloadPersistenceFailed;
        if (platformMediaPlayer is IPlatformMediaPlayerTransitions transitions)
        {
            transitions.PlaybackSurfaceChanged -=
                OnPlaybackSurfaceChanged;
            transitions.NativePlaybackStarted -=
                OnNativePlaybackStarted;
            transitions.EmbeddedFullscreenChanged -=
                OnEmbeddedFullscreenChanged;
            transitions.EmbeddedFullscreenExitRequested -=
                OnEmbeddedFullscreenExitRequested;
        }

        try
        {
            _searchCancellation?.Cancel();
            _dailyRefreshCancellation?.Cancel();
            _providerRefreshSchedulerCancellation?.Cancel();
            _liveTvLoadCancellation?.Cancel();
            _updateCheckCancellation?.Cancel();
            CancelDetailsLoad();
        }
        finally
        {
            try
            {
                CloseReaders();
            }
            finally
            {
                await CloseActivePlaybackAsync();
            }
        }

        await AwaitBackgroundTaskAsync(_providerRefreshSchedulerTask);
        await AwaitBackgroundTaskAsync(_dailyRefreshTask);

        await torrentDownloadLibrary.ShutdownAsync();
        await torrentDownloadLibrary.DisposeAsync();
        // Closing the app no longer revokes the session: the stored token is
        // what lets the next run start signed in. Only an explicit sign-out
        // ends it.
        if (remoteMediaServerClient is IDisposable disposableRemoteClient)
        {
            disposableRemoteClient.Dispose();
        }

        _updateCheckCancellation?.Dispose();
        _updateCheckCancellation = null;
        _dailyRefreshCancellation?.Dispose();
        _dailyRefreshCancellation = null;
        _dailyRefreshTask = null;
        _providerRefreshSchedulerCancellation?.Dispose();
        _providerRefreshSchedulerCancellation = null;
        _providerRefreshSchedulerTask = null;
        _liveTvLoadCancellation?.Dispose();
        _liveTvLoadCancellation = null;
        CancelApiUnitLookup();
        foreach (var channel in _allLiveTvChannels)
        {
            channel.Dispose();
        }

        _allLiveTvChannels = [];
        torrentActivityCoordinator.Dispose();
    }

    [RelayCommand]
    private async Task PauseActiveTorrentAsync()
    {
        if (ActiveTorrentActivity is not { CanPause: true } activity)
        {
            return;
        }

        try
        {
            await torrentActivityCoordinator.PauseAsync(activity.Id);
        }
        catch (Exception exception)
        {
            SetError(DownloadsFeedback, UiTextCatalog.Format(
                "TorrentActivityActionFailed",
                exception.Message));
        }
    }

    [RelayCommand]
    private async Task ResumeActiveTorrentAsync()
    {
        if (ActiveTorrentActivity is not { CanResume: true } activity)
        {
            return;
        }

        try
        {
            await torrentActivityCoordinator.ResumeAsync(activity.Id);
        }
        catch (Exception exception)
        {
            SetError(DownloadsFeedback, UiTextCatalog.Format(
                "TorrentActivityActionFailed",
                exception.Message));
        }
    }

    [RelayCommand]
    private async Task CancelActiveTorrentAsync()
    {
        if (ActiveTorrentActivity is not { CanCancel: true } activity)
        {
            return;
        }

        try
        {
            await torrentActivityCoordinator.CancelAsync(
                activity.Id,
                deleteData: false);
        }
        catch (Exception exception)
        {
            SetError(DownloadsFeedback, UiTextCatalog.Format(
                "TorrentActivityActionFailed",
                exception.Message));
        }
    }

    [RelayCommand]
    private void DismissActiveTorrent()
    {
        if (ActiveTorrentActivity is { } activity)
        {
            _dismissedTorrentActivities.Add(activity.Id);
            SelectActiveTorrentActivity();
        }
    }

    /// <summary>
    /// Reads the starred creators and providers, which belong to a profile like
    /// everything else personal and so are read again whenever the profile
    /// changes underneath the stores.
    /// </summary>
    /// <summary>
    /// Reads who can use this installation and settles on who is using it.
    /// <para>
    /// Opens the picker only when there is more than one profile. A device with
    /// one profile behaves exactly as it did before profiles existed.
    /// </para>
    /// </summary>
    private async Task LoadProfilesAsync()
    {
        if (userProfileStore is null || activeProfile is null)
        {
            return;
        }

        try
        {
            var profiles = await userProfileStore.ListAsync();
            if (profiles.Count == 0)
            {
                return;
            }

            // Whoever was here last, or the first one. Nothing is remembered
            // across launches beyond that, because a picker that pre-selects
            // is only a shortcut, never a decision.
            var active = profiles
                .OrderByDescending(profile => profile.LastUsedAt ?? DateTimeOffset.MinValue)
                .First();
            activeProfile.ActiveProfileId = active.Id;
            RebuildProfileTiles(profiles, active.Id);
            IsProfilePickerOpen = profiles.Count > 1;
        }
        catch (Exception exception)
        {
            // A library that cannot list its profiles still opens, on whichever
            // one the stores default to.
            ApplicationDiagnosticLog.Warning(
                "profiles.load-failed",
                exception.Message);
        }
    }

    private void RebuildProfileTiles(
        IReadOnlyList<UserProfile> profiles,
        Guid activeId)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            var tile = new ProfileTileViewModel(profile);
            tile.Update(profile, profile.Id == activeId);
            Profiles.Add(tile);
        }

        CurrentProfile = Profiles.FirstOrDefault(tile => tile.Id == activeId);
        OnPropertyChanged(nameof(HasOtherProfiles));
        OnPropertyChanged(nameof(CurrentProfileName));
        OnPropertyChanged(nameof(SupportsProfiles));
    }

    /// <summary>
    /// Switches to another profile, closing whatever the last one was in the
    /// middle of.
    /// <para>
    /// A reader or a player left open would keep writing one person's position
    /// against another's name, so both are torn down before the change rather
    /// than after. The catalog is shared and stays loaded; only what is
    /// personal about it is read again.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task SelectProfileAsync(ProfileTileViewModel? profile)
    {
        if (profile is null || userProfileStore is null || activeProfile is null)
        {
            return;
        }

        IsProfilePickerOpen = false;
        if (profile.Id == activeProfile.ActiveProfileId &&
            CurrentProfile is not null)
        {
            return;
        }

        await CloseActivePlaybackAsync();
        CloseReaders();
        SelectedDetails = null;
        IsSettingsOpen = false;
        IsDownloadsOpen = false;
        IsUserListsOpen = false;
        IsFriendsOpen = false;

        // Closing the reader released a position to upload. It has to land
        // before the stores start answering for somebody else, and what it was
        // carrying is forgotten either way: those titles are not the incoming
        // profile's to send.
        await FlushProgressSyncAsync();
        lock (_progressSyncGate)
        {
            _movedCatalogItems.Clear();
        }

        DetachRemoteAccount();
        activeProfile.ActiveProfileId = profile.Id;
        foreach (var tile in Profiles)
        {
            tile.IsActive = tile.Id == profile.Id;
        }

        CurrentProfile = profile;
        OnPropertyChanged(nameof(CurrentProfileName));

        try
        {
            await userProfileStore.MarkUsedAsync(profile.Id);
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "profiles.mark-used-failed",
                exception.Message);
        }

        // Everything personal is re-read: favourites, progress and lists all
        // belong to whoever was just chosen.
        await ReloadCatalogUserStateAsync();
        await ReloadUserListsAsync();

        // Including the account. Each profile signs into its own, so this both
        // loads the incoming profile's server settings and signs it back in if
        // it left a session behind; a profile that never signed in simply
        // stays signed out.
        await InitializeRemoteServerSettingsAsync();
        ClearSearch();
        await LoadHomeAsync();
    }

    /// <summary>
    /// Puts the remote account back to the state it has before anyone signs in,
    /// so nothing the outgoing profile knew is still on screen or still usable
    /// while the incoming one is being loaded.
    /// <para>
    /// The token is dropped locally rather than revoked: it belongs to the
    /// profile being left, which must not be made to sign in again to come
    /// back.
    /// </para>
    /// </summary>
    private void DetachRemoteAccount()
    {
        remoteMediaServerClient.AbandonSession();
        ClearRemoteSession();
        RemoteUsername = string.Empty;
        RemoteDisplayName = string.Empty;

        // Capabilities describe an account's server, not the installation, so
        // they are dropped with the account rather than carried across.
        _friendService.ApplyCapabilities(null);
        _watchPartyService.ApplyCapabilities(null);

        // Forgotten rather than left: the token that could tell the server is
        // the outgoing profile's, and the party is that profile's session.
        _ = _watchPartyService.ResetAsync();
        lock (_progressSyncGate)
        {
            _supportsProgressPatch = false;
            _supportsDeltaSync = false;
            _syncBaseline = null;
            _syncBaselineRevision = 0;
        }

        Friends.Clear();
        FriendRequests.Clear();
        NotifyFriendCollectionsChanged();
        OnPropertyChanged(nameof(SupportsFriends));
        RefreshWatchPartyState();
    }

    [RelayCommand]
    private void ShowProfilePicker()
    {
        if (SupportsProfiles && HasOtherProfiles)
        {
            IsProfilePickerOpen = true;
        }
    }

    [RelayCommand]
    private void CancelProfilePicker() => IsProfilePickerOpen = false;

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        if (userProfileStore is null ||
            string.IsNullOrWhiteSpace(NewProfileName))
        {
            return;
        }

        try
        {
            await userProfileStore.CreateAsync(NewProfileName);
            NewProfileName = string.Empty;
            RebuildProfileTiles(
                await userProfileStore.ListAsync(),
                activeProfile?.ActiveProfileId ?? Guid.Empty);
            StatusMessage = UiTextCatalog.Get("ProfileAdded");
        }
        catch (Exception exception)
        {
            SetError(ProfilesFeedback, exception.Message);
        }
    }

    [RelayCommand]
    private async Task RemoveProfileAsync(ProfileTileViewModel? profile)
    {
        if (profile is null || userProfileStore is null)
        {
            return;
        }

        try
        {
            await userProfileStore.DeleteAsync(profile.Id);
            var remaining = await userProfileStore.ListAsync();

            // Removing whoever is signed in has to land somewhere, so the
            // first profile left takes over rather than the application being
            // left pointing at something that is gone.
            var nextId = profile.Id == activeProfile?.ActiveProfileId
                ? remaining[0].Id
                : activeProfile?.ActiveProfileId ?? remaining[0].Id;
            RebuildProfileTiles(remaining, nextId);
            if (profile.Id == activeProfile?.ActiveProfileId &&
                CurrentProfile is { } next)
            {
                await SelectProfileAsync(next);
            }

            StatusMessage = UiTextCatalog.Get("ProfileRemoved");
        }
        catch (Exception exception)
        {
            SetError(ProfilesFeedback, exception.Message);
        }
    }

    [RelayCommand]
    private Task ShowAllAsync() =>
        LoadHomeAsync();

    [RelayCommand]
    private Task ShowMoviesAsync() =>
        LoadAsync(
            MediaType.Movie,
            UiTextCatalog.Get("NavMovies"),
            MediaSubtypeGroup.LiveAction);

    [RelayCommand]
    private Task ShowAnimeAsync() =>
        LoadAsync(
            MediaType.Movie,
            UiTextCatalog.Get("NavAnime"),
            MediaSubtypeGroup.Anime);

    [RelayCommand]
    private Task ShowBooksAsync() =>
        LoadAsync(MediaType.Book, UiTextCatalog.Get("NavBooks"));

    [RelayCommand]
    private void ToggleCatalogView() =>
        PrefersCatalogGrid = !PrefersCatalogGrid;

    /// <summary>
    /// Opening a genre row is a genre <em>search</em>, not a narrowing of the
    /// rows: the shelf only ever held what is already downloaded, and the
    /// point of asking for the genre is to see what the providers have too.
    /// The page's media type and subtype ride along, so a genre opened from
    /// anime comes back as anime.
    /// </summary>
    private void OpenGenreShelf(ApiContentGenre genre)
    {
        var filter = GenreFilters.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Genre?.Key,
                genre.Key,
                StringComparison.Ordinal));
        if (filter is null)
        {
            return;
        }

        // The row is a genre, not a genre narrowed by whatever text happens to
        // be left in the search box. Leaving the query in place ANDed the two
        // and answered with a near-empty shelf. Suppressed so clearing it does
        // not fire a search of its own before the genre is set.
        if (!string.IsNullOrEmpty(SearchQuery))
        {
            _isRebuildingCatalogFilters = true;
            try
            {
                SearchQuery = string.Empty;
            }
            finally
            {
                _isRebuildingCatalogFilters = false;
            }
        }

        // The setter is what launches the search, so an unchanged selection
        // has to be re-run by hand rather than silently doing nothing.
        if (ReferenceEquals(SelectedGenreFilter, filter))
        {
            SearchCatalogCommand.Execute(null);
            return;
        }

        SelectedGenreFilter = filter;
    }

    [RelayCommand]
    private async Task ShowLiveTvAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        CancelDetailsLoad();
        SetError(LiveTvFeedback, null);
        StatusMessage = null;
        await CloseActivePlaybackAsync();
        CloseReaders();
        SelectedDetails = null;
        IsSettingsOpen = false;
        IsDownloadsOpen = false;
        IsUserListsOpen = false;
        IsFriendsOpen = false;
        _selectedMediaType = null;
        _selectedSubtypeGroup = null;
        IsLiveTvOpen = true;
        NotifySectionNavigationChanged();

        if (_hasLoadedLiveTv)
        {
            ApplyLiveTvFilters();
            return;
        }

        await LoadLiveTvAsync();
    }

    [RelayCommand]
    private Task RefreshLiveTvAsync() =>
        LoadLiveTvAsync(forceRefresh: true);

    private async Task LoadLiveTvAsync(bool forceRefresh = false)
    {
        if (!IsLiveTvOpen ||
            (!forceRefresh && _hasLoadedLiveTv))
        {
            return;
        }

        _liveTvLoadCancellation?.Cancel();
        var loadCancellation = new CancellationTokenSource();
        _liveTvLoadCancellation = loadCancellation;
        try
        {
            IsBusy = true;
            SetError(LiveTvFeedback, null);
            StatusMessage = null;
            var result = await liveTvCatalogService.LoadAsync(
                loadCancellation.Token);
            loadCancellation.Token.ThrowIfCancellationRequested();

            foreach (var channel in _allLiveTvChannels)
            {
                channel.Dispose();
            }

            _allLiveTvChannels = result.Channels
                .Select(channel => new LiveTvChannelViewModel(
                    channel,
                    mediaSessionService,
                    PlayLiveTvChannelAsync))
                .ToArray();
            _hasLoadedLiveTv = true;
            LiveTvTotalChannelCount = _allLiveTvChannels.Count;
            LiveTvLoadedProviderCount = result.LoadedProviderCount;
            LiveTvFailureSummary = result.Failures.Count == 0
                ? string.Empty
                : UiTextCatalog.Format(
                    "LiveTvProviderFailures",
                    string.Join(
                        " · ",
                        result.Failures.Select(failure =>
                            $"{failure.ProviderName}: {failure.Message}")));
            RebuildLiveTvFilters();

        }
        catch (OperationCanceledException)
            when (loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LiveTvFailureSummary = UiTextCatalog.Format(
                "LiveTvLoadFailed",
                exception.Message);
            LiveTvLoadedProviderCount = 0;
        }
        finally
        {
            if (ReferenceEquals(_liveTvLoadCancellation, loadCancellation))
            {
                _liveTvLoadCancellation = null;
                IsBusy = false;
                OnPropertyChanged(nameof(ShowNoLiveTvChannels));
            }

            loadCancellation.Dispose();
        }
    }

    private void RebuildLiveTvFilters()
    {
        _isRebuildingLiveTvFilters = true;
        try
        {
            LiveTvProviderFilters.Clear();
            LiveTvProviderFilters.Add(
                new LiveTvFilterOptionViewModel(
                    null,
                    UiTextCatalog.Get("LiveTvAllProviders")));
            foreach (var (providerId, providerName) in
                     LiveTvChannelFilter.BuildProviderOptions(
                         _allLiveTvChannels))
            {
                LiveTvProviderFilters.Add(
                    new LiveTvFilterOptionViewModel(
                        providerId,
                        providerName));
            }

            LiveTvGroupFilters.Clear();
            LiveTvGroupFilters.Add(
                new LiveTvFilterOptionViewModel(
                    null,
                    UiTextCatalog.Get("LiveTvAllGroups")));
            foreach (var group in LiveTvChannelFilter.BuildGroupOptions(
                         _allLiveTvChannels))
            {
                LiveTvGroupFilters.Add(
                    new LiveTvFilterOptionViewModel(group, group));
            }

            SelectedLiveTvProviderFilter =
                LiveTvProviderFilters.FirstOrDefault();
            SelectedLiveTvGroupFilter =
                LiveTvGroupFilters.FirstOrDefault();
        }
        finally
        {
            _isRebuildingLiveTvFilters = false;
        }

        ApplyLiveTvFilters();
    }

    private void ApplyLiveTvFilters()
    {
        var filtered = LiveTvChannelFilter.Apply(
            _allLiveTvChannels,
            SelectedLiveTvProviderFilter?.Value,
            SelectedLiveTvGroupFilter?.Value,
            LiveTvSearchQuery);

        var selectedChannel = SelectedLiveTvChannel;
        ObservableCollectionSynchronizer.Synchronize(
            LiveTvChannels,
            filtered);
        SelectedLiveTvChannel =
            selectedChannel is not null &&
            filtered.Contains(selectedChannel)
                ? selectedChannel
                : filtered.FirstOrDefault();

        OnPropertyChanged(nameof(HasLiveTvChannels));
        OnPropertyChanged(nameof(ShowNoLiveTvChannels));
        OnPropertyChanged(nameof(LiveTvChannelSummary));
    }

    private async Task PlayLiveTvChannelAsync(
        LiveTvChannelViewModel channelViewModel)
    {
        ArgumentNullException.ThrowIfNull(channelViewModel);
        try
        {
            IsBusy = true;
            SetError(IsEmbeddedMediaOpen ? PlaybackFeedback : LiveTvFeedback, null);
            StatusMessage = null;
            if (!platformMediaPlayer.IsAvailable)
            {
                throw new NotSupportedException(
                    UiTextCatalog.Get("MediaPresenterUnavailable"));
            }

            var channel = channelViewModel.Channel;
            await CloseActivePlaybackAsync();
            CloseReaders();
            ActiveMediaTitle = channel.Name;
            var playback = await liveTvCatalogService.ResolvePlaybackAsync(
                channel);
            var initialVariant = CreateLiveTvPlatformVariant(
                playback.Label,
                playback.StreamUri,
                playback.Referrer,
                playback.UserAgent);
            var alternateVariants =
                (playback.AlternateVariants ?? [])
                .Select(variant => CreateLiveTvPlatformVariant(
                    variant.Label,
                    variant.StreamUri,
                    variant.Referrer,
                    variant.UserAgent))
                .ToArray();
            await ShowEmbeddedMediaSurfaceAsync();
            await platformMediaPlayer.OpenAsync(
                new PlatformMediaRequest(
                    initialVariant,
                    channel.Name,
                    StartPositionSeconds: 0,
                    Subtitles: [],
                    AlternateAudioVariants: alternateVariants,
                    IsLive: true));

        }
        catch (Exception exception)
        {

            SetError(IsEmbeddedMediaOpen ? PlaybackFeedback : LiveTvFeedback, UiTextCatalog.Format(
                "LiveTvPlaybackFailed",
                channelViewModel.Name,
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static PlatformMediaVariant CreateLiveTvPlatformVariant(
        string label,
        Uri streamUri,
        Uri? referrer,
        string? userAgent) =>
        new(
            new PlatformMediaSource(
                streamUri,
                openReadAsync: null,
                referrer,
                userAgent),
            label,
            IsAudio: false,
            LiveTvStreamFormat.ResolveMimeType(streamUri));

    private void LeaveLiveTv()
    {
        if (!IsLiveTvOpen)
        {
            return;
        }

        _liveTvLoadCancellation?.Cancel();
        IsLiveTvOpen = false;
    }

    [RelayCommand]
    private async Task ShowDownloadsAsync()
    {
        try
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            CancelDetailsLoad();
            IsBusy = true;
            SetError(DownloadsFeedback, null);
            StatusMessage = null;
            await CloseActivePlaybackAsync();
            LeaveLiveTv();
            CloseReaders();
            SelectedDetails = null;
            IsSettingsOpen = false;
            IsUserListsOpen = false;
            IsFriendsOpen = false;
            IsDownloadsOpen = true;
            SearchQuery = string.Empty;
            IsSearchActive = false;
            _searchResults = [];
            RefreshDownloads();
            NotifySectionNavigationChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleSelectAllDownloads()
    {
        var select = !AllDownloadsSelected;
        foreach (var download in Downloads)
        {
            download.IsSelected = select;
        }

        NotifyDownloadSelectionChanged();
    }

    [RelayCommand]
    private void RequestDeleteSelectedDownloads()
    {
        SetPendingDownloadDeletion(
            Downloads
                .Where(static download => download.IsSelected)
                .Select(static download => download.Id)
                .ToArray());
    }

    [RelayCommand]
    private void RequestDeleteAllDownloads() =>
        SetPendingDownloadDeletion(
            Downloads.Select(static download => download.Id).ToArray());

    [RelayCommand]
    private void RequestDeleteDownload(
        TorrentDownloadItemViewModel? download)
    {
        if (download is not null)
        {
            SetPendingDownloadDeletion([download.Id]);
        }
    }

    [RelayCommand]
    private void CancelDeleteDownloads()
    {
        _pendingDownloadDeletion = [];
        IsDownloadDeleteConfirmationVisible = false;
        OnPropertyChanged(nameof(DownloadDeleteConfirmationText));
    }

    [RelayCommand]
    private async Task ConfirmDeleteDownloadsAsync()
    {
        var recordIds = _pendingDownloadDeletion;
        if (recordIds.Count == 0)
        {
            CancelDeleteDownloads();
            return;
        }

        try
        {
            IsBusy = true;
            SetError(DownloadsFeedback, null);
            StatusMessage = null;
            await torrentDownloadLibrary.DeleteAsync(recordIds);
            StatusMessage = UiTextCatalog.Format(
                "DownloadsDeleted",
                recordIds.Count);
            CancelDeleteDownloads();
            RefreshDownloads();
        }
        catch (Exception exception)
        {
            SetError(DownloadsFeedback, UiTextCatalog.Format(
                "DownloadsDeleteFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PauseDownloadAsync(
        TorrentDownloadItemViewModel? download)
    {
        if (download is not { CanPause: true })
        {
            return;
        }

        try
        {
            await torrentDownloadLibrary.PauseAsync(download.Id);
        }
        catch (Exception exception)
        {
            SetError(DownloadsFeedback, UiTextCatalog.Format(
                "TorrentActivityActionFailed",
                exception.Message));
        }
    }

    [RelayCommand]
    private async Task ResumeDownloadAsync(
        TorrentDownloadItemViewModel? download)
    {
        if (download is not { CanResume: true })
        {
            return;
        }

        try
        {
            await torrentDownloadLibrary.ResumeAsync(download.Id);
        }
        catch (Exception exception)
        {
            SetError(DownloadsFeedback, UiTextCatalog.Format(
                "TorrentActivityActionFailed",
                exception.Message));
        }
    }

    /// <summary>
    /// The filtering modes offered on the settings screen. Every mode is
    /// listed even when the device will cap it: a viewer who chooses more than
    /// the hardware can carry is told so rather than silently overruled.
    /// </summary>
    public IReadOnlyList<ContentFilterModeOptionViewModel> ContentFilterModes
    { get; } =
    [
        .. Enum.GetValues<FilteringMode>()
            .Select(static mode => new ContentFilterModeOptionViewModel(mode))
    ];

    public string SelectedContentFilterModeDescription =>
        SelectedContentFilterMode?.Description ?? string.Empty;

    /// <summary>
    /// False on hosts with nowhere to cache a downloaded ruleset, where the
    /// compiled-in seed is all there is.
    /// </summary>
    public bool CanUpdateFilterLists =>
        contentFilterCoordinator?.CanDownload is true;

    /// <summary>
    /// True when the device tier will reduce the chosen mode, which is worth
    /// saying out loud on a television box.
    /// </summary>
    public bool IsContentFilterModeCapped =>
        contentFilterCoordinator is { } coordinator &&
        coordinator.EffectiveMode != coordinator.Mode;

    /// <summary>
    /// What filtering did to the last embedded page. The point of showing it
    /// is that "I still see advertisements" has several different causes —
    /// weaker in-page filtering, a ruleset that has not downloaded yet, or a
    /// page whose advertisements come from its own origin — and they are not
    /// distinguishable without this.
    /// </summary>
    public string ContentFilterPlaybackStatus
    {
        get
        {
            if (platformMediaPlayer is not WebViewPlatformMediaPlayer player ||
                player.FilterReport is not { RequestsSeen: > 0 } report)
            {
                return string.Empty;
            }

            string Count(int value) =>
                value.ToString("N0", CultureInfo.CurrentCulture);

            var status = UiTextCatalog.Format(
                report.IsNetworkLevel
                    ? "ContentFilterPlaybackNetwork"
                    : "ContentFilterPlaybackInPage",
                Count(report.RequestsBlocked),
                Count(report.RequestsSeen),
                report.Limitation ?? string.Empty);

            // Separate same-site requests that passed filtering from media
            // requests protected for playback.
            status += UiTextCatalog.Format(
                "ContentFilterPlaybackAllowances",
                Count(report.RequestsAllowedAsSameSite),
                Count(report.RequestsAllowedAsMedia));

            // And the part nobody can work out from the outside: which hosts
            // loaded simply because no rule mentions them. That is what turns
            // a surviving advertisement into a rule someone can write.
            return report.UnmatchedHosts.Count == 0
                ? status
                : status + UiTextCatalog.Format(
                    "ContentFilterPlaybackUnmatched",
                    string.Join(", ", report.UnmatchedHosts));
        }
    }

    private void LoadContentFilterSettings()
    {
        OnPropertyChanged(nameof(ContentFilterPlaybackStatus));
        if (contentFilterCoordinator is not { } coordinator)
        {
            return;
        }

        SelectedContentFilterMode = ContentFilterModes.FirstOrDefault(
            option => option.Mode == coordinator.Mode);
        HideAdultContent = coordinator.HideAdultContent;
        ContentFilterStatus = UiTextCatalog.Format(
            "ContentFilterStatusReady",
            coordinator.NetworkRuleCount.ToString("N0", CultureInfo.CurrentCulture),
            coordinator.LastRefreshUtc is { } refreshed
                ? refreshed.ToLocalTime().ToString(
                    "g",
                    CultureInfo.CurrentCulture)
                : UiTextCatalog.Get("ContentFilterNeverRefreshed"));
        OnPropertyChanged(nameof(CanUpdateFilterLists));
        OnPropertyChanged(nameof(IsContentFilterModeCapped));
    }

    private void ApplyContentFilterOutcome(ContentFilterRefreshOutcome outcome)
    {
        ContentFilterStatus = outcome.Status switch
        {
            ContentFilterRefreshStatus.Completed => UiTextCatalog.Format(
                "ContentFilterStatusUpdated",
                outcome.ListsFetched.ToString("N0", CultureInfo.CurrentCulture),
                outcome.NetworkRuleCount.ToString(
                    "N0",
                    CultureInfo.CurrentCulture)),
            ContentFilterRefreshStatus.NotSubscribed => UiTextCatalog.Format(
                "ContentFilterStatusSeedOnly",
                outcome.NetworkRuleCount.ToString(
                    "N0",
                    CultureInfo.CurrentCulture)),
            ContentFilterRefreshStatus.Deferred =>
                UiTextCatalog.Get("ContentFilterStatusDeferred"),
            ContentFilterRefreshStatus.NoStorage =>
                UiTextCatalog.Get("ContentFilterStatusNoStorage"),
            _ => UiTextCatalog.Format(
                "ContentFilterStatusFailed",
                outcome.Error ?? string.Empty)
        };

        if (outcome.ListsFailed > 0 &&
            outcome.Status == ContentFilterRefreshStatus.Completed)
        {
            // A list that failed to refresh is a stale list, not an absent
            // one, so this is a note rather than an error.
            ContentFilterStatus += UiTextCatalog.Format(
                "ContentFilterStatusSomeListsFailed",
                outcome.ListsFailed.ToString("N0", CultureInfo.CurrentCulture));
        }

        OnPropertyChanged(nameof(IsContentFilterModeCapped));
        OnPropertyChanged(nameof(ContentFilterPlaybackStatus));
    }

    [RelayCommand]
    private async Task SaveContentFilterOptionsAsync()
    {
        if (contentFilterCoordinator is not { } coordinator ||
            SelectedContentFilterMode is not { } option)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetError(FiltersFeedback, null);
            if (option.Mode != coordinator.Mode)
            {
                ApplyContentFilterOutcome(
                    await coordinator.SetModeAsync(
                        option.Mode,
                        HideAdultContent));
            }
            else
            {
                await coordinator.SetHideAdultContentAsync(HideAdultContent);
            }
            await RefreshAsync();
            StatusMessage = UiTextCatalog.Get("ContentFilterSaved");
        }
        catch (Exception exception)
        {
            SetError(FiltersFeedback, UiTextCatalog.Format(
                "ContentFilterSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateFilterListsAsync()
    {
        if (contentFilterCoordinator is not { } coordinator)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetError(FiltersFeedback, null);
            ApplyContentFilterOutcome(
                await coordinator.RefreshAsync(force: true));
        }
        catch (Exception exception)
        {
            SetError(FiltersFeedback, UiTextCatalog.Format(
                "ContentFilterSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveDownloadOptionsAsync()
    {
        try
        {
            IsBusy = true;
            SetError(DownloadOptionsFeedback, null);
            await torrentDownloadLibrary.SavePreferencesAsync(
                new TorrentDownloadPreferences(
                    DeleteTorrentDataWhenSessionEnds
                        ? TorrentSessionEndBehavior.DeleteDownloads
                        : TorrentSessionEndBehavior.KeepDownloads));
            StatusMessage = UiTextCatalog.Get("DownloadOptionsSaved");
        }
        catch (Exception exception)
        {
            SetError(DownloadOptionsFeedback, UiTextCatalog.Format(
                "DownloadOptionsSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() =>
        CheckForUpdatesCoreAsync(silent: false);

    [RelayCommand]
    private void DismissUpdatePrompt()
    {
        CancelUpdatePromptTimeout();
        IsUpdatePromptVisible = false;
    }

    internal void KeepUpdatePromptOpenForInteraction()
    {
        if (IsUpdatePromptVisible)
        {
            // Do not cancel/dispose the timeout from Android's input dispatch
            // path. Some low-power TV devices run cancellation continuations
            // inline and can exceed the five-second key-event deadline.
            _isUpdatePromptPinned = true;
        }
    }

    /// <summary>
    /// Raised after a downloaded update has been staged and the application has
    /// to exit so its files can be replaced.
    /// </summary>
    public event Action? RestartRequested;

    /// <summary>
    /// Runs only from the update prompt: an update is never downloaded,
    /// replaced, or installed without the user asking for it.
    /// </summary>
    [RelayCommand]
    private async Task InstallAvailableUpdateAsync()
    {
        if (AvailableUpdate is not { } update ||
            IsDownloadingUpdate)
        {
            return;
        }

        CancelUpdatePromptTimeout();
        try
        {
            IsDownloadingUpdate = true;
            UpdateDownloadProgress = 0;
            SetError(UpdatesFeedback, null);
            var progress = new Progress<double>(value =>
                UpdateDownloadProgress = Math.Clamp(value, 0, 1));
            var result =
                await applicationUpdateService.DownloadAndApplyAsync(
                    update,
                    progress);
            switch (result.Outcome)
            {
                case ApplicationUpdateOutcome.RestartRequired:
                    IsUpdatePromptVisible = false;
                    StatusMessage = UiTextCatalog.Get("UpdateRestarting");
                    RestartRequested?.Invoke();
                    break;
                case ApplicationUpdateOutcome.InstallerRequested:
                    IsUpdatePromptVisible = false;
                    StatusMessage = UiTextCatalog.Get("UpdateInstallerStarted");
                    break;
                case ApplicationUpdateOutcome.PermissionRequired:
                    SetError(UpdatesFeedback, UiTextCatalog.Get(
                        "UpdateInstallPermissionRequired"));
                    break;
            }
        }
        catch (OperationCanceledException exception)
        {
            StatusMessage = UiTextCatalog.Format(
                "UpdateCancelled",
                exception.Message);
        }
        catch (Exception exception)
        {
            SetError(UpdatesFeedback, UiTextCatalog.Format(
                "UpdateInstallFailed",
                exception.Message));
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private async Task CheckForUpdatesCoreAsync(bool silent)
    {
        if (!SupportsApplicationUpdates ||
            IsCheckingForUpdates ||
            IsDownloadingUpdate)
        {
            return;
        }

        _updateCheckCancellation?.Cancel();
        _updateCheckCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _updateCheckCancellation = cancellation;
        try
        {
            IsCheckingForUpdates = true;
            if (!silent)
            {
                SetError(UpdatesFeedback, null);
                StatusMessage = null;
            }

            applicationUpdateService.RememberServiceUrl(RemoteServiceUrl);
            AvailableUpdate =
                await applicationUpdateService.CheckForUpdateAsync(
                    cancellation.Token);
            IsUpdatePromptVisible = AvailableUpdate is not null;
            if (AvailableUpdate is not null && silent)
            {
                // The startup notice is a reminder, not a modal decision: it
                // fades on its own and the update stays available in Settings.
                StartUpdatePromptTimeout();
            }

            if (!silent && AvailableUpdate is null)
            {
                StatusMessage = UiTextCatalog.Get("ApplicationUpToDate");
            }
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!silent)
            {
                SetError(UpdatesFeedback, UiTextCatalog.Format(
                    "UpdateCheckFailed",
                    exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_updateCheckCancellation, cancellation))
            {
                IsCheckingForUpdates = false;
            }
        }
    }

    [RelayCommand]
    private async Task SaveRemoteServerSettingsAsync()
    {
        if (!SupportsRemoteSync || IsRemoteSyncBusy)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(AccountFeedback, null);
            StatusMessage = null;
            var settings = BuildRemoteServerSettings();
            if (settings.ServiceUrl.Length > 0)
            {
                var capabilities =
                    await remoteMediaServerClient.GetCapabilitiesAsync(
                        new Uri(settings.ServiceUrl));
                EnsureRemoteCapabilities(
                    capabilities,
                    settings.OnlineCatalogEnabled);
            }

            if (IsRemoteSignedIn &&
                !string.Equals(
                    remoteMediaServerClient.Session?.ServiceBaseUri
                        .AbsoluteUri,
                    settings.ServiceUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                await remoteMediaServerClient.SignOutAsync();
                ClearRemoteSession();
                await ForgetStoredRemoteSessionAsync();
            }

            await remoteServerSettingsStore.SaveAsync(settings);
            _remoteServerSettings = settings;
            await ConfigureRemoteOnlineCatalogAsync(settings);
            StatusMessage = UiTextCatalog.Get(
                "RemoteServerSettingsSaved");
        }
        catch (Exception exception)
        {
            SetError(AccountFeedback, UiTextCatalog.Format(
                "RemoteServerSettingsSaveFailed",
                exception.Message));
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    [RelayCommand]
    private Task RegisterRemoteAccountAsync() =>
        AuthenticateRemoteAccountAsync(register: true);

    [RelayCommand]
    private Task SignInRemoteAccountAsync() =>
        AuthenticateRemoteAccountAsync(register: false);

    [RelayCommand]
    private async Task SignOutRemoteAccountAsync()
    {
        if (!IsRemoteSignedIn || IsRemoteSyncBusy)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(AccountFeedback, null);
            await remoteMediaServerClient.SignOutAsync();
            ClearRemoteSession();
            await ForgetStoredRemoteSessionAsync();
            StatusMessage = UiTextCatalog.Get("RemoteAccountSignedOutStatus");
        }
        catch (Exception exception)
        {
            ClearRemoteSession();
            // The user asked to sign out, so the token must not survive even if
            // telling the server failed.
            await ForgetStoredRemoteSessionAsync();
            SetError(AccountFeedback, UiTextCatalog.Format(
                "RemoteSignOutFailed",
                exception.Message));
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshRemoteSnapshotAsync()
    {
        if (!CanUseRemoteSync)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(AccountFeedback, null);
            StatusMessage = null;
            await RefreshRemoteSnapshotStateAsync();
            StatusMessage = UiTextCatalog.Get(
                _hasRemoteSnapshot
                    ? "RemoteSnapshotRefreshed"
                    : "RemoteSnapshotNone");
        }
        catch (Exception exception)
        {
            ReportRemoteSyncFailure(
                "RemoteSnapshotCheckFailed",
                exception);
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task UploadRemoteSnapshotAsync()
    {
        if (!CanUseRemoteSync)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(AccountFeedback, null);
            StatusMessage = null;
            var result = await UploadRemoteSnapshotCoreAsync();
            StatusMessage = UiTextCatalog.Format(
                "RemoteSnapshotUploaded",
                result.Revision);
        }
        catch (RemoteSyncConflictException exception)
        {
            SetError(AccountFeedback, UiTextCatalog.Format(
                "RemoteSnapshotConflict",
                exception.CurrentRevision));
        }
        catch (Exception exception)
        {
            ReportRemoteSyncFailure(
                "RemoteSnapshotUploadFailed",
                exception);
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    [RelayCommand]
    private void RequestRestoreRemoteSnapshot()
    {
        if (CanUseRemoteSync && _hasRemoteSnapshot)
        {
            IsRemoteRestoreConfirmationVisible = true;
        }
    }

    [RelayCommand]
    private void CancelRestoreRemoteSnapshot() =>
        IsRemoteRestoreConfirmationVisible = false;

    [RelayCommand]
    private async Task ConfirmRestoreRemoteSnapshotAsync()
    {
        if (!CanUseRemoteSync ||
            userSyncStore is null ||
            !IsRemoteRestoreConfirmationVisible)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(AccountFeedback, null);
            StatusMessage = null;
            var reconciliation =
                await RemoteUserSyncReconciler.SynchronizeAsync(
                    remoteMediaServerClient,
                    userSyncStore,
                    _remoteServerSettings.DeviceId,
                    RemoteDeviceName,
                    CreateSyncDeltaContext());
            _syncBaseline = reconciliation.NewBaseline ?? _syncBaseline;
            _syncBaselineRevision = reconciliation.BaselineRevision ?? reconciliation.WriteResult.Revision;
            var restored = reconciliation.RestoreResult ??
                new UserSyncRestoreResult(0, 0, 0, 0, 0, 0);
            ApplyRemoteSnapshotState(
                reconciliation.WriteResult.Revision,
                reconciliation.WriteResult.UpdatedAt,
                hasSnapshot: true);
            IsRemoteRestoreConfirmationVisible = false;
            await ReloadCatalogUserStateAsync();
            await ReloadUserListsAsync();
            await RefreshAsync();
            StatusMessage = UiTextCatalog.Format(
                "RemoteSnapshotRestored",
                restored.CatalogCreatedCount,
                restored.ProgressCount,
                restored.ListCount);
        }
        catch (Exception exception)
        {
            ReportRemoteSyncFailure(
                "RemoteSnapshotRestoreFailed",
                exception);
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task PublishSelectedUserListAsync()
    {
        if (!CanUseRemoteSync || SelectedUserList is null)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(ListsFeedback, null);
            StatusMessage = null;
            await UploadRemoteSnapshotCoreAsync();
            _lastPublishedSharedList =
                await remoteMediaServerClient.PublishSharedListAsync(
                    SelectedUserList.Id);
            RemoteSharedListUrl =
                _lastPublishedSharedList.CatalogUri.AbsoluteUri;
            StatusMessage = UiTextCatalog.Format(
                "RemoteSharedListPublished",
                SelectedUserList.Name);
        }
        catch (RemoteSyncConflictException exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "RemoteSnapshotConflict",
                exception.CurrentRevision));
        }
        catch (Exception exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "RemoteSharedListPublishFailed",
                exception.Message));
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeLastSharedListAsync()
    {
        if (!CanUseRemoteSync ||
            _lastPublishedSharedList is null)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(ListsFeedback, null);
            await remoteMediaServerClient.RevokeSharedListAsync(
                _lastPublishedSharedList.ShareId);
            _lastPublishedSharedList = null;
            RemoteSharedListUrl = string.Empty;
            StatusMessage = UiTextCatalog.Get(
                "RemoteSharedListRevoked");
        }
        catch (Exception exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "RemoteSharedListRevokeFailed",
                exception.Message));
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowFriendsAsync()
    {
        try
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            CancelDetailsLoad();
            SetError(FriendsFeedback, null);
            StatusMessage = null;
            await CloseActivePlaybackAsync();
            LeaveLiveTv();
            CloseReaders();
            SelectedDetails = null;
            IsSettingsOpen = false;
            IsDownloadsOpen = false;
            IsUserListsOpen = false;
            IsFriendsOpen = false;
            _selectedMediaType = null;
            _selectedSubtypeGroup = null;
            IsFriendsOpen = true;
            NotifySectionNavigationChanged();
            SearchQuery = string.Empty;
            IsSearchActive = false;
            _searchResults = [];
            SectionTitle = UiTextCatalog.Get(
                SupportsFriends ? "FriendsTitle" : "WatchPartyTitle");
            await ReloadFriendsAsync();
        }
        catch (Exception exception)
        {
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendsLoadFailed",
                exception.Message));
        }
    }

    /// <summary>
    /// Reloads the friend list, the outstanding requests, and the account-wide
    /// switches.
    /// </summary>
    private async Task ReloadFriendsAsync()
    {
        if (!SupportsFriends)
        {
            Friends.Clear();
            FriendRequests.Clear();
            NotifyFriendCollectionsChanged();
            return;
        }

        try
        {
            IsFriendsBusy = true;
            var friends = await _friendService.GetFriendsAsync();
            var requests = await _friendService.GetRequestsAsync();
            var visibility = await _friendService.GetVisibilityAsync();

            Friends.Clear();
            foreach (var friend in friends)
            {
                Friends.Add(new FriendViewModel(
                    friend,
                    ApplyFriendSharingAsync,
                    RemoveFriendAsync));
            }

            FriendRequests.Clear();
            foreach (var request in requests)
            {
                FriendRequests.Add(new FriendRequestViewModel(
                    request,
                    AcceptFriendRequestAsync,
                    DismissFriendRequestAsync));
            }

            ShareProgressWithFriends = visibility.ShareProgress;
            DiscoverableByUsername = visibility.DiscoverableByUsername;
            await LoadSharedTitlesAsync();
        }
        catch (Exception exception)
        {
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendsLoadFailed",
                exception.Message));
        }
        finally
        {
            IsFriendsBusy = false;
            NotifyFriendCollectionsChanged();
        }
    }

    /// <summary>
    /// Fills in what each friend has in common with this account.
    /// <para>
    /// One request per friend, after the list itself is on screen. A friend
    /// whose titles cannot be fetched keeps an empty list rather than taking
    /// the page down with them.
    /// </para>
    /// </summary>
    private async Task LoadSharedTitlesAsync()
    {
        foreach (var friend in Friends.ToArray())
        {
            try
            {
                var comparison = await _friendService.GetComparisonAsync(
                    friend.AccountId);

                // Titles this account holds become the same poster cards the
                // catalog uses, so they open from here. What only the friend
                // has cannot: this device has no copy, so those are named and
                // described instead — which is the whole point of showing
                // them, since they are the ones worth going and finding.
                friend.SetComparison(
                    ResolveComparisonCards(comparison.InCommon),
                    comparison.InCommon.Count(static title =>
                        title.ViewerFavorite && title.FriendFavorite),
                    comparison.FriendOnly
                        .Select(title => new FriendTitleViewModel(
                            title,
                            SearchForFriendTitleAsync))
                        .ToArray(),
                    ResolveComparisonCards(comparison.ViewerOnly));
            }
            catch (Exception exception)
            {
                ApplicationDiagnosticLog.Warning(
                    "friends.comparison-unavailable",
                    exception.Message);
            }
        }
    }

    /// <summary>
    /// Goes looking for a title a friend has and this device does not, in the
    /// part of the catalog it belongs to.
    /// </summary>
    /// <remarks>
    /// The section comes from the title's own media type and subtype, so a
    /// manga lands under books, an anime under anime and a film under movies
    /// rather than searching everything and making the reader sort it out.
    /// Switching sections clears the search box on purpose, so the query is
    /// set after the switch rather than before it.
    /// </remarks>
    private async Task SearchForFriendTitleAsync(FriendTitleViewModel title)
    {
        var isBook = string.Equals(
            title.MediaType,
            "book",
            StringComparison.OrdinalIgnoreCase);
        var isAnime = !isBook &&
            !string.IsNullOrWhiteSpace(title.Subtype) &&
            MediaSubtypeGroups.Resolve(
                CatalogSubtypeResolver.ResolveCode(
                    MediaType.Movie,
                    title.Subtype)) is MediaSubtypeGroup.Anime;

        if (isBook)
        {
            await LoadAsync(MediaType.Book, UiTextCatalog.Get("NavBooks"));
        }
        else
        {
            await LoadAsync(
                MediaType.Movie,
                UiTextCatalog.Get(isAnime ? "NavAnime" : "NavMovies"),
                isAnime ? MediaSubtypeGroup.Anime : MediaSubtypeGroup.LiveAction);
        }

        SearchQuery = title.Title;
        await SearchCatalogAsync();
    }

    /// <summary>
    /// Matches compared titles against this device's own catalog, dropping
    /// what it cannot key or does not hold — a card that leads nowhere is
    /// worse than no card.
    /// </summary>
    private IReadOnlyList<CatalogCardViewModel> ResolveComparisonCards(
        IReadOnlyList<RemoteFriendComparisonTitle> titles) =>
        ResolveSharedCards(titles
            .Select(static title => new RemoteSharedTitle(
                title.ItemKey,
                title.Title,
                title.UnitKind,
                title.DisplayNumber,
                title.SeasonNumber,
                title.IsCompleted,
                title.UpdatedAt))
            .ToArray());

    /// <summary>
    /// Restarts the wait before the next snapshot upload.
    /// <para>
    /// Each saved position pushes the upload back rather than adding one, so a
    /// reading run costs a single upload once it stops. A manual sync already
    /// under way is left alone: it is uploading the same state.
    /// </para>
    /// </summary>
    private void ScheduleProgressSync(CatalogContentIdentity content)
    {
        if (!CanUseRemoteSync)
        {
            return;
        }

        CancellationTokenSource cancellation;
        TimeSpan delay;
        lock (_progressSyncGate)
        {
            if (content.CatalogItemId != Guid.Empty)
            {
                _movedCatalogItems.Add(content.CatalogItemId);
            }

            // The wait is as long as the upload is expensive: seconds when a
            // patch will carry it, minutes when the whole snapshot must.
            delay = _supportsProgressPatch
                ? ProgressPatchDelay
                : ProgressSyncDelay;
            _progressSyncCancellation?.Cancel();
            _progressSyncCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _progressSyncCancellation = cancellation;
        }

        _ = RunProgressSyncAsync(cancellation, delay);
    }

    private async Task RunProgressSyncAsync(
        CancellationTokenSource cancellation,
        TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            lock (_progressSyncGate)
            {
                if (!ReferenceEquals(_progressSyncCancellation, cancellation))
                {
                    return;
                }

                _progressSyncCancellation = null;
            }

            var upload = UploadProgressSnapshotAsync();
            _progressUpload = upload;
            await upload;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later save, or flushed early by a closing
            // reader. Either way this run has nothing left to do.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Sends only the titles that moved, when the server can merge them.
    /// <para>
    /// Measured against a library of 1,774 titles a patch came to 10 KiB where
    /// the snapshot came to 2.7 MiB. Anything unexpected — no support, an
    /// export that cannot be built, a rejected patch — answers false so the
    /// caller falls back to the full upload rather than losing the position.
    /// </para>
    /// <para>
    /// A patch is a difference, so it needs something to differ from. Until the
    /// account has uploaded once there is nothing on the server, and sending
    /// the titles just read would leave a whole library represented by the one
    /// or two of them — a backup the account would then offer to restore from.
    /// </para>
    /// </summary>
    private async ValueTask<bool> TryPatchProgressAsync()
    {
        if (!_hasRemoteSnapshot)
        {
            return false;
        }

        Guid[] moved;
        lock (_progressSyncGate)
        {
            if (!_supportsProgressPatch || _movedCatalogItems.Count == 0)
            {
                return false;
            }

            moved = [.. _movedCatalogItems];
        }

        if (userSyncStore is null)
        {
            return false;
        }

        try
        {
            // Off the UI thread: the patch carries only the titles that moved
            // but it is built by reading every recorded position, and this
            // runs while the reader is open — the delay before it fires
            // resumed here, so without the hop the page turns stutter on the
            // save rather than during it.
            var patch = await Task.Run(
                () => userSyncStore.ExportProgressPatchAsync(
                    _remoteServerSettings.DeviceId,
                    RemoteDeviceName,
                    moved).AsTask());
            if (patch.Progress.Count == 0)
            {
                return false;
            }

            var written = await remoteMediaServerClient.PatchProgressAsync(
                patch);
            ApplyRemoteSnapshotState(
                written.Revision,
                written.UpdatedAt,
                hasSnapshot: true);

            // Only what this patch carried: a position saved while it was in
            // flight has to stay pending for the next one.
            lock (_progressSyncGate)
            {
                foreach (var catalogItemId in moved)
                {
                    _movedCatalogItems.Remove(catalogItemId);
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "sync.progress-patch-failed",
                exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Publishes a waiting position now instead of letting the timer run out.
    /// <para>
    /// Closing a reader or a player is the moment a sitting ends, and waiting
    /// out the full delay afterwards would leave a friend behind for no reason
    /// — the reading that would have coalesced into the same upload is over.
    /// Does nothing when no save is waiting, so leaving a title twice does not
    /// upload twice.
    /// </para>
    /// </summary>
    private void FlushProgressSync() => _ = FlushProgressSyncAsync();

    /// <summary>
    /// The same flush, for the one caller that cannot leave it running: a
    /// profile switch changes which library the stores read, so an upload still
    /// in flight would finish by sending the incoming person's titles to the
    /// outgoing person's account.
    /// </summary>
    private Task FlushProgressSyncAsync()
    {
        CancellationTokenSource? waiting;
        lock (_progressSyncGate)
        {
            waiting = _progressSyncCancellation;
            _progressSyncCancellation = null;
        }

        if (waiting is null)
        {
            return _progressUpload;
        }

        waiting.Cancel();
        var upload = UploadProgressSnapshotAsync();
        _progressUpload = upload;
        return upload;
    }

    private async Task UploadProgressSnapshotAsync()
    {
        try
        {
            if (!CanUseRemoteSync || IsRemoteSyncBusy)
            {
                return;
            }

            if (!await TryPatchProgressAsync())
            {
                Guid[] carried;
                lock (_progressSyncGate)
                {
                    carried = [.. _movedCatalogItems];
                }

                await UploadRemoteSnapshotCoreAsync();

                // The whole library went, so nothing that was waiting still
                // needs sending. Clearing only what was pending when it
                // started, for the same reason the patch path does: a position
                // saved while it was in flight belongs to the next upload.
                lock (_progressSyncGate)
                {
                    foreach (var catalogItemId in carried)
                    {
                        _movedCatalogItems.Remove(catalogItemId);
                    }
                }
            }

            // Friends now see a different answer for anything just read.
            _friendService.InvalidateProgress();
        }
        catch (Exception exception)
        {
            // A background upload that fails must stay out of the way: the
            // position is already saved locally and the next one tries again.
            ApplicationDiagnosticLog.Warning(
                "sync.background-upload-failed",
                exception.Message);
        }
    }

    /// <summary>
    /// Turns the keys the server answered with back into catalog cards.
    /// <para>
    /// The server names titles by key, which is deliberately all it needs to
    /// know. Matching them against this device's own catalog is what makes them
    /// openable, and a key with nothing behind it is dropped rather than shown
    /// as a card that leads nowhere.
    /// </para>
    /// </summary>
    private IReadOnlyList<CatalogCardViewModel> ResolveSharedCards(
        IReadOnlyList<RemoteSharedTitle> shared)
    {
        if (shared.Count == 0)
        {
            return [];
        }

        var byKey = new Dictionary<string, CatalogCard>(StringComparer.Ordinal);
        foreach (var card in _catalogCards)
        {
            try
            {
                byKey.TryAdd(
                    ContentItemKey.Create(card.MediaType, card.Name),
                    card);
            }
            catch (ArgumentException)
            {
                // A title this device cannot key is simply not matchable.
            }
        }

        return shared
            .Select(title => byKey.GetValueOrDefault(title.ItemKey))
            .OfType<CatalogCard>()
            .Select(card => GetOrCreateCatalogCard(card, "friend-shared"))
            .ToArray();
    }

    [ObservableProperty]
    private string _watchPartyJoinCode = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResynchronizeWatchParty))]
    private bool _isWatchPartyBusy;

    private IReadOnlyList<string> _watchPartyMemberLabels = [];

    public bool SupportsWatchParties => _watchPartyService.IsAvailable;

    public bool HasWatchParty => _watchPartyService.IsInParty;

    public bool ShowWatchPartyJoinControls =>
        SupportsWatchParties && !HasWatchParty;

    public string WatchPartyCode =>
        _watchPartyService.Current?.JoinCode ?? string.Empty;

    public IReadOnlyList<string> WatchPartyMemberLabels =>
        _watchPartyMemberLabels;

    public bool WatchPartyHasContent =>
        _watchPartyService.Current?.Content is not null;

    public bool CanResynchronizeWatchParty =>
        !IsWatchPartyBusy && _watchPartyService.IsConnected &&
        _watchPartyService.IsSynchronized && _watchPartyService.CanFollowParty;

    [RelayCommand]
    private async Task ResynchronizeWatchPartyAsync()
    {
        if (CanResynchronizeWatchParty)
        {
            await _watchPartyService.ResynchronizeAsync();
        }
    }

    public string WatchPartyContentLabel =>
        _watchPartyService.Current?.Content is { } content
            ? UiTextCatalog.Format("WatchPartyWatchingLabel", content.Title)
            : UiTextCatalog.Get("WatchPartyNothingPlaying");

    public string WatchPartySyncStatus =>
        HasWatchParty && !_watchPartyService.IsConnected
            ? UiTextCatalog.Get("WatchPartyReconnectingLabel")
            : _watchPartyService.IsSynchronized && _watchPartyService.CanFollowParty
            ? UiTextCatalog.Get("WatchPartySyncedLabel")
            : _watchPartyService.IsSynchronized
                ? UiTextCatalog.Get("WatchPartyNoTransportNotice")
            : _watchPartyService.Current?.Content is { } content
                ? UiTextCatalog.Format(
                    "WatchPartyNotSyncedLabel",
                    content.Title)
                : string.Empty;

    public bool HasWatchPartySyncStatus =>
        WatchPartySyncStatus.Length > 0;

    public string? WatchPartyLastEvent =>
        _watchPartyService.LastEventDescription;

    public bool HasWatchPartyLastEvent =>
        !string.IsNullOrWhiteSpace(WatchPartyLastEvent);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWatchPartyFollowPrompt))]
    private string? _watchPartyFollowPromptText;

    public bool ShowWatchPartyFollowPrompt =>
        WatchPartyFollowPromptText is not null;

    /// <summary>
    /// The content the viewer said "not now" to. Compared against the party's
    /// current content, so the prompt stays away for that episode but returns
    /// the moment the host moves on to another.
    /// </summary>
    private string? _dismissedWatchPartyContentKey;

    /// <summary>
    /// Whether the toolbar should say playback is riding the party's shared
    /// timeline.
    /// </summary>
    public bool ShowWatchPartyPlayerStatus =>
        _watchPartyService.IsInParty && _watchPartyService.IsSynchronized &&
        _watchPartyService.CanFollowParty;

    public string WatchPartyPlayerStatus
    {
        get
        {
            var current = _watchPartyService.Current;
            if (current is null)
            {
                return string.Empty;
            }

            if (!_watchPartyService.IsConnected)
            {
                return UiTextCatalog.Get("WatchPartyReconnectingLabel");
            }

            var status = UiTextCatalog.Format(
                "WatchPartyPlayerStatus",
                current.Members.Count);
            return _watchPartyService.LastEventDescription is { } lastEvent
                ? $"{status} · {lastEvent}"
                : status;
        }
    }

    /// <summary>
    /// Only the host is offered the lock, and only they can act on it.
    /// </summary>
    public bool ShowWatchPartyHostControlsToggle =>
        HasWatchParty && _watchPartyService.IsHost;

    public bool WatchPartyHostControlsOnly
    {
        get => _watchPartyService.HostControlsOnly;
        set
        {
            if (value == _watchPartyService.HostControlsOnly)
            {
                return;
            }

            _ = ApplyWatchPartyHostControlsAsync(value);
        }
    }

    /// <summary>
    /// Whether to warn that this member's player cannot follow the party. An
    /// embedded page owns its own transport, so commands would arrive and do
    /// nothing.
    /// </summary>
    public bool ShowWatchPartyNoTransportNotice =>
        HasWatchParty &&
        _watchPartyService.IsSynchronized &&
        !_watchPartyService.CanFollowParty;

    /// <summary>
    /// Republishes only what depends on whether the active player can be
    /// driven. Called from the playback monitor, so it deliberately avoids
    /// rebuilding the member list on every tick.
    /// </summary>
    private void RefreshWatchPartyTransportState()
    {
        if (!_watchPartyService.IsInParty ||
            _showWatchPartyNoTransportNotice ==
                ShowWatchPartyNoTransportNotice)
        {
            return;
        }

        _showWatchPartyNoTransportNotice = ShowWatchPartyNoTransportNotice;
        OnPropertyChanged(nameof(ShowWatchPartyNoTransportNotice));
        OnPropertyChanged(nameof(WatchPartySyncStatus));
        OnPropertyChanged(nameof(HasWatchPartySyncStatus));
        OnPropertyChanged(nameof(CanResynchronizeWatchParty));
        OnPropertyChanged(nameof(ShowWatchPartyPlayerStatus));
    }

    private bool _showWatchPartyNoTransportNotice;

    private async Task ApplyWatchPartyHostControlsAsync(bool hostControlsOnly)
    {
        try
        {
            if (!await _watchPartyService.SetHostControlsOnlyAsync(
                    hostControlsOnly))
            {
                // Refused or no longer the host: publish the real value so the
                // switch snaps back rather than showing a state nobody holds.
                OnPropertyChanged(nameof(WatchPartyHostControlsOnly));
            }
        }
        catch (Exception exception)
        {
            SetError(PartyFeedback, UiTextCatalog.Format(
                "WatchPartyOptionsFailed",
                exception.Message));
            OnPropertyChanged(nameof(WatchPartyHostControlsOnly));
        }
    }

    [RelayCommand]
    private void DismissWatchPartyFollowPrompt()
    {
        _dismissedWatchPartyContentKey =
            _watchPartyService.Current?.Content is { } content
                ? DescribeWatchPartyContentKey(content)
                : null;
        WatchPartyFollowPromptText = null;
    }

    /// <summary>
    /// Shows or clears the "the party is watching X" invitation. It appears
    /// only for a member who is not already on that title — the host chose
    /// it, and a member who declined an episode is not asked about it again.
    /// </summary>
    private void UpdateWatchPartyFollowPrompt()
    {
        var content = _watchPartyService.Current?.Content;
        if (content is null ||
            _watchPartyService.IsHost ||
            _watchPartyService.IsSynchronized ||
            string.Equals(
                DescribeWatchPartyContentKey(content),
                _dismissedWatchPartyContentKey,
                StringComparison.Ordinal))
        {
            WatchPartyFollowPromptText = null;
            return;
        }

        WatchPartyFollowPromptText = UiTextCatalog.Format(
            "WatchPartyFollowPrompt",
            content.Title);
    }

    private static string DescribeWatchPartyContentKey(
        RemoteWatchPartyContent content) =>
        string.Join(
            '|',
            content.ItemKey,
            content.UnitKind ?? string.Empty,
            content.SeasonNumber?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ??
            string.Empty,
            content.DisplayNumber ?? string.Empty);

    /// <summary>
    /// Where playback of this title should open: on the party's shared
    /// timeline when a joined party is already watching it, otherwise at the
    /// saved resume position. Joining mid-episode means starting where the
    /// party is, not where this viewer once stopped.
    /// </summary>
    private async ValueTask<double> ResolveWatchPartyStartPositionAsync(
        CatalogContentIdentity identity,
        double resumePosition)
    {
        if (_watchPartyService.Current?.Content is null)
        {
            return resumePosition;
        }

        var content = await ResolveWatchPartyContentAsync(identity);
        return content is not null &&
               _watchPartyService.GetJoinPositionFor(content) is
               { } partyPosition
            ? partyPosition
            : resumePosition;
    }

    /// <summary>
    /// Whether the embedded page about to open belongs to a party that is
    /// sitting paused. Read by the web player just before it starts that
    /// page on the viewer's behalf.
    /// </summary>
    private bool _watchPartyWantsPausedStart;

    private async ValueTask UpdateWatchPartyPausedStartAsync(
        CatalogContentIdentity identity)
    {
        // Cleared for every open, so a title started outside the party never
        // inherits a hold from one started inside it.
        _watchPartyWantsPausedStart = false;
        if (_watchPartyService.Current?.Content is null)
        {
            return;
        }

        var content = await ResolveWatchPartyContentAsync(identity);
        _watchPartyWantsPausedStart = content is not null &&
            _watchPartyService.ShouldOpenPaused(content);
    }

    private void OnWatchPartyStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshWatchPartyState();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshWatchPartyState);
        }
    }

    private void RefreshWatchPartyState()
    {
        _watchPartyMemberLabels =
            _watchPartyService.Current?.Members
                .Select(member =>
                {
                    var label = member.DisplayName;
                    if (member.IsHost)
                    {
                        label += " · " +
                            UiTextCatalog.Get("WatchPartyHostSuffix");
                    }

                    if (!member.IsConnected)
                    {
                        label += " · " +
                            UiTextCatalog.Get("WatchPartyAwaySuffix");
                    }
                    else if (member.IsBuffering)
                    {
                        label += " · " +
                            UiTextCatalog.Get("WatchPartyBufferingSuffix");
                    }

                    return label;
                })
                .ToArray() ?? [];
        OnPropertyChanged(nameof(SupportsWatchParties));
        OnPropertyChanged(nameof(ShowFriendsNav));
        OnPropertyChanged(nameof(FriendsNavLabel));
        OnPropertyChanged(nameof(HasWatchParty));
        OnPropertyChanged(nameof(ShowWatchPartyJoinControls));
        OnPropertyChanged(nameof(WatchPartyCode));
        OnPropertyChanged(nameof(WatchPartyMemberLabels));
        OnPropertyChanged(nameof(WatchPartyHasContent));
        OnPropertyChanged(nameof(CanResynchronizeWatchParty));
        OnPropertyChanged(nameof(WatchPartyContentLabel));
        OnPropertyChanged(nameof(WatchPartySyncStatus));
        OnPropertyChanged(nameof(HasWatchPartySyncStatus));
        OnPropertyChanged(nameof(WatchPartyLastEvent));
        OnPropertyChanged(nameof(HasWatchPartyLastEvent));
        OnPropertyChanged(nameof(ShowWatchPartyPlayerStatus));
        OnPropertyChanged(nameof(WatchPartyPlayerStatus));
        OnPropertyChanged(nameof(ShowWatchPartyHostControlsToggle));
        OnPropertyChanged(nameof(WatchPartyHostControlsOnly));
        _showWatchPartyNoTransportNotice = ShowWatchPartyNoTransportNotice;
        OnPropertyChanged(nameof(ShowWatchPartyNoTransportNotice));
        UpdateWatchPartyFollowPrompt();
    }

    [RelayCommand]
    private async Task StartWatchPartyAsync()
    {
        if (!SupportsWatchParties || IsWatchPartyBusy)
        {
            return;
        }

        try
        {
            IsWatchPartyBusy = true;
            RemoteWatchPartyContent? content = null;
            double position = 0;

            // A party started while something plays begins on that title at
            // this moment; one started from anywhere else begins empty and
            // adopts whatever the host plays first.
            if (_activePlaybackIdentity is { } identity)
            {
                content = await ResolveWatchPartyContentAsync(identity);
                try
                {
                    position = Math.Max(
                        0,
                        (await platformMediaPlayer.GetStateAsync())
                            .PositionSeconds);
                }
                catch (Exception)
                {
                    // The party can still start at zero.
                }
            }
            else if (SelectedDetails?.Details is { } details)
            {
                content = BuildWatchPartyContent(
                    details,
                    details.Identity);
            }

            await _watchPartyService.CreateAsync(content, position);
        }
        catch (Exception exception)
        {
            SetError(PartyFeedback, UiTextCatalog.Format(
                "WatchPartyCreateFailed",
                exception.Message));
        }
        finally
        {
            IsWatchPartyBusy = false;
            RefreshWatchPartyState();
        }
    }

    [RelayCommand]
    private async Task JoinWatchPartyAsync()
    {
        var code = WatchPartyJoinCode.Trim();
        if (!SupportsWatchParties || IsWatchPartyBusy || code.Length == 0)
        {
            return;
        }

        try
        {
            IsWatchPartyBusy = true;
            var joined = await _watchPartyService.JoinAsync(code);
            if (joined is null)
            {
                SetError(PartyFeedback, UiTextCatalog.Get("WatchPartyNotFound"));
                return;
            }

            WatchPartyJoinCode = string.Empty;
        }
        catch (Exception exception)
        {
            SetError(PartyFeedback, UiTextCatalog.Format(
                "WatchPartyJoinFailed",
                exception.Message));
        }
        finally
        {
            IsWatchPartyBusy = false;
            RefreshWatchPartyState();
        }
    }

    [RelayCommand]
    private async Task LeaveWatchPartyAsync()
    {
        if (IsWatchPartyBusy)
        {
            return;
        }

        try
        {
            IsWatchPartyBusy = true;
            await _watchPartyService.LeaveAsync();
        }
        catch (Exception exception)
        {
            SetError(PartyFeedback, UiTextCatalog.Format(
                "WatchPartyLeaveFailed",
                exception.Message));
        }
        finally
        {
            IsWatchPartyBusy = false;
            RefreshWatchPartyState();
        }
    }

    /// <summary>
    /// Takes this member to what the party is watching: the title's own page
    /// when this catalog holds it, otherwise the search box pre-filled with
    /// its name so the online providers can find it.
    /// </summary>
    [RelayCommand]
    private async Task OpenWatchPartyContentAsync()
    {
        if (_watchPartyService.Current?.Content is not { } content)
        {
            return;
        }

        try
        {
            var overview = await OffUiThread.ReadAsync(
                () => catalogBrowseService.GetOverviewAsync());
            foreach (var card in overview.Items)
            {
                string cardKey;
                try
                {
                    cardKey = ContentItemKey.Create(
                        card.MediaType,
                        card.Name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (string.Equals(
                        cardKey,
                        content.ItemKey,
                        StringComparison.Ordinal))
                {
                    IsFriendsOpen = false;
                    await OpenDetailsAsync(card.Id);
                    return;
                }
            }

            IsFriendsOpen = false;
            SearchQuery = content.Name;
            SetError(PartyFeedback, UiTextCatalog.Format(
                "WatchPartyContentNotInCatalog",
                content.Title));
            await SearchCatalogAsync();
        }
        catch (Exception exception)
        {
            SetError(ContentFeedback, UiTextCatalog.Format(
                "WatchPartyJoinFailed",
                exception.Message));
        }
    }

    /// <summary>
    /// Turns a local playback identity into the party's portable description
    /// of it. Local Guids never cross the wire; the name-based key does.
    /// </summary>
    private async ValueTask<RemoteWatchPartyContent?>
        ResolveWatchPartyContentAsync(CatalogContentIdentity identity)
    {
        try
        {
            var open = SelectedDetails?.Details;
            var details =
                open is not null &&
                open.Identity.CatalogItemId == identity.CatalogItemId
                    ? open
                    : await OffUiThread.ReadAsync(() => catalogBrowseService.GetDetailsAsync(
                        identity.CatalogItemId));
            return details is null
                ? null
                : BuildWatchPartyContent(details, identity);
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "watch-party.content-resolve",
                exception.Message);
            return null;
        }
    }

    private static RemoteWatchPartyContent BuildWatchPartyContent(
        CatalogItemDetails details,
        CatalogContentIdentity identity)
    {
        var title = details.Item.Name;
        if (identity.IsUnit)
        {
            title = identity.SeasonNumber is { } season
                ? $"{details.Item.Name} · S{season} · {identity.DisplayNumber}"
                : $"{details.Item.Name} · {identity.DisplayNumber}";
        }

        return new RemoteWatchPartyContent(
            ContentItemKey.Create(details.Item.MediaType, details.Item.Name),
            details.Item.MediaType.ToString().ToLowerInvariant(),
            details.Item.Name,
            title,
            identity.UnitKind is { } kind
                ? UserSyncDocumentValidator.UnitKindToValue(kind)
                : null,
            identity.SeasonNumber,
            identity.DisplayNumber);
    }

    private async Task NotifyWatchPartyPlaybackStartedAsync(
        CatalogContentIdentity identity)
    {
        if (!_watchPartyService.IsInParty)
        {
            return;
        }

        try
        {
            var content = await ResolveWatchPartyContentAsync(identity);
            if (content is null)
            {
                return;
            }

            double position = 0;
            try
            {
                position = Math.Max(
                    0,
                    (await platformMediaPlayer.GetStateAsync())
                        .PositionSeconds);
            }
            catch (Exception)
            {
                // Zero is an honest answer while the player is still opening.
            }

            await _watchPartyService.NotifyPlaybackStartedAsync(
                content,
                position);
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "watch-party.playback-announce",
                exception.Message);
        }
    }

    [RelayCommand]
    private async Task AddFriendAsync()
    {
        var username = FriendUsernameQuery.Trim();
        if (username.Length == 0 || !SupportsFriends)
        {
            return;
        }

        try
        {
            IsFriendsBusy = true;
            SetError(FriendsFeedback, null);
            var outcome = await _friendService.SendRequestAsync(username);

            // A name nobody holds and one whose owner does not accept requests
            // answer alike, so this cannot say which it was.
            if (outcome is null)
            {
                SetError(FriendsFeedback, UiTextCatalog.Format(
                    "FriendRequestNoSuchAccount",
                    username));
                return;
            }

            StatusMessage = UiTextCatalog.Format(
                outcome is RemoteFriendRequestOutcome.Accepted
                    ? "FriendRequestAccepted"
                    : "FriendRequestSent",
                username);
            FriendUsernameQuery = string.Empty;
            await ReloadFriendsAsync();
        }
        catch (Exception exception)
        {
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendRequestFailed",
                exception.Message));
        }
        finally
        {
            IsFriendsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshFriendsAsync() => ReloadFriendsAsync();

    /// <summary>
    /// Saves the account-wide switches. Turning sharing off only clears what
    /// friends can see on the next sync, so the message says so rather than
    /// implying it has already gone.
    /// </summary>
    [RelayCommand]
    private async Task SaveFriendVisibilityAsync()
    {
        if (!SupportsFriends)
        {
            return;
        }

        try
        {
            IsFriendsBusy = true;
            SetError(FriendsFeedback, null);
            var saved = await _friendService.SetVisibilityAsync(
                new RemoteFriendVisibility(
                    ShareProgressWithFriends,
                    DiscoverableByUsername,
                    []));
            ShareProgressWithFriends = saved.ShareProgress;
            DiscoverableByUsername = saved.DiscoverableByUsername;
            StatusMessage = UiTextCatalog.Get(
                saved.ShareProgress
                    ? "FriendVisibilitySharingOn"
                    : "FriendVisibilitySharingOff");
        }
        catch (Exception exception)
        {
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendVisibilitySaveFailed",
                exception.Message));
        }
        finally
        {
            IsFriendsBusy = false;
        }
    }

    private async Task ApplyFriendSharingAsync(
        FriendViewModel friend,
        bool shareProgress)
    {
        try
        {
            SetError(FriendsFeedback, null);
            var applied = await _friendService.SetFriendSharingAsync(
                friend.AccountId,
                shareProgress);
            if (!applied)
            {
                friend.RevertSharing(!shareProgress);
                SetError(FriendsFeedback, UiTextCatalog.Format(
                    "FriendSharingSaveFailed",
                    friend.DisplayName));
            }
        }
        catch (Exception exception)
        {
            friend.RevertSharing(!shareProgress);
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendSharingSaveFailed",
                exception.Message));
        }
    }

    private async Task RemoveFriendAsync(FriendViewModel friend)
    {
        try
        {
            SetError(FriendsFeedback, null);
            if (await _friendService.RemoveFriendAsync(friend.AccountId))
            {
                Friends.Remove(friend);
                NotifyFriendCollectionsChanged();
                StatusMessage = UiTextCatalog.Format(
                    "FriendRemoved",
                    friend.DisplayName);
            }
        }
        catch (Exception exception)
        {
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendRemoveFailed",
                exception.Message));
        }
    }

    private async Task AcceptFriendRequestAsync(
        FriendRequestViewModel request)
    {
        try
        {
            SetError(FriendsFeedback, null);
            if (await _friendService.AcceptRequestAsync(request.AccountId))
            {
                StatusMessage = UiTextCatalog.Format(
                    "FriendRequestAccepted",
                    request.DisplayName);
            }

            await ReloadFriendsAsync();
        }
        catch (Exception exception)
        {
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendRequestFailed",
                exception.Message));
        }
    }

    private async Task DismissFriendRequestAsync(
        FriendRequestViewModel request)
    {
        try
        {
            SetError(FriendsFeedback, null);
            await _friendService.DeclineRequestAsync(request.AccountId);
            FriendRequests.Remove(request);
            NotifyFriendCollectionsChanged();
        }
        catch (Exception exception)
        {
            SetError(FriendsFeedback, UiTextCatalog.Format(
                "FriendRequestFailed",
                exception.Message));
        }
    }

    [RelayCommand]
    private async Task ShowUserListsAsync()
    {
        try
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            CancelDetailsLoad();
            IsBusy = true;
            SetError(ListsFeedback, null);
            StatusMessage = null;
            await CloseActivePlaybackAsync();
            LeaveLiveTv();
            CloseReaders();
            SelectedDetails = null;
            IsSettingsOpen = false;
            IsDownloadsOpen = false;
            _selectedMediaType = null;
            _selectedSubtypeGroup = null;
            IsUserListsOpen = true;
            NotifySectionNavigationChanged();
            SearchQuery = string.Empty;
            IsSearchActive = false;
            _searchResults = [];

            await ReloadUserListsAsync();
            var overview = await OffUiThread.ReadAsync(
                () => catalogBrowseService.GetOverviewAsync());
            ApplyOverview(overview, resetProviderFilter: true);
            SectionTitle =
                SelectedUserList?.Name ??
                UiTextCatalog.Get("UserListsTitle");
        }
        catch (Exception exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "UserListsLoadFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateUserListAsync()
    {
        if (!TryNormalizeUserListName(
                NewUserListName,
                exceptListId: null,
                out var normalizedName))
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetError(ListsFeedback, null);
            StatusMessage = null;
            var created = await UserMediaState.CreateUserListAsync(
                normalizedName);
            NewUserListName = string.Empty;
            await ReloadUserListsAsync(
                selectedBrowsingListId: created.Id,
                selectedDetailListId: created.Id);
            StatusMessage = UiTextCatalog.Format(
                "UserListCreated",
                created.Name);
        }
        catch (Exception exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "UserListSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RenameUserListAsync()
    {
        var selected = SelectedUserList;
        if (selected is null ||
            !TryNormalizeUserListName(
                RenameUserListName,
                selected.Id,
                out var normalizedName))
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetError(ListsFeedback, null);
            StatusMessage = null;
            var renamed = await UserMediaState.RenameUserListAsync(
                selected.Id,
                normalizedName);
            await ReloadUserListsAsync(
                selectedBrowsingListId: renamed.Id,
                selectedDetailListId: SelectedDetailUserList?.Id);
            StatusMessage = UiTextCatalog.Format(
                "UserListRenamed",
                renamed.Name);
        }
        catch (Exception exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "UserListSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestDeleteUserList()
    {
        if (SelectedUserList is not null)
        {
            IsDeleteUserListConfirmationVisible = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteUserList() =>
        IsDeleteUserListConfirmationVisible = false;

    [RelayCommand]
    private async Task ConfirmDeleteUserListAsync()
    {
        var selected = SelectedUserList;
        if (selected is null)
        {
            IsDeleteUserListConfirmationVisible = false;
            return;
        }

        try
        {
            IsBusy = true;
            SetError(ListsFeedback, null);
            StatusMessage = null;
            await UserMediaState.DeleteUserListAsync(selected.Id);
            IsDeleteUserListConfirmationVisible = false;
            await ReloadUserListsAsync(
                selectedDetailListId:
                    SelectedDetailUserList?.Id == selected.Id
                        ? null
                        : SelectedDetailUserList?.Id);
            StatusMessage = UiTextCatalog.Format(
                "UserListDeleted",
                selected.Name);
        }
        catch (Exception exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "UserListSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshSelectedItemUserLists()
    {
        var catalogItemId = SelectedDetails?.Details.Identity.CatalogItemId;
        ObservableCollectionSynchronizer.Synchronize(
            SelectedItemUserLists,
            catalogItemId is { } id
                ? UserLists.Where(list => list.Contains(id)).ToArray()
                : []);
        OnPropertyChanged(nameof(HasSelectedItemUserLists));
    }

    /// <summary>
    /// Takes the open title off one named list, without it having to be the
    /// list currently chosen in the picker.
    /// </summary>
    [RelayCommand]
    private async Task RemoveSelectedItemFromListAsync(
        UserMediaListViewModel? list)
    {
        var details = SelectedDetails;
        if (details is null || list is null)
        {
            return;
        }

        var catalogItemId = details.Details.Identity.CatalogItemId;
        try
        {
            IsBusy = true;
            SetError(DetailsFeedback, null);
            StatusMessage = null;
            await UserMediaState.SetUserListMembershipAsync(
                list.Id,
                catalogItemId,
                isMember: false);
            await ReloadUserListsAsync(
                selectedBrowsingListId: SelectedUserList?.Id,
                selectedDetailListId: SelectedDetailUserList?.Id);
            StatusMessage = UiTextCatalog.Format(
                "ItemRemovedFromUserList",
                details.Name,
                list.Name);
        }
        catch (Exception exception)
        {
            SetError(DetailsFeedback, UiTextCatalog.Format(
                "UserListSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleSelectedItemUserListAsync()
    {
        var details = SelectedDetails;
        var selectedList = SelectedDetailUserList;
        if (details is null || selectedList is null)
        {
            return;
        }

        var catalogItemId = details.Details.Identity.CatalogItemId;
        var shouldAdd = !selectedList.Contains(catalogItemId);
        try
        {
            IsBusy = true;
            SetError(DetailsFeedback, null);
            StatusMessage = null;
            await UserMediaState.SetUserListMembershipAsync(
                selectedList.Id,
                catalogItemId,
                shouldAdd);
            await ReloadUserListsAsync(
                selectedBrowsingListId: SelectedUserList?.Id,
                selectedDetailListId: selectedList.Id);
            StatusMessage = UiTextCatalog.Format(
                shouldAdd
                    ? "ItemAddedToUserList"
                    : "ItemRemovedFromUserList",
                details.Name,
                selectedList.Name);
        }
        catch (Exception exception)
        {
            SetError(DetailsFeedback, UiTextCatalog.Format(
                "UserListMembershipSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenUserListsFromDetailsAsync() =>
        ShowUserListsAsync();

    /// <summary>
    /// Dismisses settings, leaving whatever it was covering exactly as it was.
    /// </summary>
    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void ShowDonationPrompt()
    {
        IsDonationAddressCopied = false;
        IsDonationPromptVisible = true;
    }

    [RelayCommand]
    private void DismissDonationPrompt()
    {
        IsDonationPromptVisible = false;
        IsDonationAddressCopied = false;
    }

    public void MarkDonationAddressCopied() =>
        IsDonationAddressCopied = true;

    /// <summary>
    /// Drops the list filter, which is how the catalog goes back to showing
    /// everything — there is no "all lists" row to pick.
    /// </summary>
    [RelayCommand]
    private void ClearCatalogListFilter() =>
        SelectedCatalogListFilter = null;

    [RelayCommand]
    private void ToggleNavigation() =>
        IsNavigationCollapsed = !IsNavigationCollapsed;

    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        SetError(CatalogFeedback, null);
        StatusMessage = null;
        await CloseActivePlaybackAsync();
        LeaveLiveTv();

        // Playback stops because it makes noise over a layer the viewer is
        // reading; an open chapter does neither and is deliberately kept, along
        // with the open title. Settings sits over whatever the viewer was
        // looking at, so closing it puts them back where they were rather than
        // at the top of the catalog with their place lost. Tearing the reader
        // down here is what dropped viewers onto the title page on close.
        IsDownloadsOpen = false;
        IsSettingsOpen = true;
    }

    /// <summary>
    /// The criteria the pickers currently describe. Genre and year belong here
    /// because they send a new search; category and provider do not, because
    /// they only narrow what came back.
    /// </summary>
    private CatalogSearchSelection CurrentSearchSelection => new(
        SearchQuery,
        _selectedMediaType,
        _selectedSubtypeGroup,
        SelectedGenreFilter?.Genre,
        SelectedYearFilter?.Year);

    /// <summary>
    /// Whether there is anything to search for. A genre or a year is enough on
    /// its own; text is no longer required.
    /// </summary>
    public bool HasSearchCriteria => CurrentSearchSelection.HasCriteria;

    /// <summary>
    /// Whether a picker is narrowing the catalog without being something the
    /// sources can be asked about.
    /// <para>
    /// Provider and category describe what came back rather than what to go and
    /// fetch — the category list is built from the results themselves, so there
    /// is no query to send for one. Searching on them alone is therefore a
    /// narrower view of the catalog, not a request.
    /// </para>
    /// </summary>
    public bool HasCatalogFilters =>
        HasSelectedProvider ||
        SelectedCategoryFilter?.Category is not null ||
        SelectedStatusFilter?.Status is not null ||
        ShowFavoritesOnly;

    /// <summary>
    /// Whether anything at all is narrowing the catalog, by search or by
    /// picker. What the clear action offers to undo: without the pickers
    /// counted, selecting only a category left no way to get back.
    /// </summary>
    public bool HasAnyCatalogSelection =>
        HasSearchCriteria || HasCatalogFilters;

    /// <summary>
    /// Whether favourites may be answered from the whole library: the page has
    /// to be the one about the whole library, and nothing else may be narrowing
    /// it. On a media-type page the page is itself the question, so favourites
    /// are answered from that page's own titles.
    /// <para>
    /// The provider picker is deliberately not counted: narrowing favourites to
    /// one provider is still a question about favourites, and the provider list
    /// applies across the library just as well.
    /// </para>
    /// </summary>
    private bool IsFavoritesOnlySelection =>
        IsAllSection &&
        !HasSearchCriteria &&
        SelectedCategoryFilter?.Category is null &&
        SelectedStatusFilter?.Status is null &&
        SelectedCatalogListFilter is null;

    [RelayCommand]
    private async Task SearchCatalogAsync()
    {
        if (!HasSearchCriteria)
        {
            // An empty box with filters set is still an ask: show the catalog
            // those filters describe. Clearing here threw the selection away
            // and put the viewer back at the unfiltered catalog, which read as
            // the button undoing what they had just set up.
            if (HasCatalogFilters)
            {
                if (IsSearchActive)
                {
                    LeaveSearchResults();
                }

                ApplyCatalogItems(resetPage: true);
                return;
            }

            ClearSearch();
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var searchCancellation = new CancellationTokenSource();
        _searchCancellation = searchCancellation;
        var generation = ++_searchGeneration;
        CancelDetailsLoad();
        try
        {
            IsBusy = true;
            SetError(CatalogFeedback, null);
            StatusMessage = null;
            CloseReaders();
            SelectedDetails = null;
            IsSettingsOpen = false;
            IsDownloadsOpen = false;
            IsUserListsOpen = false;
            IsFriendsOpen = false;
            NotifySectionNavigationChanged();

            var selection = CurrentSearchSelection;
            var criteria = selection.ToCriteria();
            var criteriaLabel = selection.Describe();
            var results =
                new List<ContentSourceCatalogSearchResult>();
            var failures = new List<ContentSourceSearchFailure>();
            IsSearchActive = true;
            var firstBatch = true;
            await foreach (var batch in
                           contentSourceCatalogService.SearchStreamingAsync(
                               criteria,
                               searchCancellation.Token))
            {
                // A superseded run must publish nothing. Cancellation is not
                // observed until the next provider yields, so without this the
                // loser of two overlapping searches wakes up and writes its
                // results and heading over the winner's.
                if (generation != _searchGeneration)
                {
                    return;
                }

                results.AddRange(batch.Results);
                if (batch.Failure is not null)
                {
                    failures.Add(batch.Failure);
                }

                _searchResults = results.ToArray();
                SectionTitle = UiTextCatalog.Format(
                    "SearchResultsTitle",
                    criteriaLabel,
                    results.Count);
                // A search narrows what the user was already looking at, so
                // the provider, category and favourites choices carry into it
                // rather than being wiped. Each is re-resolved against the new
                // results and only falls back to "all" when what it named is
                // genuinely not there. Only the page number resets, because a
                // new result set makes the old one meaningless.
                RebuildProviderFilters(
                    selectedContributorKey:
                        SelectedProviderFilter?.ContributorKey,
                    resetCatalogFilters: false,
                    resetPage: firstBatch);
                firstBatch = false;

                // Local results are the first batch. Release the blocking
                // spinner while online providers continue to fill the grid.
                if (batch.IsLocal)
                {
                    IsBusy = false;
                }
            }

            SetError(CatalogFeedback, failures.Count == 0
                ? null
                : UiTextCatalog.Format("SearchCompletedWithFailures", results.Count, failures.Count));
        }
        catch (OperationCanceledException)
            when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "SearchFailed",
                exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, searchCancellation))
            {
                _searchCancellation = null;
            }

            // Released whenever this run owned the spinner, even if something
            // else has already cleared the cancellation field. Tying it to
            // ownership of that field meant a Clear during a running search
            // left IsBusy true for the rest of the session, with Search and
            // Clear both disabled and no way back.
            if (generation == _searchGeneration)
            {
                IsBusy = false;
            }

            searchCancellation.Dispose();
        }
    }

    /// <summary>
    /// Retires the running search and puts the browse grid back, leaving every
    /// picker exactly as it was.
    /// <para>
    /// Separate from <see cref="ClearSearch"/> because that resets the pickers
    /// as well, which is right when the viewer asks to clear and wrong when
    /// they are searching on the pickers alone.
    /// </para>
    /// </summary>
    private void LeaveSearchResults()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        // Retires whatever run was in flight so its finally releases the
        // spinner and its batches stop publishing.
        _searchGeneration++;
        IsBusy = false;
        SearchQuery = string.Empty;
        IsSearchActive = false;
        _searchResults = [];
        SectionTitle = _browseSectionTitle;
        StatusMessage = null;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        LeaveSearchResults();

        // Genre and year survive a filter rebuild by design, so clearing the
        // search has to drop them explicitly. Suppressed so neither reset
        // launches a fresh search on the way out.
        _isRebuildingCatalogFilters = true;
        try
        {
            SelectedGenreFilter = GenreFilters.FirstOrDefault(
                static filter => filter.Genre is null);
            SelectedYearFilter = YearFilters.FirstOrDefault(
                static filter => filter.Year is null);
        }
        finally
        {
            _isRebuildingCatalogFilters = false;
        }

        RebuildProviderFilters(
            selectedContributorKey: null,
            resetCatalogFilters: true);
    }

    [RelayCommand]
    private async Task AddTorrentProviderAsync()
    {
        await _contentSourceSaveGate.WaitAsync();
        try
        {
            IsBusy = true;
            SetError(SourcesFeedback, null);
            StatusMessage = null;
            var provider = ContentSourceCatalogService.CreateTorrentProvider(
                NewTorrentProviderName,
                NewTorrentProviderBaseUrl);
            var existing = ContentSourceProviders
                .Select(static row => row.ToConfiguration())
                .ToArray();

            // Every added provider gets a fresh generated id, so the settings
            // layer's uniqueness rule — which is on id — can never fire. The
            // endpoint is what actually matters: a duplicate is fetched twice
            // on every search and its results concatenated, so the same title
            // appears twice with two identical provider filters.
            if (existing.Any(candidate =>
                    string.Equals(
                        candidate.BaseUrl,
                        provider.BaseUrl,
                        StringComparison.OrdinalIgnoreCase)))
            {
                SetError(SourcesFeedback, UiTextCatalog.Get("ContentSourceDuplicate"));
                return;
            }

            var settings = new ContentSourceProviderSettings
            {
                Providers = existing.Append(provider).ToArray()
            };
            ApplyContentSourceSettings(
                await contentSourceCatalogService.SaveSettingsAsync(settings));
            NewTorrentProviderName = string.Empty;
            NewTorrentProviderBaseUrl = string.Empty;
            StatusMessage = UiTextCatalog.Get("ContentSourceAdded");
        }
        catch (Exception exception)
        {
            SetError(SourcesFeedback, UiTextCatalog.Format(
                "ContentSourceSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
            _contentSourceSaveGate.Release();
        }
    }

    [RelayCommand]
    private async Task SaveContentSourcesAsync()
    {
        try
        {
            IsBusy = true;
            SetError(SourcesFeedback, null);
            StatusMessage = null;
            var saved = await contentSourceCatalogService.SaveSettingsAsync(
                new ContentSourceProviderSettings
                {
                    Providers = ContentSourceProviders
                        .Select(static row => row.ToConfiguration())
                        .ToArray()
                });
            ApplyContentSourceSettings(saved);
            _hasLoadedLiveTv = false;
            StatusMessage = UiTextCatalog.Get("ContentSourcesSaved");
        }
        catch (Exception exception)
        {
            SetError(SourcesFeedback, UiTextCatalog.Format(
                "ContentSourceSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveContentSourceAsync(
        ContentSourceProviderRowViewModel? provider)
    {
        if (provider is null || !provider.CanDelete)
        {
            return;
        }

        // Serialized against the other writers of this list. Each save
        // snapshots the whole collection before its await, so two overlapping
        // removals each wrote a list captured before the other ran and the
        // loser's provider came back from the database.
        await _contentSourceSaveGate.WaitAsync();
        try
        {
            IsBusy = true;
            SetError(SourcesFeedback, null);
            StatusMessage = null;
            var saved = await contentSourceCatalogService.SaveSettingsAsync(
                new ContentSourceProviderSettings
                {
                    Providers = ContentSourceProviders
                        .Where(row => !ReferenceEquals(row, provider))
                        .Select(static row => row.ToConfiguration())
                        .ToArray()
                });
            ApplyContentSourceSettings(saved);
            StatusMessage = UiTextCatalog.Get("ContentSourceRemoved");
        }
        catch (Exception exception)
        {
            SetError(SourcesFeedback, UiTextCatalog.Format(
                "ContentSourceSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
            _contentSourceSaveGate.Release();
        }
    }

    [RelayCommand]
    private void ToggleFavoritesFilter() =>
        ShowFavoritesOnly = !ShowFavoritesOnly;

    [RelayCommand(CanExecute = nameof(CanGoPreviousCatalogPage))]
    private void PreviousCatalogPage()
    {
        if (!CanGoPreviousCatalogPage)
        {
            return;
        }

        CatalogPage--;
        ApplyCatalogItems();
    }

    [RelayCommand(CanExecute = nameof(CanGoNextCatalogPage))]
    private void NextCatalogPage()
    {
        if (!CanGoNextCatalogPage)
        {
            return;
        }

        CatalogPage++;
        ApplyCatalogItems();
    }

    private async Task SetCatalogItemFavoriteAsync(
        Guid catalogItemId,
        bool isFavorite)
    {
        try
        {
            IsBusy = true;
            SetError(SelectedDetails is not null ? DetailsFeedback : CatalogFeedback, null);
            StatusMessage = null;
            await UserMediaState.SetFavoriteCatalogItemAsync(
                catalogItemId,
                isFavorite);
            if (isFavorite)
            {
                _favoriteCatalogItemIds.Add(catalogItemId);
            }
            else
            {
                _favoriteCatalogItemIds.Remove(catalogItemId);
            }

            OnPropertyChanged(nameof(SelectedItemIsFavorite));
            OnPropertyChanged(
                nameof(SelectedItemFavoriteActionText));
            OnPropertyChanged(
                nameof(SelectedItemFavoriteGlyph));
            ApplyCatalogItems();
        }
        catch (Exception exception)
        {
            SetError(SelectedDetails is not null ? DetailsFeedback : CatalogFeedback, UiTextCatalog.Format(
                "FavoriteItemSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task ToggleSelectedItemFavoriteAsync()
    {
        var itemId = SelectedDetails?.Details.Item.Id;
        return itemId is null
            ? Task.CompletedTask
            : SetCatalogItemFavoriteAsync(
                itemId.Value,
                !_favoriteCatalogItemIds.Contains(
                    itemId.Value));
    }

    private async Task<bool> SetContentSeenAsync(
        CatalogContentIdentity content,
        bool isSeen)
    {
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(content), null);
            StatusMessage = null;
            // Marking a chapter seen implies the ones before it were too.
            var affected = isSeen
                ? ResolveUnseenUpTo(content)
                : [content];
            foreach (var unit in affected)
            {
                await SaveContentSeenCoreAsync(unit, isSeen);
            }

            if (isSeen && affected.Count == 0)
            {
                await SaveContentSeenCoreAsync(content, isSeen);
            }

            RebuildLatestProgressIndex();
            ApplyCatalogItems();
            RefreshSelectedDetailsSeenState();

            return true;
        }
        catch (Exception exception)
        {
            SetError(EntryFeedback(content), UiTextCatalog.Format(
                "UnitSeenSaveFailed",
                exception.Message));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> SetAllContentSeenAsync(
        IReadOnlyList<CatalogContentIdentity> contents,
        bool isSeen)
    {
        try
        {
            IsBusy = true;
            SetError(ContentFeedback, null);
            StatusMessage = null;
            foreach (var content in contents)
            {
                await SaveContentSeenCoreAsync(
                    content,
                    isSeen);
            }

            RebuildLatestProgressIndex();
            ApplyCatalogItems();

            return true;
        }
        catch (Exception exception)
        {
            SetError(ContentFeedback, UiTextCatalog.Format(
                "UnitSeenSaveFailed",
                exception.Message));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsContentSeen(CatalogContentIdentity content) =>
        _catalogProgressByContentKey
            .GetValueOrDefault(content.StableKey)
            ?.Progress.IsCompleted is true;

    private IReadOnlyList<CatalogContentIdentity> ResolveUnseenUpTo(
        CatalogContentIdentity target) =>
        CatalogSeenPlanner.ResolveUnseenUpTo(
            SelectedDetails?.Details,
            target,
            IsContentSeen);

    /// <summary>
    /// Marks a chapter or episode seen because the user opened it, together with
    /// everything before it. Silent: opening content is not a settings action.
    /// </summary>
    private async Task MarkContentEnteredAsync(CatalogContentIdentity content)
    {
        if (!content.IsUnit)
        {
            return;
        }

        var pending = ResolveUnseenUpTo(content);
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var unit in pending)
            {
                await SaveContentSeenCoreAsync(unit, isSeen: true);
            }

            RebuildLatestProgressIndex();
            ApplyCatalogItems();
            RefreshSelectedDetailsSeenState();
        }
        catch (Exception exception)
        {
            SetError(ContentFeedback, UiTextCatalog.Format(
                "UnitSeenSaveFailed",
                exception.Message));
        }
    }

    private async Task RecordCatalogItemViewedAsync(
        CatalogContentIdentity catalogItem)
    {
        var previous = _catalogProgressByContentKey
            .GetValueOrDefault(catalogItem.StableKey);
        var progress = new MediaProgressState(
            previous?.Progress.Position ?? 0,
            previous?.Progress.PositionKind ??
            MediaPositionKind.Seconds,
            previous?.Progress.IsCompleted ?? false,
            DateTimeOffset.UtcNow);
        var summary = new CatalogProgressSummary(
            catalogItem,
            progress);
        _catalogProgressByContentKey[catalogItem.StableKey] = summary;
        RebuildLatestProgressIndex();
        try
        {
            await UserMediaState.SaveProgressAsync(
                catalogItem,
                progress);
        }
        catch
        {
            // Keep it in this session's recent shelf. A later progress save or
            // sync can make the view durable without blocking the details page.
        }
    }

    private void RefreshSelectedDetailsSeenState()
    {
        if (SelectedDetails?.Details.Item.Id is not { } itemId)
        {
            return;
        }

        SelectedDetails.ApplySeenState(
            _catalogProgressByContentKey.Values
                .Where(summary =>
                    summary.CatalogItemId == itemId &&
                    summary.Progress.IsCompleted)
                .Select(static summary => summary.Content.StableKey)
                .ToHashSet(StringComparer.Ordinal));
    }

    private async Task SaveContentSeenCoreAsync(
        CatalogContentIdentity content,
        bool isSeen)
    {
        var previous = _catalogProgressByContentKey
            .GetValueOrDefault(content.StableKey);
        var progress = new MediaProgressState(
            isSeen
                ? previous?.Progress.Position ?? 0
                : 0,
            previous?.Progress.PositionKind ??
            (content.UnitKind is ContentUnitKind.Chapter
                ? MediaPositionKind.Page
                : MediaPositionKind.Seconds),
            isSeen,
            DateTimeOffset.UtcNow);
        await UserMediaState.SaveProgressAsync(
            content,
            progress);
        _catalogProgressByContentKey[content.StableKey] =
            new CatalogProgressSummary(content, progress);
    }

    private void RebuildLatestProgressIndex()
    {
        _catalogRecentActivityByItemId =
            CatalogProgressIndex.CreateRecent(
                _catalogProgressByContentKey.Values);
        _catalogProgressByItemId =
            CatalogProgressIndex.CreateResumable(
                _catalogProgressByContentKey.Values);
    }

    [RelayCommand]
    private async Task CloseDetailsAsync()
    {
        CancelDetailsLoad();
        CloseReaders();
        SelectedDetails = null;
        await ReloadCatalogUserStateAsync();
        RebuildProviderFilters(
            SelectedProviderFilter?.ContributorKey);
    }

    private async Task<bool> DeleteCatalogEntryAsync(CatalogContentIdentity identity)
    {
        var selected = SelectedDetails;
        if (IsBusy || selected is null || selected.Details.Item.Id != identity.CatalogItemId ||
            !selected.Entries.Any(entry => entry.Identity == identity)) return false;
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(identity), null);
            StatusMessage = null;
            if (_activePlaybackIdentity == identity || _activeApiVideoSelection?.ContentIdentity == identity)
                await CloseActivePlaybackAsync();
            var deleted = await OffUiThread.ReadAsync(() => catalogBrowseService.DeleteUnitAsync(identity));
            if (!deleted) throw new KeyNotFoundException(UiTextCatalog.Get("CatalogEntryMissing"));

            var details = await OffUiThread.ReadAsync(() => catalogBrowseService.GetDetailsAsync(identity.CatalogItemId));
            await ReloadCatalogUserStateAsync();
            await RefreshAsync();
            // A slow database must not reopen a title the viewer navigated away from.
            if (ReferenceEquals(SelectedDetails, selected) && details is not null)
            {
                var replacement = CreateDetailsViewModel(details, selected.ContentSourceName,
                    selected.TrustedRemoteOrigins, selected.ApiImageSets, selected.ApiVideoSets,
                    selected.ArtworkReferrer, selected.ContentSourceProviderId);
                if (selected.SelectedSeason?.SeasonLabel is { } season)
                    replacement.SelectedSeason = replacement.SeasonOptions.FirstOrDefault(option => option.SeasonLabel == season)
                        ?? replacement.SelectedSeason;
                SelectedDetails = replacement;
            }
            StatusMessage = UiTextCatalog.Get("CatalogEntryDeleted");
            return true;
        }
        catch (Exception exception)
        {
            SetError(EntryFeedback(identity), UiTextCatalog.Format("CatalogEntryDeleteFailed", exception.Message));
            return false;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RequestDeleteCatalogItem()
    {
        if (SelectedDetails is not null)
        {
            IsCatalogItemDeleteConfirmationVisible = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteCatalogItem() =>
        IsCatalogItemDeleteConfirmationVisible = false;

    [RelayCommand]
    private async Task ConfirmDeleteCatalogItemAsync()
    {
        var selected = SelectedDetails;
        if (selected is null)
        {
            IsCatalogItemDeleteConfirmationVisible = false;
            return;
        }

        var itemId = selected.Details.Item.Id;
        var itemName = selected.Details.Item.Name;
        try
        {
            IsBusy = true;
            SetError(ContentFeedback, null);
            StatusMessage = null;
            var deleted = await catalogBrowseService.DeleteAsync(itemId);
            if (!deleted)
            {
                throw new KeyNotFoundException(
                    UiTextCatalog.Get("CatalogItemMissing"));
            }

            await CloseActivePlaybackAsync();
            CancelDetailsLoad();
            CloseReaders();
            SelectedDetails = null;
            _allCatalogCards = _allCatalogCards
                .Where(card => card.Id != itemId)
                .ToArray();
            await ReloadCatalogUserStateAsync();
            await RefreshAsync();
            StatusMessage = UiTextCatalog.Format(
                "CatalogItemDeleted",
                itemName);
        }
        catch (Exception exception)
        {
            SetError(ContentFeedback, UiTextCatalog.Format(
                "CatalogItemDeleteFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Starts a details load and cancels the one before it. The caller checks
    /// the returned scope before touching the UI, so a load the user has
    /// already navigated away from leaves no trace.
    /// </summary>
    private CancellationTokenSource BeginDetailsLoad()
    {
        CancelDetailsLoad();
        var cancellation = new CancellationTokenSource();
        _detailsLoadCancellation = cancellation;
        return cancellation;
    }

    private void CancelDetailsLoad()
    {
        var cancellation = _detailsLoadCancellation;
        _detailsLoadCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellation.Dispose();
        IsBusy = false;
    }

    /// <summary>
    /// True while this load is still the one the user is waiting for.
    /// </summary>
    private bool IsCurrentDetailsLoad(CancellationTokenSource cancellation) =>
        ReferenceEquals(_detailsLoadCancellation, cancellation) &&
        !cancellation.IsCancellationRequested;

    private void EndDetailsLoad(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_detailsLoadCancellation, cancellation))
        {
            // A superseded load must not switch the busy indicator off
            // underneath the navigation that replaced it.
            return;
        }

        IsBusy = false;
        _detailsLoadCancellation = null;
        cancellation.Dispose();
    }

    [RelayCommand]
    private void CloseReader()
    {
        CloseReaders();
    }

    [RelayCommand]
    private Task OpenPreviousReaderChapterAsync() =>
        OpenAdjacentReaderChapterAsync(-1);

    [RelayCommand]
    private Task OpenNextReaderChapterAsync() =>
        OpenAdjacentReaderChapterAsync(1);

    [RelayCommand]
    private void ReturnToManga()
    {
        CloseReaders();
    }

    [RelayCommand]
    private Task OpenPreviousEpisodeAsync() => OpenAdjacentEpisodeAsync(-1);

    [RelayCommand]
    private Task OpenNextEpisodeAsync() => OpenAdjacentEpisodeAsync(1);

    private async ValueTask ShowEmbeddedMediaSurfaceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var embeddedView = platformMediaPlayer.EmbeddedView;
        IsEmbeddedMediaOpen = embeddedView is not null || _activeApiVideoSelection is not null;
        OnPropertyChanged(nameof(EmbeddedMediaView));
        if (embeddedView is null)
        {
            return;
        }

        // LibVLC must see the Avalonia VideoView's Windows child handle before
        // Play is called. Starting playback first makes VLC create its own
        // top-level output window instead of rendering inside the application.
        //
        // Closing the previous item hid the host grid, which destroyed that
        // handle; showing it again only queues the arrange pass that rebuilds
        // it, and a grid whose toolbar content just changed routinely needs
        // more than one layout cycle. So this waits for the surface to be laid
        // out rather than assuming one dispatcher turn covered it.
        var deadline = Environment.TickCount64 +
            (long)EmbeddedSurfaceTimeout.TotalMilliseconds;
        while (true)
        {
            await Dispatcher.UIThread.InvokeAsync(
                static () => { },
                DispatcherPriority.Background);
            cancellationToken.ThrowIfCancellationRequested();
            if (embeddedView.IsEffectivelyVisible &&
                embeddedView.Bounds is { Width: > 0, Height: > 0 })
            {
                return;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return;
            }

            await Task.Delay(EmbeddedSurfacePollInterval, cancellationToken);
        }
    }

    [RelayCommand]
    private Task CloseMediaPlayerAsync() => CloseActivePlaybackAsync();

    [RelayCommand]
    private void TogglePlayerFullscreen()
    {
        if (IsEmbeddedMediaOpen)
        {
            IsPlayerFullscreen = !IsPlayerFullscreen;
        }
    }

    [RelayCommand]
    private Task OpenMagnetAsync() => HandleMagnetAsync(MagnetLinkInput, userConfirmed: true);

    [RelayCommand]
    private Task OpenPendingMagnetAsync() =>
        HandleMagnetAsync(PendingMagnetLink ?? string.Empty, userConfirmed: true);

    [RelayCommand]
    private async Task SaveMagnetOptionsAsync()
    {
        try
        {
            IsBusy = true;
            SetError(MagnetFeedback, null);
            await magnetOpenService.SavePreferencesAsync(new MagnetOpenPreferences(
                OpenMagnetsAutomatically,
                ImportTorrentInfoJson,
                TreatExternalMagnetsAsPrivate));
            StatusMessage = UiTextCatalog.Get("MagnetOptionsSaved");
        }
        catch (Exception exception)
        {
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "MagnetOptionsSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveMetadataOptionsAsync()
    {
        try
        {
            IsBusy = true;
            SetError(MetadataFeedback, null);
            StatusMessage = null;
            var settings = new MetadataProviderSettings
            {
                TmdbApiKey = TmdbApiKey,
                MyAnimeListClientId = MyAnimeListClientId,
                PreferredLanguage = MetadataLanguage
            }.Normalize();
            await metadataProviderSettingsStore.SaveAsync(settings);
            ApplyMetadataSettings(settings);
            StatusMessage = UiTextCatalog.Get("MetadataOptionsSaved");
        }
        catch (Exception exception)
        {
            SetError(MetadataFeedback, UiTextCatalog.Format(
                "MetadataOptionsSaveFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshMetadataAsync()
    {
        var selectedDetails = SelectedDetails;
        if (selectedDetails is null || !selectedDetails.CanRefreshMetadata)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetError(DetailsFeedback, null);
            StatusMessage = null;
            var result = await catalogMetadataRefreshService.RefreshAsync(
                selectedDetails.Details.Identity.CatalogItemId);
            await RefreshAsync();
            SelectedDetails = CreateDetailsViewModel(
                result.Details,
                selectedDetails.ContentSourceName,
                selectedDetails.TrustedRemoteOrigins,
                selectedDetails.ApiImageSets,
                selectedDetails.ApiVideoSets,
                selectedDetails.ArtworkReferrer,
                selectedDetails.ContentSourceProviderId);
            StatusMessage = UiTextCatalog.Format(
                "MetadataUpdated",
                result.ProviderName);
        }
        catch (Exception exception)
        {
            SetError(DetailsFeedback, UiTextCatalog.Format(
                "MetadataUpdateFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenTorrentFileAsync(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var torrentActivityId = torrentActivityCoordinator.Begin(
            UiTextCatalog.Get("TorrentActivityTorrentFile"),
            UiTextCatalog.Get("TorrentActivityReadingFile"));

        try
        {
            IsBusy = true;
            SetError(MagnetFeedback, null);
            StatusMessage = null;

            var result = await magnetOpenService.ReceiveTorrentAsync(source);
            switch (result.Status)
            {
                case TorrentFileOpenStatus.EngineUnavailable:
                    torrentActivityCoordinator.Dismiss(torrentActivityId);
                    SetError(MagnetFeedback, UiTextCatalog.Format(
                        "TorrentFileEngineUnavailable",
                        TorrentEngineName));
                    break;

                case TorrentFileOpenStatus.AlreadyOpen:
                    if (result.SessionId is { } existingSessionId)
                    {
                        torrentActivityId =
                            torrentActivityCoordinator.AttachSession(
                                torrentActivityId,
                                existingSessionId,
                                result.Metadata.DisplayName);
                    }
                    StatusMessage =
                        UiTextCatalog.Get("TorrentFileAlreadyOpen");
                    break;

                case TorrentFileOpenStatus.Opened:
                    if (result.SessionId is { } sessionId)
                    {
                        torrentActivityId =
                            torrentActivityCoordinator.AttachSession(
                                torrentActivityId,
                                sessionId,
                                result.Metadata.DisplayName);
                    }
                    StatusMessage = BuildTorrentOpenedMessage(
                        result.ManifestImport,
                        result.Warning);
                    if (result.ManifestImport?.Status is
                        TorrentManifestImportStatus.Imported)
                    {
                        await RefreshAsync();
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(result.Status));
            }
        }
        catch (TorrentMetadataFormatException exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "TorrentFileInvalid",
                exception.Message));
        }
        catch (MagnetCompatibilityException exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "TorrentFileIncompatible",
                exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "TorrentFileUnsafe",
                exception.Message));
        }
        catch (Exception exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "TorrentFileOpenFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportCatalogJsonAsync(Stream source, string? sourceName)
    {
        try
        {
            IsBusy = true;
            SetError(FilesFeedback, null);
            StatusMessage = null;

            var result = await catalogTransferService.ImportAsync(source, sourceName);
            StatusMessage = UiTextCatalog.Format(
                "CatalogImported",
                result.CreatedCount,
                result.UpdatedCount);
            await RefreshAsync();
        }
        catch (CatalogTransferValidationException exception)
        {
            SetError(FilesFeedback, UiTextCatalog.Format(
                "CatalogInvalid",
                string.Join(" ", exception.Errors.Take(3))));
        }
        catch (Exception exception)
        {
            SetError(FilesFeedback, UiTextCatalog.Format(
                "CatalogImportFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportCatalogJsonAsync(Stream destination)
    {
        try
        {
            IsBusy = true;
            SetError(FilesFeedback, null);
            StatusMessage = null;

            await catalogTransferService.ExportAsync(destination);
            await destination.FlushAsync();
            StatusMessage = UiTextCatalog.Get("CatalogExported");
        }
        catch (Exception exception)
        {
            SetError(FilesFeedback, UiTextCatalog.Format(
                "CatalogExportFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportFullDatabaseAsync(Stream destination)
    {
        if (fullDatabaseBackupService is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetError(BackupFeedback, null);
            StatusMessage = null;

            await fullDatabaseBackupService.ExportAsync(destination);
            StatusMessage = UiTextCatalog.Get("BackupExported");
        }
        catch (Exception exception)
        {
            SetError(BackupFeedback, UiTextCatalog.Format(
                "BackupExportFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RestoreFullDatabaseAsync(Stream source)
    {
        if (fullDatabaseBackupService is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetError(BackupFeedback, null);
            StatusMessage = null;

            var result = await fullDatabaseBackupService.RestoreAsync(source);
            StatusMessage = UiTextCatalog.Format(
                "BackupRestored",
                result.SafetyBackupPath);
            await ReloadCatalogUserStateAsync();
            await ReloadUserListsAsync();
            await RefreshAsync();
            // The restored database carries whatever session the backup was
            // taken with. Overwrite that row with the session actually running
            // here, so a backup can never hand its token to another machine.
            await SaveRemoteSessionAsync(remoteMediaServerClient.Session);
        }
        catch (Exception exception)
        {
            SetError(BackupFeedback, UiTextCatalog.Format(
                "BackupRestoreFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <param name="reloadUserState">
    /// Whether to re-read favourites, progress and latest units before applying
    /// the overview. Only the startup path passes false, and only when it has
    /// just read them successfully itself: that read is three database
    /// round-trips including the whole-catalog latest-unit query, and running it
    /// twice before the first frame was pure duplicate work on the slowest
    /// device. Every other caller needs the refresh.
    /// </param>
    private async Task LoadAsync(
        MediaType? mediaType,
        string sectionTitle,
        MediaSubtypeGroup? subtypeGroup = null,
        bool reloadUserState = true)
    {
        try
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            CancelDetailsLoad();
            IsBusy = true;
            SetError(CatalogFeedback, null);
            await CloseActivePlaybackAsync();
            LeaveLiveTv();
            _selectedMediaType = mediaType;
            _selectedSubtypeGroup = subtypeGroup;
            IsUserListsOpen = false;
            IsFriendsOpen = false;
            IsDownloadsOpen = false;
            NotifySectionNavigationChanged();
            _browseSectionTitle = sectionTitle;
            SectionTitle = sectionTitle;
            SearchQuery = string.Empty;
            IsSearchActive = false;
            _searchResults = [];
            CloseReaders();
            SelectedDetails = null;
            IsSettingsOpen = false;

            if (reloadUserState)
            {
                await ReloadCatalogUserStateAsync();
            }

            var overview = await OffUiThread.ReadAsync(
                () => catalogBrowseService.GetOverviewAsync(mediaType));

            ApplyOverview(overview, resetProviderFilter: true);
        }
        catch (Exception exception)
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "CatalogLoadFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadHomeAsync(bool reloadUserState = true)
    {
        await LoadAsync(
            null,
            UiTextCatalog.Get("SectionAll"),
            reloadUserState: reloadUserState);
        if (HasError)
        {
            return;
        }

        // Paired with the refresh-complete snapshot so the daily refresh's own
        // contribution to the footprint can be read off as a delta.
        MemoryDiagnostics.Snapshot("catalog-loaded", DescribeRetainedCatalog());
        StartDailyUpdatesRefresh();
        EnsureHourlyProviderRefreshScheduler();
    }

    /// <summary>
    /// What the catalog is holding, so a footprint can be divided by the things
    /// that produced it. The whole catalog is resident: the store has no paged
    /// read, so pagination only narrows what is displayed.
    /// </summary>
    private string DescribeRetainedCatalog()
    {
        var alternativeNames = 0;
        var contributors = 0;
        // Characters rather than counts for the free text, because a scraped
        // description can be a whole page of markup and would dominate the
        // retained set without changing any of the counts around it.
        long descriptionCharacters = 0;
        long nameCharacters = 0;
        foreach (var card in _catalogCards)
        {
            alternativeNames += card.AlternativeNames.Count;
            contributors += card.Contributors.Count;
            descriptionCharacters += card.Description.Length;
            nameCharacters += card.Name.Length;
        }

        return $"cards={_catalogCards.Count} " +
            $"alt-names={alternativeNames} " +
            $"contributors={contributors} " +
            $"desc-chars={descriptionCharacters} " +
            $"desc-mib={descriptionCharacters * 2 / (1024d * 1024d):F1} " +
            $"name-chars={nameCharacters} " +
            $"card-vms={_catalogCardCache.Count} " +
            $"shelf-vms={_browseShelfCache.Count} " +
            $"artwork={DecodedArtworkCache.ByteCount / (1024d * 1024d):F1}MiB";
    }

    private void StartDailyUpdatesRefresh()
    {
        if (_dailyRefreshTask is { IsCompleted: false })
        {
            // Navigating back to Home must not cancel providers that are still
            // returning their batches; doing so repeatedly prevented a scan
            // from ever reaching its import stage on a remote.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastDailyRefreshStartedAt is { } previous &&
            now - previous < ProviderRefreshInterval)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _dailyRefreshCancellation = cancellation;
        _lastDailyRefreshStartedAt = now;

        _dailyRefreshTask =
            RefreshDailyUpdatesInBackgroundAsync(cancellation);
    }

    private void EnsureHourlyProviderRefreshScheduler()
    {
        if (_providerRefreshSchedulerTask is { IsCompleted: false })
        {
            return;
        }

        _providerRefreshSchedulerCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _providerRefreshSchedulerCancellation = cancellation;
        _providerRefreshSchedulerTask =
            RunHourlyProviderRefreshSchedulerAsync(cancellation.Token);
    }

    private async Task RunHourlyProviderRefreshSchedulerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var activeRefresh = _dailyRefreshTask;
                if (activeRefresh is not null)
                {
                    await activeRefresh.WaitAsync(cancellationToken);
                }

                await Task.Delay(
                    ProviderRefreshInterval,
                    cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        StartDailyUpdatesRefresh();
                    }
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshDailyUpdatesInBackgroundAsync(
        CancellationTokenSource cancellation)
    {
        var createdCount = 0;
        var updatedCount = 0;
        var failureCount = 0;
        Task? producerTask = null;
        try
        {
            // Provider parsing and catalog imports can be CPU-heavy on every
            // client. Keep that work off the UI thread while retaining the
            // streaming behavior that lets non-TV clients render each provider
            // as soon as its serialized import finishes.
            var completedBatches =
                Channel.CreateBounded<ContentSourceDailyUpdateBatch>(
                    new BoundedChannelOptions(4)
                    {
                        AllowSynchronousContinuations = false,
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = true
                    });
            producerTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (var batch in contentSourceCatalogService
                                           .RefreshDailyUpdatesStreamingAsync(
                                               cancellation.Token)
                                           .ConfigureAwait(false))
                        {
                            await completedBatches.Writer.WriteAsync(
                                    batch,
                                    cancellation.Token)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        completedBatches.Writer.TryComplete();
                    }
                });

            await foreach (var batch in completedBatches.Reader
                               .ReadAllAsync(cancellation.Token))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Accumulate(batch);
                if (!IsTelevision)
                {
                    await RefreshTrustedArtworkOriginsAsync();
                    if ((batch.CreatedCount > 0 ||
                         batch.UpdatedCount > 0) &&
                        IsCatalogOpen)
                    {
                        await RefreshAsync();
                    }

                    UpdateStatus();
                }
            }

            await producerTask;
            if (IsTelevision)
            {
                // Rebuilding a large active TV grid can invalidate remote focus.
                // Persist all updates now and render them on the next page load.
                await RefreshTrustedArtworkOriginsAsync();
            }

            UpdateStatus();
            MemoryDiagnostics.Snapshot(
                "daily-refresh-complete",
                DescribeRetainedCatalog());
            if (failureCount > 0)
            {
                _lastDailyRefreshStartedAt = null;
            }
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _lastDailyRefreshStartedAt = null;
            ApplicationDiagnosticLog.Error(
                "providers.daily-refresh",
                "The daily provider refresh stopped unexpectedly.",
                exception);
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "DailyUpdatesFailed",
                exception.Message));
        }
        finally
        {
            cancellation.Cancel();
            if (producerTask is not null)
            {
                try
                {
                    await producerTask;
                }
                catch (OperationCanceledException)
                    when (cancellation.IsCancellationRequested)
                {
                }
                catch
                {
                    // The producer exception was already converted into the
                    // user-facing daily-refresh failure above.
                }
            }

            if (ReferenceEquals(_dailyRefreshCancellation, cancellation))
            {
                _dailyRefreshCancellation = null;
            }

            cancellation.Dispose();
        }

        void Accumulate(ContentSourceDailyUpdateBatch batch)
        {
            createdCount += batch.CreatedCount;
            updatedCount += batch.UpdatedCount;
            failureCount += batch.Failure is null ? 0 : 1;
        }

        async Task RefreshTrustedArtworkOriginsAsync()
        {
            var refreshedSettings =
                await contentSourceCatalogService.LoadSettingsAsync(
                    cancellation.Token);
            _artworkPolicies =
                contentSourceCatalogService.GetArtworkPolicies(
                    refreshedSettings);
            _trustedArtworkOrigins = _artworkPolicies
                .Select(static policy => policy.Origin)
                .ToArray();
        }

        void UpdateStatus()
        {
            if (failureCount > 0)
            {
                SetError(CatalogFeedback, UiTextCatalog.Format(
                    "DailyUpdatesPartial", createdCount, updatedCount, failureCount));
            }
        }
    }

    /// <summary>
    /// Puts one provider's answer on the page, if the reader is still looking
    /// at the title it belongs to.
    /// </summary>
    /// <remarks>
    /// Called once per answer as the providers reply, so this runs several
    /// times for one open. A provider refresh can outlast the reader's
    /// patience, and applying a late answer to whatever they moved on to
    /// would be worse than dropping it.
    /// </remarks>
    private void ShowRefreshedDetails(
        CatalogItemDetails opened,
        ContentSourceCatalogRefreshResult refreshed,
        CancellationTokenSource load)
    {
        if (!IsCurrentDetailsLoad(load))
        {
            return;
        }

        SelectedDetails = CreateDetailsViewModel(
            refreshed.Details,
            refreshed.ProviderName,
            refreshed.TrustedArtworkOrigins,
            artworkReferrer: refreshed.ArtworkReferrer,
            contentSourceProviderId: refreshed.ProviderId);

    }

    private async Task OpenDetailsAsync(Guid catalogItemId)
    {
        var load = BeginDetailsLoad();
        var cancellationToken = load.Token;
        try
        {
            IsBusy = true;
            SetError(CatalogFeedback, null);
            StatusMessage = null;
            var details = await OffUiThread.ReadAsync(() => catalogBrowseService.GetDetailsAsync(
                    catalogItemId,
                    cancellationToken)) ??
                throw new InvalidOperationException(
                    UiTextCatalog.Get("CatalogItemMissing"));
            if (!IsCurrentDetailsLoad(load))
            {
                return;
            }

            await ReloadCatalogUserStateAsync();
            if (!IsCurrentDetailsLoad(load))
            {
                return;
            }

            await RecordCatalogItemViewedAsync(details.Identity);
            if (!IsCurrentDetailsLoad(load))
            {
                return;
            }

            CloseReaders();
            IsSettingsOpen = false;
            IsDownloadsOpen = false;
            SelectedDetails = CreateDetailsViewModel(details);
            if (_hasEnabledApiContentProviders &&
                details.Item.MediaType is MediaType.Book or MediaType.Movie)
            {
                try
                {
                    // Every provider answers into this, so the page fills in
                    // from the first one back and grows as the slower ones
                    // arrive with more episodes. Progress posts to the thread
                    // that created it, which is this one.
                    var progress = new Progress<ContentSourceCatalogRefreshResult>(
                        interim => ShowRefreshedDetails(details, interim, load));
                    var refreshed = await OffUiThread.ReadAsync(() =>
                        contentSourceCatalogService.RefreshApiItemAsync(
                            details, progress, cancellationToken));

                    // The last answer is applied once more rather than trusted
                    // to have been the last one reported: the reporter is
                    // asynchronous, and the page must end on what was actually
                    // imported.
                    if (refreshed is not null && IsCurrentDetailsLoad(load))
                    {
                        await RefreshAsync();
                        if (!IsCurrentDetailsLoad(load))
                        {
                            return;
                        }

                        ShowRefreshedDetails(details, refreshed, load);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Superseded or navigated away. Recorded because from the
                    // outside it is indistinguishable from a title that
                    // simply never fills in.
                    ApplicationDiagnosticLog.Warning(
                        "catalog.details-refresh-cancelled",
                        details.Item.Name);
                }
                catch (Exception exception)
                {
                    // Opening the saved local copy must remain available when
                    // an online provider is temporarily unreachable — but
                    // swallowing this silently is what made a failing refresh
                    // look like a page that loads nothing.
                    ApplicationDiagnosticLog.Warning(
                        "catalog.details-refresh-failed",
                        $"{details.Item.Name}: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The reader navigated away; the newer navigation owns the screen.
        }
        catch (Exception exception) when (IsCurrentDetailsLoad(load))
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "DetailsLoadFailed",
                exception.Message));
        }
        catch
        {
            // A failure belonging to an abandoned load is not reported.
        }
        finally
        {
            EndDetailsLoad(load);
        }
    }

    private async Task ContinueProgressAsync(
        CatalogProgressSummary summary)
    {
        await OpenDetailsAsync(summary.CatalogItemId);
        var detailsViewModel = SelectedDetails;
        if (detailsViewModel is null)
        {
            return;
        }

        try
        {
            var details = detailsViewModel.Details;
            if (!summary.Content.IsUnit)
            {
                var source = details.Sources.FirstOrDefault(
                    static candidate =>
                        !CatalogMediaSourceViewModel.IsSubtitle(
                            candidate.AssetRole));
                if (source is null)
                {
                    throw new InvalidOperationException(
                        UiTextCatalog.Get("ProgressSourceMissing"));
                }

                await OpenMediaSourceAsync(
                    new CatalogMediaSourceSelection(
                        details.Identity,
                        details.Identity,
                        details.Item.Name,
                        details.Item.MediaType,
                        source,
                        details.Sources));
                return;
            }

            var unit = details.Units
                .Concat(details.Seasons.SelectMany(
                    static season => season.Units))
                .FirstOrDefault(candidate =>
                    candidate.Kind == summary.Content.UnitKind &&
                    candidate.Identity.SeasonNumber ==
                        summary.Content.SeasonNumber &&
                    ApiContentUnitRequest.DisplayNumbersMatch(
                        candidate.DisplayNumber,
                        summary.Content.DisplayNumber!));
            if (unit is null)
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get("ProgressTargetMissing"));
            }

            var title = BuildUnitTitle(unit);
            var localSource = unit.Sources.FirstOrDefault(
                static candidate =>
                    !CatalogMediaSourceViewModel.IsSubtitle(
                        candidate.AssetRole));
            if (localSource is not null)
            {
                await OpenMediaSourceAsync(
                    new CatalogMediaSourceSelection(
                        unit.Identity,
                        details.Identity,
                        title,
                        details.Item.MediaType,
                        localSource,
                        unit.Sources));
                return;
            }

            if (unit.Kind is ContentUnitKind.Chapter)
            {
                var imageSet =
                    detailsViewModel.ApiImageSets.FirstOrDefault(
                        candidate => candidate.Matches(
                            unit.Kind,
                            unit.DisplayNumber,
                            unit.Identity.SeasonNumber));
                if (imageSet is not null)
                {
                    await OpenApiImagesAsync(
                        new ApiContentImageSelection(
                            unit.Identity,
                            details.Identity,
                            title,
                            imageSet,
                            detailsViewModel.ContentSourceProviderId));
                    return;
                }

                await ResolveAndOpenApiImagesAsync(
                    new ApiContentUnitLookupSelection(
                        details,
                        unit,
                        title));
                return;
            }

            if (unit.Kind is ContentUnitKind.Episode)
            {
                var videoSet =
                    detailsViewModel.ApiVideoSets.FirstOrDefault(
                        candidate => candidate.Matches(
                            unit.Kind,
                            unit.DisplayNumber,
                            unit.Identity.SeasonNumber));
                if (videoSet is not null)
                {
                    await OpenApiVideosAsync(
                        new ApiContentVideoSelection(
                            unit.Identity,
                            details.Identity,
                            title,
                            videoSet,
                            detailsViewModel.ContentSourceProviderId));
                    return;
                }

                await ResolveAndOpenApiVideosAsync(
                    new ApiContentUnitLookupSelection(
                        details,
                        unit,
                        title));
                return;
            }

            throw new InvalidOperationException(
                UiTextCatalog.Get("ProgressSourceMissing"));
        }
        catch (Exception exception)
        {
            SetError(ContentFeedback, UiTextCatalog.Format(
                "ProgressOpenFailed",
                exception.Message));
        }
    }

    private async Task OpenSearchResultAsync(
        ContentSourceCatalogSearchResult searchResult)
    {
        if (!searchResult.IsOnline)
        {
            await OpenDetailsAsync(searchResult.Card.Id);
            return;
        }

        var load = BeginDetailsLoad();
        try
        {
            IsBusy = true;
            SetError(CatalogFeedback, null);
            StatusMessage = null;
            var loaded = await OffUiThread.ReadAsync(() => contentSourceCatalogService.LoadOnlineItemAsync(
                searchResult,
                load.Token));
            if (!IsCurrentDetailsLoad(load))
            {
                return;
            }

            await RefreshAsync();
            if (!IsCurrentDetailsLoad(load))
            {
                return;
            }

            CloseReaders();
            IsSettingsOpen = false;
            SelectedDetails = CreateDetailsViewModel(
                loaded.Details,
                loaded.ProviderName,
                loaded.TrustedArtworkOrigins,
                loaded.ImageSets,
                loaded.VideoSets,
                loaded.ArtworkReferrer,
                searchResult.Provider?.Id);

        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsCurrentDetailsLoad(load))
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "OnlineItemLoadFailed",
                exception.Message));
        }
        catch
        {
        }
        finally
        {
            EndDetailsLoad(load);
        }
    }

    private async Task OpenMediaSourceAsync(
        CatalogMediaSourceSelection selection)
    {
        await OpenMediaSourceCoreAsync(selection);
        SetPlayerEpisodeNavigation(
            selection.ContentIdentity,
            SelectedDetails?.Details);
        await MarkEnteredContentAsync(selection.ContentIdentity);
    }

    /// <summary>
    /// Only the content's own failure prevents marking it entered; an
    /// unrelated background operation may report an error at the same time.
    /// </summary>
    private Task MarkEnteredContentAsync(CatalogContentIdentity content) =>
        !EntryFeedback(content).HasMessage
            ? MarkContentEnteredAsync(content)
            : Task.CompletedTask;

    private async Task OpenMediaSourceCoreAsync(
        CatalogMediaSourceSelection selection)
    {
        var torrentActivityId = torrentActivityCoordinator.Begin(
            selection.Title,
            UiTextCatalog.Get("TorrentActivityPreparingContent"));
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(selection.ContentIdentity), null);
            StatusMessage = null;

            if (!mediaSessionService.IsAvailable)
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get("TorrentEngineMissing"));
            }

            if (selection.Source.Layout is CatalogMediaSourceLayout.ImageSequence)
            {
                await CloseActivePlaybackAsync();
                var sequence = await mediaSessionService.PrepareImageSequenceAsync(
                    selection.Source,
                    activityId: torrentActivityId);
                CloseReaders();
                Reader = await ImageReaderViewModel.CreateAsync(
                    selection.Title,
                    selection.ContentIdentity,
                    selection.CatalogItemIdentity,
                    sequence,
                    mediaSessionService,
                    UserMediaState);
                SetReaderChapterNavigation(
                    selection.ContentIdentity,
                    SelectedDetails?.Details);

                return;
            }

            if (CatalogMediaSourceViewModel.IsSingleBookImage(selection))
            {
                await CloseActivePlaybackAsync();
                var image = await mediaSessionService.PrepareSingleImageAsync(
                    selection.Source,
                    activityId: torrentActivityId);
                CloseReaders();
                Reader = await ImageReaderViewModel.CreateAsync(
                    selection.Title,
                    selection.ContentIdentity,
                    selection.CatalogItemIdentity,
                    image,
                    mediaSessionService,
                    UserMediaState);
                SetReaderChapterNavigation(
                    selection.ContentIdentity,
                    SelectedDetails?.Details);

                return;
            }

            if (IsCbz(selection))
            {
                await CloseActivePlaybackAsync();
                var archive = await mediaSessionService.PrepareImageArchiveAsync(
                    selection.Source,
                    activityId: torrentActivityId);
                try
                {
                    CloseReaders();
                    Reader = await ImageReaderViewModel.CreateAsync(
                        selection.Title,
                        selection.ContentIdentity,
                        selection.CatalogItemIdentity,
                        archive,
                        UserMediaState);
                    SetReaderChapterNavigation(
                        selection.ContentIdentity,
                        SelectedDetails?.Details);
                }
                catch
                {
                    archive.Dispose();
                    throw;
                }

                return;
            }

            if (IsEpub(selection))
            {
                await CloseActivePlaybackAsync();
                var book = await mediaSessionService.PrepareEpubBookAsync(
                    selection.Source,
                    activityId: torrentActivityId);
                try
                {
                    CloseReaders();
                    EpubReader = await EpubReaderViewModel.CreateAsync(
                        selection.Title,
                        selection.ContentIdentity,
                        book,
                        UserMediaState);
                }
                catch
                {
                    book.Dispose();
                    throw;
                }

                return;
            }

            if (CatalogMediaSourceViewModel.IsPdf(selection))
            {
                await CloseActivePlaybackAsync();
                if (!pdfRenderer.IsAvailable)
                {
                    throw new NotSupportedException(
                        UiTextCatalog.Get("PdfRendererUnavailable"));
                }

                var pdfFile = await mediaSessionService.PrepareSingleFileAsync(
                    selection.Source,
                    activityId: torrentActivityId);
                var pdfUri = await mediaSessionService.GetPlaybackUriAsync(pdfFile);
                var document = await pdfRenderer.OpenAsync(new PdfDocumentSource(
                    pdfUri,
                    pdfFile.Length,
                    cancellationToken => mediaSessionService.OpenReadAsync(
                        pdfFile,
                        cancellationToken)));
                try
                {
                    CloseReaders();
                    Reader = await ImageReaderViewModel.CreateAsync(
                        selection.Title,
                        selection.ContentIdentity,
                        selection.CatalogItemIdentity,
                        document,
                        UserMediaState);
                    SetReaderChapterNavigation(
                        selection.ContentIdentity,
                        SelectedDetails?.Details);
                }
                catch
                {
                    document.Dispose();
                    throw;
                }

                return;
            }

            if (CatalogMediaSourceViewModel.IsSubtitle(selection.Source.AssetRole))
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get("SelectPrimarySource"));
            }

            if (!IsAudioOrVideo(selection))
            {
                throw new NotSupportedException(
                    UiTextCatalog.Get("ReaderNotImplemented"));
            }

            if (!platformMediaPlayer.IsAvailable)
            {
                throw new NotSupportedException(
                    UiTextCatalog.Get("MediaPresenterUnavailable"));
            }

            var prepared = await mediaSessionService.PrepareSingleFileAsync(
                selection.Source,
                activityId: torrentActivityId);
            var playbackUri = await mediaSessionService.GetPlaybackUriAsync(prepared);
            var progress = await UserMediaState.LoadProgressAsync(selection.ContentIdentity);
            var startPosition = progress?.PositionKind is MediaPositionKind.Seconds
                ? progress.Position
                : 0;
            startPosition = await ResolveWatchPartyStartPositionAsync(
                selection.ContentIdentity,
                startPosition);
            var subtitles = platformMediaPlayer.SupportsExternalTracks
                ? await PrepareSubtitlesAsync(selection.RelatedSources)
                : [];
            var initialVariant = CreatePlatformMediaVariant(
                selection.MediaType,
                selection.Source,
                prepared,
                playbackUri);
            var alternateAudioVariants = platformMediaPlayer.SupportsExternalTracks
                ? await PrepareAlternateAudioVariantsAsync(selection)
                : [];

            await CloseActivePlaybackAsync();
            ActiveMediaTitle = selection.Title;
            await ShowEmbeddedMediaSurfaceAsync();
            await platformMediaPlayer.OpenAsync(new PlatformMediaRequest(
                initialVariant,
                selection.Title,
                startPosition,
                subtitles,
                alternateAudioVariants));
            StartPlaybackMonitor(selection.ContentIdentity);

        }
        catch (Exception exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);

            SetError(EntryFeedback(selection.ContentIdentity), UiTextCatalog.Format(
                "MediaOpenFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenApiImagesAsync(ApiContentImageSelection selection)
    {
        await OpenApiImagesCoreAsync(selection);
        await MarkEnteredContentAsync(selection.ContentIdentity);
    }

    private async Task OpenApiImagesCoreAsync(
        ApiContentImageSelection selection)
    {
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(selection.ContentIdentity), null);
            StatusMessage = null;
            await CloseActivePlaybackAsync();
            CloseReaders();
            Reader = await ImageReaderViewModel.CreateAsync(
                selection.Title,
                selection.ContentIdentity,
                selection.CatalogItemIdentity,
                selection.ImageSet,
                UserMediaState);
            SetReaderChapterNavigation(
                selection.ContentIdentity,
                SelectedDetails?.Details,
                selection.ProviderId);

        }
        catch (Exception exception)
        {
            SetError(EntryFeedback(selection.ContentIdentity), UiTextCatalog.Format(
                "OnlinePagesOpenFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ResolveAndOpenApiImagesAsync(
        ApiContentUnitLookupSelection selection) =>
        ResolveAndOpenApiImagesAsync(
            selection,
            preferredProviderId: null,
            automaticallyChooseProvider: false);

    private async Task ResolveAndOpenApiImagesAsync(
        ApiContentUnitLookupSelection selection,
        string? preferredProviderId,
        bool automaticallyChooseProvider)
    {
        var lookupCancellation = BeginApiUnitLookup();
        var cancellationToken = lookupCancellation.Token;
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(selection.Unit.Identity), null);
            StatusMessage = null;
            ClearApiProviderChoices();
            BeginApiProviderSearchIndicator(selection);
            var candidates =
                await contentSourceCatalogService
                    .FindApiImageCandidatesForUnitAsync(
                        selection.Details,
                        selection.Unit,
                        cancellationToken);
            if (!automaticallyChooseProvider && candidates.Count > 1)
            {
                ShowApiImageProviderChoices(
                    selection,
                    candidates);
                return;
            }

            if (automaticallyChooseProvider)
            {
                // The normal provider picker lives inside the chapter row,
                // behind the open reader. Adjacent navigation therefore has
                // to choose here: keep the reader's current provider when it
                // answered for this chapter, otherwise use discovery's
                // highest-ranked candidate.
                var preferredIndex =
                    ApiProviderPreference.FindPreferredProviderIndex(
                        candidates,
                        preferredProviderId,
                        static candidate => candidate.ProviderId);
                Exception? lastResolutionFailure = null;
                for (var attempt = 0; attempt < candidates.Count; attempt++)
                {
                    var candidateIndex =
                        ApiProviderPreference.GetAttemptIndex(
                            attempt,
                            candidates.Count,
                            preferredIndex);
                    try
                    {
                        var automaticResolution =
                            await contentSourceCatalogService
                                .ResolveApiImagesForUnitAsync(
                                    selection.Details,
                                    selection.Unit,
                                    candidates[candidateIndex],
                                    cancellationToken);
                        await ApplyApiImageResolutionAsync(
                            selection,
                            automaticResolution);
                        return;
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        lastResolutionFailure = exception;
                    }
                }

                throw lastResolutionFailure ??
                      new InvalidOperationException(
                          "No API provider could open the adjacent chapter.");
            }

            var resolution = await contentSourceCatalogService
                .ResolveApiImagesForUnitAsync(
                    selection.Details,
                    selection.Unit,
                    candidates[0],
                    cancellationToken);
            await ApplyApiImageResolutionAsync(
                selection,
                resolution);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(EntryFeedback(selection.Unit.Identity), UiTextCatalog.Format(
                "OnlineChapterResolveFailed",
                exception.Message));
        }
        finally
        {
            SelectedDetails?.EndProviderSearch();
            if (EndApiUnitLookup(lookupCancellation))
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Puts the pressed entry into its searching state so the row shows
    /// progress while providers are queried one after another.
    /// </summary>
    private void BeginApiProviderSearchIndicator(
        ApiContentUnitLookupSelection selection)
    {
        SelectedDetails?.BeginProviderSearch(
            selection.Unit.Identity,
            UiTextCatalog.Get("SearchingContentProviders"),
            CancelApiProviderChoice);
        _providerChoiceTargetStableKey = selection.Unit.Identity.StableKey;
        OnPropertyChanged(nameof(ProviderChoiceTargetStableKey));
    }

    private void ShowApiImageProviderChoices(
        ApiContentUnitLookupSelection selection,
        IReadOnlyList<ApiContentUnitImageCandidate> candidates)
    {
        ApiProviderChoices.Clear();
        foreach (var candidate in candidates)
        {
            ApiProviderChoices.Add(
                new ApiContentProviderOptionViewModel(
                    candidate.ProviderName,
                    candidate.TitleSimilarity,
                    () => SelectApiImageProviderAsync(
                        selection,
                        candidate)));
        }

        ShowApiProviderChoice(selection, candidates.Count);
    }

    private async Task SelectApiImageProviderAsync(
        ApiContentUnitLookupSelection selection,
        ApiContentUnitImageCandidate candidate)
    {
        var lookupCancellation = BeginApiUnitLookup();
        var cancellationToken = lookupCancellation.Token;
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(selection.Unit.Identity), null);
            StatusMessage = null;
            ClearApiProviderChoices();
            var resolution = await contentSourceCatalogService
                .ResolveApiImagesForUnitAsync(
                    selection.Details,
                    selection.Unit,
                    candidate,
                    cancellationToken);
            await ApplyApiImageResolutionAsync(
                selection,
                resolution);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(EntryFeedback(selection.Unit.Identity), UiTextCatalog.Format(
                "OnlineChapterResolveFailed",
                exception.Message));
        }
        finally
        {
            if (EndApiUnitLookup(lookupCancellation))
            {
                IsBusy = false;
            }
        }
    }

    private async Task ApplyApiImageResolutionAsync(
        ApiContentUnitLookupSelection selection,
        ApiContentUnitImageResolution resolution)
    {
        await RefreshAsync();
        await CloseActivePlaybackAsync();
        CloseReaders();
        SelectedDetails = CreateDetailsViewModel(
            resolution.Details,
            resolution.ProviderName,
            resolution.TrustedArtworkOrigins,
            [resolution.ImageSet],
            artworkReferrer: resolution.ArtworkReferrer,
            contentSourceProviderId: resolution.ProviderId);
        Reader = await ImageReaderViewModel.CreateAsync(
            selection.Title,
            resolution.Unit.Identity,
            resolution.Details.Identity,
            resolution.ImageSet,
            UserMediaState);
        SetReaderChapterNavigation(
            resolution.Unit.Identity,
            resolution.Details,
            resolution.ProviderId);

    }

    private async Task OpenApiVideosAsync(ApiContentVideoSelection selection)
    {
        if (!await OpenApiVideosCoreAsync(selection)) return;
        SetPlayerEpisodeNavigation(
            selection.ContentIdentity,
            SelectedDetails?.Details,
            selection.ProviderId);
        await MarkEnteredContentAsync(selection.ContentIdentity);
    }

    internal async Task<bool> OpenApiVideosCoreAsync(
        ApiContentVideoSelection selection)
    {
        var lookupCancellation = BeginApiUnitLookup();
        var cancellationToken = lookupCancellation.Token;
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(selection.ContentIdentity), null);
            StatusMessage = null;
            await OpenFirstPlayableApiVideoServerAsync(selection, cancellationToken);
            return true;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            await TryCloseFailedApiPlaybackAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return false;
            SetError(EntryFeedback(selection.ContentIdentity), UiTextCatalog.Format(
                "OnlineVideoOpenFailed",
                exception.Message));
            return false;
        }
        finally
        {
            if (EndApiUnitLookup(lookupCancellation)) IsBusy = false;
        }
    }

    private async Task ResolveAndOpenApiVideosAsync(
        ApiContentUnitLookupSelection selection)
    {
        var lookupCancellation = BeginApiUnitLookup();
        var cancellationToken = lookupCancellation.Token;
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(selection.Unit.Identity), null);
            StatusMessage = null;
            ClearApiProviderChoices();
            BeginApiProviderSearchIndicator(selection);
            ApplicationDiagnosticLog.Information(
                "api.provider-picker",
                "media=video; stage=search-started");
            var streamedCandidates =
                new List<ApiContentUnitVideoCandidate>();
            string? attemptedRememberedProviderId = null;
            Exception? rememberedFailure = null;
            IReadOnlyList<ApiContentUnitVideoCandidate> candidates =
                await contentSourceCatalogService
                    .FindApiVideoCandidatesForUnitAsync(
                        selection.Details,
                        selection.Unit,
                        async (candidate, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            var isFirstCandidate =
                                streamedCandidates.Count == 0;
                            streamedCandidates.Add(candidate);
                            ApplicationDiagnosticLog.Information(
                                "api.provider-picker",
                                $"media=video; stage=candidate; " +
                                $"provider={candidate.ProviderId}; " +
                                $"knownBinding={candidate.UsedKnownBinding}; " +
                                $"visible={streamedCandidates.Count}");
                            var shouldOpenRememberedCandidate =
                                isFirstCandidate &&
                                candidate.UsedKnownBinding &&
                                candidate.SearchItem.UnitRoutes.Count > 0;
                            if (shouldOpenRememberedCandidate)
                            {
                                attemptedRememberedProviderId =
                                    candidate.ProviderId;
                                try
                                {
                                    var resolution =
                                        await contentSourceCatalogService
                                            .ResolveApiVideosForUnitAsync(
                                                selection.Details,
                                                selection.Unit,
                                                candidate,
                                                token);
                                    // Resolved but deliberately not opened. The
                                    // saved provider used to start playing the
                                    // moment it resolved, which took the choice
                                    // away and committed the viewer to whichever
                                    // server answered first — often one that
                                    // then failed to play. Resolving it still
                                    // proves it works, which is what the
                                    // remembered-failure handling below reacts
                                    // to; the picker below offers it first.
                                    _ = resolution;
                                    token.ThrowIfCancellationRequested();
                                    ApplicationDiagnosticLog.Information(
                                        "api.provider-picker",
                                        $"media=video; " +
                                        $"stage=known-binding-validated; " +
                                        $"provider={candidate.ProviderId}");

                                    // A choice can be made now; the remaining
                                    // provider tasks keep running.
                                    IsBusy = false;
                                }
                                catch (OperationCanceledException)
                                    when (token.IsCancellationRequested)
                                {
                                    throw;
                                }
                                catch (Exception exception)
                                {
                                    rememberedFailure = exception;
                                    streamedCandidates.Remove(candidate);
                                    ApplicationDiagnosticLog.Warning(
                                        "api.provider-picker",
                                        $"media=video; " +
                                        $"stage=known-binding-rejected; " +
                                        $"provider={candidate.ProviderId}; " +
                                        $"failure={exception.GetType().Name}");
                                    await TryCloseFailedApiPlaybackAsync(token);
                                    token.ThrowIfCancellationRequested();
                                }
                            }

                            if (streamedCandidates.Count > 0)
                            {
                                ShowApiVideoProviderChoices(
                                    selection,
                                    streamedCandidates,
                                    searchIsComplete: false);
                                // A result can be selected now while the
                                // remaining provider tasks keep running.
                                IsBusy = false;
                            }
                        },
                        cancellationToken);
            ApplicationDiagnosticLog.Information(
                "api.provider-picker",
                $"media=video; stage=search-completed; " +
                $"candidates={candidates.Count}; " +
                $"knownBindingFailed={rememberedFailure is not null}");
            if (rememberedFailure is not null)
            {
                candidates = candidates
                    .Where(candidate => !string.Equals(
                        candidate.ProviderId,
                        attemptedRememberedProviderId,
                        StringComparison.Ordinal))
                    .ToArray();
            }

            if (candidates.Count == 0 && rememberedFailure is not null)
            {
                throw rememberedFailure;
            }

            if (ApiProviderPreference.ShouldShowManualPicker(
                    candidates.Count))
            {
                ShowApiVideoProviderChoices(
                    selection,
                    candidates);
                return;
            }

            throw new InvalidOperationException(
                "No enabled API provider found the selected episode.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "api.provider-picker",
                $"media=video; stage=search-failed; " +
                $"failure={exception.GetType().Name}; " +
                $"message={exception.Message}");
            await TryCloseFailedApiPlaybackAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            SetError(EntryFeedback(selection.Unit.Identity), UiTextCatalog.Format(
                "OnlineEpisodeResolveFailed",
                exception.Message));
        }
        finally
        {
            if (EndApiUnitLookup(lookupCancellation))
            {
                SelectedDetails?.EndProviderSearch();
                IsBusy = false;
            }
        }
    }

    private void ShowApiVideoProviderChoices(
        ApiContentUnitLookupSelection selection,
        IReadOnlyList<ApiContentUnitVideoCandidate> candidates,
        bool searchIsComplete = true)
    {
        ApiProviderChoices.Clear();
        foreach (var candidate in candidates)
        {
            ApiProviderChoices.Add(
                new ApiContentProviderOptionViewModel(
                    candidate.ProviderName,
                    candidate.TitleSimilarity,
                    () => SelectApiVideoProviderAsync(
                        selection,
                        candidate)));
        }

        ShowApiProviderChoice(
            selection,
            candidates.Count,
            searchIsComplete);
        ApplicationDiagnosticLog.Information(
            "api.provider-picker",
            $"media=video; stage=shown; candidates={candidates.Count}; " +
            $"searchComplete={searchIsComplete}");
    }

    internal async Task SelectApiVideoProviderAsync(
        ApiContentUnitLookupSelection selection,
        ApiContentUnitVideoCandidate candidate)
    {
        var lookupCancellation = BeginApiUnitLookup();
        var cancellationToken = lookupCancellation.Token;
        try
        {
            IsBusy = true;
            SetError(EntryFeedback(selection.Unit.Identity), null);
            StatusMessage = null;
            ApplicationDiagnosticLog.Information(
                "api.provider-picker",
                $"media=video; stage=selected; " +
                $"provider={candidate.ProviderId}");
            // Keep other choices available while this provider resolves.
            var resolution = await contentSourceCatalogService
                .ResolveApiVideosForUnitAsync(
                    selection.Details,
                    selection.Unit,
                    candidate,
                    cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyApiVideoResolutionAsync(
                selection,
                resolution,
                cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "api.provider-picker",
                $"media=video; stage=selection-failed; " +
                $"provider={candidate.ProviderId}; " +
                $"failure={exception.GetType().Name}; " +
                $"message={exception.Message}");
            await TryCloseFailedApiPlaybackAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            SetError(EntryFeedback(selection.Unit.Identity), UiTextCatalog.Format(
                "OnlineEpisodeResolveFailed",
                exception.Message));
        }
        finally
        {
            if (EndApiUnitLookup(lookupCancellation))
            {
                IsBusy = false;
            }
        }
    }

    private async Task ApplyApiVideoResolutionAsync(
        ApiContentUnitLookupSelection selection,
        ApiContentUnitVideoResolution resolution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync();
        cancellationToken.ThrowIfCancellationRequested();
        SelectedDetails = CreateDetailsViewModel(
            resolution.Details,
            resolution.ProviderName,
            resolution.TrustedArtworkOrigins,
            apiImageSets: null,
            apiVideoSets: [resolution.VideoSet],
            artworkReferrer: resolution.ArtworkReferrer,
            contentSourceProviderId: resolution.ProviderId);
        var videoSelection = new ApiContentVideoSelection(
            resolution.Unit.Identity,
            resolution.Details.Identity,
            selection.Title,
            resolution.VideoSet);
        _ = await OpenFirstPlayableApiVideoServerAsync(
            videoSelection, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // This path opens the player directly rather than through
        // OpenApiVideosAsync, so it has to arm the episode stepper itself.
        // Carrying the provider forward keeps a series on one source instead
        // of re-deciding at every episode.
        SetPlayerEpisodeNavigation(
            resolution.Unit.Identity,
            resolution.Details,
            resolution.ProviderId);

    }

    /// <summary>
    /// The picker is rendered by the entry itself, so the user chooses a source
    /// without leaving the chapter row they pressed.
    /// </summary>
    private void ShowApiProviderChoice(
        ApiContentUnitLookupSelection selection,
        int providerCount,
        bool searchIsComplete = true)
    {
        SelectedDetails?.ShowProviderChoices(
            selection.Unit.Identity,
            UiTextCatalog.Format(
                searchIsComplete
                    ? "MultipleContentProvidersFound"
                    : "ContentProvidersFoundWhileSearching",
                providerCount),
            ApiProviderChoices.ToArray(),
            CancelApiProviderChoice);
        _providerChoiceTargetStableKey = selection.Unit.Identity.StableKey;
        // A streaming lookup can publish more matches for the same entry.
        // Always raise the signal so the picker remains visible as it grows.
        OnPropertyChanged(nameof(ProviderChoiceTargetStableKey));
    }

    [RelayCommand]
    private void CancelApiProviderChoice()
    {
        CancelApiUnitLookup();
        ClearApiProviderChoices();
    }

    private void ClearApiProviderChoices()
    {
        ApiProviderChoices.Clear();
        SelectedDetails?.ClearProviderChoices();
        if (_providerChoiceTargetStableKey is not null)
        {
            _providerChoiceTargetStableKey = null;
            OnPropertyChanged(nameof(ProviderChoiceTargetStableKey));
        }
    }

    private CancellationTokenSource BeginApiUnitLookup()
    {
        CancelApiUnitLookup();
        var cancellation = new CancellationTokenSource();
        _apiUnitLookupCancellation = cancellation;
        return cancellation;
    }

    private bool EndApiUnitLookup(CancellationTokenSource cancellation)
    {
        var current = _apiUnitLookupCancellation;
        if (ReferenceEquals(current, cancellation))
        {
            _apiUnitLookupCancellation = null;
        }

        cancellation.Dispose();
        return ReferenceEquals(current, cancellation);
    }

    private void CancelApiUnitLookup()
    {
        var cancellation = Interlocked.Exchange(
            ref _apiUnitLookupCancellation,
            null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        // The operation owns disposal when its continuation finishes.
        IsBusy = false;
    }

    private async Task SelectApiVideoServerAsync(
        ApiContentVideoSource source)
    {
        var selection = _activeApiVideoSelection;
        if (selection is null ||
            ActiveVideoServers.Any(option =>
                option.IsSelected &&
                IsSameApiVideoSource(option.Source, source)))
        {
            return;
        }

        var lookupCancellation = BeginApiUnitLookup();
        var cancellationToken = lookupCancellation.Token;
        try
        {
            IsBusy = true;
            SetError(PlaybackFeedback, null);
            // Selection follows the requested server immediately, so the
            // previous server remains clickable while extraction is pending.
            foreach (var option in ActiveVideoServers)
                option.SetSelected(IsSameApiVideoSource(option.Source, source));
            await OpenApiVideoServerAsync(
                selection,
                source,
                preserveCurrentPosition: true,
                cancellationToken);

        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (ActiveVideoServers.Any(option => option.IsSelected &&
                    IsSameApiVideoSource(option.Source, source)))
            {
                await TryCloseFailedApiPlaybackAsync(cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested) return;
            SetError(PlaybackFeedback, UiTextCatalog.Format(
                "OnlineVideoOpenFailed",
                exception.Message));
        }
        finally
        {
            if (EndApiUnitLookup(lookupCancellation)) IsBusy = false;
        }
    }

    private async Task<ApiContentVideoSource>
        OpenFirstPlayableApiVideoServerAsync(
            ApiContentVideoSelection selection,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _activeApiVideoSelection = selection;
        ActiveMediaTitle = selection.Title;
        PlaybackFeedback.Message = null;
        if (selection.VideoSet.Sources.Count > 0)
        {
            RebuildActiveVideoServers(selection.VideoSet, selection.VideoSet.Sources[0]);
            IsEmbeddedMediaOpen = true;
        }
        Exception? lastError = null;
        // Every server that fails is recorded, not just the last one. Keeping
        // only lastError meant a title with several servers reported one
        // generic "nothing could be opened" and threw away the reason each
        // individual server refused, which is the only thing that separates a
        // dead hoster from a stream the player rejected.
        var failures = new List<string>();
        var sources = selection.VideoSet.Sources;
        if (sources.Count == 0)
        {
            // Distinct from every server failing, and it looks identical from
            // the message alone.
            ApplicationDiagnosticLog.Error(
                "playback.no-servers-offered",
                "The selected unit resolved with no video servers at all, so " +
                "nothing was attempted.");
            throw new InvalidOperationException(
                UiTextCatalog.Get("NoPlayableOnlineVideoServer"));
        }

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await OpenApiVideoServerAsync(
                    selection,
                    source,
                    preserveCurrentPosition: false,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (failures.Count > 0)
                {
                    ApplicationDiagnosticLog.Information(
                        "playback.server-opened",
                        $"Opened '{source.Label}' after {failures.Count} " +
                        $"failed server(s): {string.Join(" | ", failures)}");
                }

                return source;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                failures.Add(DescribeServerFailure(source, exception));
                try
                {
                    await CloseActivePlaybackAsync(
                        clearApiVideoServers: false, cancellationToken);
                }
                catch (Exception cleanupException)
                {
                    // Continue with another server even when cleanup of the
                    // failed native source reports its own error, but say so:
                    // a failed teardown can be why the next server fails too.
                    failures.Add(
                        $"cleanup after '{source.Label}' -> " +
                        $"{cleanupException.GetType().Name}");
                }
            }
        }

        ApplicationDiagnosticLog.Error(
            "playback.no-server-opened",
            $"None of {sources.Count} video server(s) could be opened: " +
            string.Join(" | ", failures));
        throw new InvalidOperationException(
            UiTextCatalog.Get("NoPlayableOnlineVideoServer"),
            lastError);
    }

    /// <summary>
    /// One line per failed server: which one, how it was going to be played,
    /// and both ends of the exception chain.
    /// </summary>
    /// <remarks>
    /// Reporting only the innermost exception threw away the meaning. A player
    /// that never starts surfaces as an outer "the selected video server did
    /// not start playback in time" wrapping a bare
    /// <see cref="TimeoutException"/> whose message is "The operation has timed
    /// out" — and the log kept the useless half, which read as a network
    /// timeout and sent the search to the wrong layer entirely. The outer
    /// message says what failed, the inner one how, so both are kept.
    /// </remarks>
    private static string DescribeServerFailure(
        ApiContentVideoSource source,
        Exception exception)
    {
        var root = exception;
        while (root.InnerException is { } inner)
        {
            root = inner;
        }

        var kind = source.IsEmbeddedWeb ? "embedded-web" : "native";
        var description = $"'{source.Label}' ({kind}) -> " +
            $"{exception.GetType().Name}: {Summarize(exception.Message)}";
        return ReferenceEquals(root, exception)
            ? description
            : $"{description} (caused by {root.GetType().Name}: " +
                $"{Summarize(root.Message)})";

        static string Summarize(string message)
        {
            var text = message.ReplaceLineEndings(" ").Trim();
            return text.Length > 160 ? text[..160] : text;
        }
    }

    private async Task OpenApiVideoServerAsync(
        ApiContentVideoSelection selection,
        ApiContentVideoSource source,
        bool preserveCurrentPosition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var playbackSource = videoHosterResolver is null
            ? source
            : await videoHosterResolver.RefreshAsync(source, cancellationToken).AsTask().WaitAsync(cancellationToken) ??
              throw new InvalidOperationException(
                  UiTextCatalog.Get("NoPlayableOnlineVideoServer"));
        cancellationToken.ThrowIfCancellationRequested();

        if (source.IsEmbeddedWeb && !platformMediaPlayer.IsAvailable)
            playbackSource = source;

        if (!playbackSource.IsEmbeddedWeb &&
            !platformMediaPlayer.IsAvailable)
        {
            throw new NotSupportedException(
                UiTextCatalog.Get("MediaPresenterUnavailable"));
        }

        if (playbackSource.IsEmbeddedWeb &&
            !platformMediaPlayer.SupportsEmbeddedWeb)
        {
            throw new NotSupportedException(
                UiTextCatalog.Get("EmbeddedWebPlayerNotConfigured"));
        }

        var replacingCurrentServer =
            preserveCurrentPosition &&
            _activeApiVideoSelection is not null &&
            string.Equals(
                _activeApiVideoSelection.ContentIdentity.StableKey,
                selection.ContentIdentity.StableKey,
                StringComparison.Ordinal) &&
            IsEmbeddedMediaOpen;
        double startPosition;
        if (replacingCurrentServer &&
            _activePlaybackIdentity is not null)
        {
            var currentState = await platformMediaPlayer.GetStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            startPosition = Math.Max(0, currentState.PositionSeconds);
            try
            {
                await SavePlaybackProgressAsync(
                    selection.ContentIdentity,
                    currentState);
            }
            catch
            {
                // A server switch must continue when its progress snapshot fails.
            }

            cancellationToken.ThrowIfCancellationRequested();
            await StopPlaybackMonitorAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _activePlaybackIdentity = null;
        }
        else
        {
            var progress = await UserMediaState.LoadProgressAsync(
                selection.ContentIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            startPosition =
                progress?.PositionKind is MediaPositionKind.Seconds &&
                !progress.IsCompleted
                    ? progress.Position
                    : 0;
            startPosition = await ResolveWatchPartyStartPositionAsync(
                selection.ContentIdentity,
                startPosition);
            cancellationToken.ThrowIfCancellationRequested();
            if (replacingCurrentServer)
            {
                await StopPlaybackMonitorAsync();
                cancellationToken.ThrowIfCancellationRequested();
                await platformMediaPlayer.CloseAsync(cancellationToken);
            }
            else
            {
                await CloseActivePlaybackAsync(
                    clearApiVideoServers: false, cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        CloseReaders();
        _activeApiVideoSelection = selection;
        RebuildActiveVideoServers(selection.VideoSet, source);
        ActiveMediaTitle = selection.Title;
        if (playbackSource.IsEmbeddedWeb)
        {
            // Decided before the page opens, because the script that starts
            // the page's own player is injected as soon as it loads.
            await UpdateWatchPartyPausedStartAsync(selection.ContentIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            await platformMediaPlayer.OpenEmbeddedWebAsync(
                playbackSource.PlaybackUri,
                selection.Title,
                playbackSource.Referrer,
                cancellationToken);
            await ShowEmbeddedMediaSurfaceAsync(cancellationToken);
        }
        else
        {
            var initialVariant = ApiVideoServerVariantPlanner.CreateVariant(
                playbackSource);
            var alternateServerVariants =
                platformMediaPlayer.SupportsInPlayerServerSelection
                    ? ApiVideoServerVariantPlanner.CreateAlternates(
                        selection.VideoSet,
                        source,
                        playbackSource)
                    : [];
            await ShowEmbeddedMediaSurfaceAsync(cancellationToken);
            try
            {
                await platformMediaPlayer.OpenAsync(new PlatformMediaRequest(
                    initialVariant,
                    selection.Title,
                    startPosition,
                    Subtitles: ApiVideoServerVariantPlanner.CreateSubtitles(playbackSource),
                    AlternateAudioVariants: alternateServerVariants), cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested && source.IsEmbeddedWeb && platformMediaPlayer.SupportsEmbeddedWeb)
            {
                // A captured feed is still an optimization. If the native
                // player rejects it, preserve the selected server in WebView.
                await platformMediaPlayer.CloseAsync(cancellationToken);
                playbackSource = source;
                await UpdateWatchPartyPausedStartAsync(selection.ContentIdentity);
                cancellationToken.ThrowIfCancellationRequested();
                await platformMediaPlayer.OpenEmbeddedWebAsync(source.PlaybackUri, selection.Title, source.Referrer, cancellationToken);
                await ShowEmbeddedMediaSurfaceAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!playbackSource.IsEmbeddedWeb)
        {
            StartPlaybackMonitor(selection.ContentIdentity);
        }
    }

    private void RebuildActiveVideoServers(
        ApiContentVideoSet videoSet,
        ApiContentVideoSource selectedSource)
    {
        ActiveVideoServers.Clear();
        foreach (var source in videoSet.Sources)
        {
            var option = new ApiVideoServerOptionViewModel(
                source,
                SelectApiVideoServerAsync);
            option.SetSelected(
                IsSameApiVideoSource(source, selectedSource));
            ActiveVideoServers.Add(option);
        }

        OnPropertyChanged(nameof(HasActiveVideoServers));
    }

    private static bool IsSameApiVideoSource(
        ApiContentVideoSource left,
        ApiContentVideoSource right) =>
        left.PlaybackUri == right.PlaybackUri &&
        left.Kind == right.Kind &&
        string.Equals(
            left.Label,
            right.Label,
            StringComparison.OrdinalIgnoreCase);

    private async Task TryCloseFailedApiPlaybackAsync(CancellationToken cancellationToken)
    {
        if (_activeApiVideoSelection is null)
        {
            return;
        }

        try
        {
            await CloseActivePlaybackAsync(clearApiVideoServers: false, cancellationToken);
        }
        catch
        {
            // Keep the original failure and the choices even if native cleanup fails.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsEmbeddedMediaOpen = true;
                ActiveMediaTitle = _activeApiVideoSelection?.Title;
                foreach (var option in ActiveVideoServers)
                {
                    option.SetSelected(false);
                }
            }
        }
    }

    private async Task InitializeMagnetIntakeAsync()
    {
        try
        {
            var preferences = await magnetOpenService.LoadPreferencesAsync();
            OpenMagnetsAutomatically = preferences.OpenAutomatically;
            ImportTorrentInfoJson = preferences.ImportInfoJson;
            TreatExternalMagnetsAsPrivate = preferences.TreatExternalMagnetsAsPrivate;

            foreach (var magnetLink in initialMagnetLinks)
            {
                await HandleMagnetAsync(magnetLink, userConfirmed: false);
            }
        }
        catch (Exception exception)
        {
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "MagnetOptionsLoadFailed",
                exception.Message));
        }
    }

    private async Task InitializeMetadataSettingsAsync()
    {
        try
        {
            ApplyMetadataSettings(await metadataProviderSettingsStore.LoadAsync());
        }
        catch (Exception exception)
        {
            SetError(MetadataFeedback, UiTextCatalog.Format(
                "MetadataOptionsLoadFailed",
                exception.Message));
        }
    }

    private async Task InitializeContentSourceSettingsAsync()
    {
        try
        {
            ApplyContentSourceSettings(
                await contentSourceCatalogService.LoadSettingsAsync());
        }
        catch (Exception exception)
        {
            SetError(SourcesFeedback, UiTextCatalog.Format(
                "ContentSourceLoadFailed",
                exception.Message));
        }
    }

    private async Task InitializeRemoteServerSettingsAsync()
    {
        if (!SupportsRemoteSync)
        {
            return;
        }

        try
        {
            ApplyRemoteServerSettings(
                await remoteServerSettingsStore.LoadAsync());
        }
        catch (Exception exception)
        {
            SetError(AccountFeedback, UiTextCatalog.Format(
                "RemoteServerSettingsLoadFailed",
                exception.Message));
        }

        await RestoreRemoteSessionAsync();
    }

    /// <summary>
    /// Signs back in with the token kept from the previous run. A session the
    /// server no longer honours is forgotten quietly: the user simply sees the
    /// signed-out form, which is what an expired login should look like.
    /// </summary>
    private async Task RestoreRemoteSessionAsync()
    {
        if (remoteSessionStore is null || IsRemoteSignedIn)
        {
            return;
        }

        RemoteSessionState? stored;
        try
        {
            stored = await remoteSessionStore.LoadAsync();
        }
        catch (Exception exception)
        {
            ReportRemoteSyncFailure("RemoteSessionRestoreFailed", exception);
            return;
        }

        if (stored is null)
        {
            return;
        }

        if (!stored.IsUsable(DateTimeOffset.UtcNow))
        {
            await SaveRemoteSessionAsync(null);
            return;
        }

        try
        {
            var session = await remoteMediaServerClient.RestoreSessionAsync(
                new Uri(stored.ServiceUrl),
                stored.AccessToken);
            if (session is null)
            {
                await SaveRemoteSessionAsync(null);
                return;
            }

            RemoteUsername = session.Username;
            RemoteAccountDisplayName = session.DisplayName;
            RemoteDisplayName = session.DisplayName;
            IsRemoteSignedIn = true;
            await SaveRemoteSessionAsync(session);

            // A session restored at startup never passed through the sign-in
            // or settings-save paths, which were the only places capabilities
            // were read. Without this the application knows it is signed in but
            // not what the server can do, and every optional surface stays
            // hidden until the settings page happens to be saved.
            await RefreshRemoteCapabilitiesAsync(session.ServiceBaseUri);
            await RefreshRemoteSnapshotStateAsync();
        }
        catch (Exception exception)
        {
            // The server being unreachable is not a rejected session, so the
            // token is kept for the next attempt instead of being discarded.
            ClearRemoteSession();
            if (RemoteSyncFailurePolicy.Classify(exception) is
                RemoteSyncFailureKind.SessionRejected)
            {
                await SaveRemoteSessionAsync(null);
            }

            ReportRemoteSyncFailure("RemoteSessionRestoreFailed", exception);
        }
    }

    private async Task ForgetStoredRemoteSessionAsync()
    {
        if (remoteSessionStore is null)
        {
            return;
        }

        try
        {
            await remoteSessionStore.SaveAsync(null);
        }
        catch
        {
            // An unusable stored session is discarded on the next start, so a
            // failure here is not worth interrupting the user for.
        }
    }

    private async Task SaveRemoteSessionAsync(
        RemoteAccountSessionInfo? session)
    {
        if (remoteSessionStore is null)
        {
            return;
        }

        try
        {
            if (session is null ||
                remoteMediaServerClient.AccessToken is not { Length: > 0 }
                    accessToken)
            {
                await remoteSessionStore.SaveAsync(null);
                return;
            }

            await remoteSessionStore.SaveAsync(new RemoteSessionState
            {
                ServiceUrl = session.ServiceBaseUri.AbsoluteUri,
                AccessToken = accessToken,
                AccountId = session.AccountId,
                Username = session.Username,
                DisplayName = session.DisplayName,
                ExpiresAt = session.ExpiresAt
            });
        }
        catch (Exception exception)
        {
            // Losing the stored session only costs one sign-in, so it must not
            // break the sign-in that just succeeded.
            ReportRemoteSyncFailure("RemoteSessionSaveFailed", exception);
        }
    }

    private async Task AuthenticateRemoteAccountAsync(bool register)
    {
        if (!SupportsRemoteSync || IsRemoteSyncBusy)
        {
            return;
        }

        try
        {
            IsRemoteSyncBusy = true;
            SetError(AccountFeedback, null);
            StatusMessage = null;
            var settings = BuildRemoteServerSettings();
            if (settings.ServiceUrl.Length == 0)
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get("RemoteServerUrlRequired"));
            }

            var serviceUri = new Uri(settings.ServiceUrl);
            var capabilities =
                await remoteMediaServerClient.GetCapabilitiesAsync(
                    serviceUri);
            EnsureRemoteCapabilities(
                capabilities,
                settings.OnlineCatalogEnabled);
            if (register && !capabilities.AccountRegistration)
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get(
                        "RemoteRegistrationDisabled"));
            }

            await remoteServerSettingsStore.SaveAsync(settings);
            _remoteServerSettings = settings;
            await ConfigureRemoteOnlineCatalogAsync(settings);
            if (remoteMediaServerClient.Session is not null)
            {
                // Including its watch party, or the incoming account would
                // inherit a socket speaking as the outgoing one.
                await _watchPartyService.ResetAsync();
                await remoteMediaServerClient.SignOutAsync();
            }

            var session = register
                ? await remoteMediaServerClient.RegisterAsync(
                    serviceUri,
                    settings.Username,
                    RemotePassword,
                    RemoteDisplayName,
                    settings.DeviceName)
                : await remoteMediaServerClient.LoginAsync(
                    serviceUri,
                    settings.Username,
                    RemotePassword,
                    settings.DeviceName);
            RemoteUsername = session.Username;
            RemoteAccountDisplayName = session.DisplayName;
            RemoteDisplayName = session.DisplayName;
            RemotePassword = string.Empty;
            IsRemoteSignedIn = true;
            _remoteServerSettings = settings with
            {
                Username = session.Username
            };
            await remoteServerSettingsStore.SaveAsync(
                _remoteServerSettings);
            await SaveRemoteSessionAsync(session);
            StatusMessage = UiTextCatalog.Format(
                "RemoteSignInSucceeded",
                session.DisplayName);

            // The session is already valid at this point. A failure while
            // reconciling device state is a sync problem, not a rejected
            // sign-in, so the account stays signed in and the real reason is
            // reported instead of "sign-in failed".
            try
            {
                if (userSyncStore is null)
                {
                    await RefreshRemoteSnapshotStateAsync();
                }
                else
                {
                    await UploadRemoteSnapshotCoreAsync();
                }
            }
            catch (Exception syncException)
            {
                ReportRemoteSyncFailure(
                    "RemoteInitialSyncFailed",
                    syncException);
            }
        }
        catch (Exception exception)
        {
            ClearRemoteSession();
            await ForgetStoredRemoteSessionAsync();
            SetError(AccountFeedback, UiTextCatalog.Format(
                register
                    ? "RemoteRegistrationFailed"
                    : "RemoteSignInFailed",
                exception.Message));
        }
        finally
        {
            IsRemoteSyncBusy = false;
        }
    }

    private RemoteServerSettings BuildRemoteServerSettings()
    {
        var settings = new RemoteServerSettings
        {
            ServiceUrl = RemoteServiceUrl,
            Username = RemoteUsername,
            DeviceId = _remoteServerSettings.DeviceId,
            DeviceName = RemoteDeviceName,
            OnlineCatalogEnabled = RemoteOnlineCatalogEnabled
        }.Normalize();
        applicationUpdateService.RememberServiceUrl(settings.ServiceUrl);
        return settings;
    }

    private void ApplyRemoteServerSettings(
        RemoteServerSettings settings)
    {
        var normalized = settings.Normalize();
        _remoteServerSettings = normalized;
        RemoteServiceUrl = normalized.ServiceUrl;
        applicationUpdateService.RememberServiceUrl(normalized.ServiceUrl);
        RemoteUsername = normalized.Username;
        RemoteDeviceName = normalized.DeviceName;
        RemoteOnlineCatalogEnabled =
            normalized.OnlineCatalogEnabled;
        RemoteDisplayName = normalized.Username;
        ClearRemoteSession();
    }

    private async Task ConfigureRemoteOnlineCatalogAsync(
        RemoteServerSettings settings)
    {
        var current = await contentSourceCatalogService.LoadSettingsAsync();
        var catalogBaseUrl = settings.ServiceUrl.Length == 0
            ? string.Empty
            : new Uri(
                new Uri(settings.ServiceUrl),
                "api/v1/catalog/")
                .AbsoluteUri;
        var providers = current.Providers
            .Select(provider =>
                string.Equals(
                    provider.Id,
                    "umd-json-api",
                    StringComparison.Ordinal)
                    ? provider with
                    {
                        DisplayName = UiTextCatalog.Get(
                            "RemoteOnlineCatalogProvider"),
                        IsEnabled =
                            settings.OnlineCatalogEnabled,
                        BaseUrl = catalogBaseUrl
                    }
                    : provider)
            .ToArray();
        ApplyContentSourceSettings(
            await contentSourceCatalogService.SaveSettingsAsync(
                new ContentSourceProviderSettings
                {
                    Providers = providers
                }));
    }

    /// <summary>
    /// Reads what the signed-in server supports, without treating an
    /// unreachable one as a problem: this runs on the startup path, where the
    /// only cost of failing is that optional surfaces stay hidden.
    /// </summary>
    private async Task RefreshRemoteCapabilitiesAsync(Uri serviceBaseUri)
    {
        try
        {
            var capabilities =
                await remoteMediaServerClient.GetCapabilitiesAsync(
                    serviceBaseUri);
            _friendService.ApplyCapabilities(capabilities);
            _watchPartyService.ApplyCapabilities(capabilities);
            lock (_progressSyncGate)
            {
                _supportsProgressPatch = capabilities.ProgressPatch;
                _supportsDeltaSync = capabilities.DeltaSync;
            }

            OnPropertyChanged(nameof(SupportsFriends));
            RefreshWatchPartyState();

            // A party this account was in when the app last closed may still
            // be running; rejoining it is what a member expects.
            if (await _watchPartyService.TryResumeAsync())
            {
                RefreshWatchPartyState();
            }
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "remote.capabilities-unavailable",
                exception.Message);
        }
    }

    private void EnsureRemoteCapabilities(
        RemoteServerCapabilities capabilities,
        bool requireOnlineCatalog)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // Recorded before the compatibility test, so a server this build
        // refuses still leaves the friend surface switched off rather than
        // holding whatever the last server reported.
        _friendService.ApplyCapabilities(capabilities);
        _watchPartyService.ApplyCapabilities(capabilities);
        lock (_progressSyncGate)
        {
            _supportsProgressPatch = capabilities.ProgressPatch;
            _supportsDeltaSync = capabilities.DeltaSync;
        }

        OnPropertyChanged(nameof(SupportsFriends));
        RefreshWatchPartyState();
        if (capabilities.ApiVersion != 1 ||
            !capabilities.Accounts ||
            !capabilities.BackupSync ||
            capabilities.SyncProtocolVersion <
                UserSyncDocument.CurrentVersion ||
            requireOnlineCatalog &&
            !capabilities.OnlineCatalog)
        {
            throw new InvalidOperationException(
                UiTextCatalog.Get("RemoteServerIncompatible"));
        }
    }

    private async ValueTask RefreshRemoteSnapshotStateAsync()
    {
        var snapshot =
            await remoteMediaServerClient.GetSnapshotAsync();
        if (snapshot is null)
        {
            ApplyRemoteSnapshotState(
                revision: 0,
                updatedAt: null,
                hasSnapshot: false);
            return;
        }

        ApplyRemoteSnapshotState(
            snapshot.Revision,
            snapshot.UpdatedAt,
            hasSnapshot: true);
    }

    private async ValueTask<RemoteSyncWriteResult>
        UploadRemoteSnapshotCoreAsync()
    {
        if (userSyncStore is null)
        {
            throw new InvalidOperationException(
                UiTextCatalog.Get("RemoteSyncUnavailable"));
        }

        var reconciliation =
            await RemoteUserSyncReconciler.SynchronizeAsync(
                remoteMediaServerClient,
                userSyncStore,
                _remoteServerSettings.DeviceId,
                RemoteDeviceName,
                CreateSyncDeltaContext());
        _syncBaseline = reconciliation.NewBaseline ?? _syncBaseline;
        _syncBaselineRevision = reconciliation.BaselineRevision ?? reconciliation.WriteResult.Revision;
        var result = reconciliation.WriteResult;
        ApplyRemoteSnapshotState(
            result.Revision,
            result.UpdatedAt,
            hasSnapshot: true);
        return result;
    }

    /// <summary>
    /// Ready when the server can answer incrementally and this session has a
    /// baseline to measure local changes against. Anything less and the full
    /// snapshot path runs — which is also what seeds the baseline.
    /// </summary>
    private RemoteSyncDeltaContext? CreateSyncDeltaContext() =>
        _supportsDeltaSync && _hasRemoteSnapshot && _syncBaseline is not null
            ? new RemoteSyncDeltaContext(_syncBaselineRevision, _syncBaseline)
            : null;

    private void ApplyRemoteSnapshotState(
        long revision,
        DateTimeOffset? updatedAt,
        bool hasSnapshot)
    {
        _remoteSyncRevision = Math.Max(0, revision);
        _hasRemoteSnapshot = hasSnapshot;
        RemoteSnapshotUpdatedAt = updatedAt;
        OnPropertyChanged(nameof(RemoteSnapshotSummary));
        OnPropertyChanged(nameof(CanRestoreRemoteSnapshot));
    }

    /// <summary>
    /// Reports a sync failure without pretending the account is signed out,
    /// unless the server actually rejected the session.
    /// </summary>
    private void ReportRemoteSyncFailure(
        string messageKey,
        Exception exception)
    {
        if (RemoteSyncFailurePolicy.Classify(exception) is
            RemoteSyncFailureKind.SessionRejected)
        {
            ClearRemoteSession();
            // The server has refused this token, so it is of no use to the next
            // run either. A network failure is classified as recoverable and
            // deliberately keeps the token, so restarting while offline does
            // not sign the user out.
            _ = ForgetStoredRemoteSessionAsync();
            SetError(AccountFeedback, UiTextCatalog.Format(
                "RemoteSessionExpired",
                exception.Message));
            return;
        }

        SetError(AccountFeedback, UiTextCatalog.Format(
            messageKey,
            exception.Message));
    }

    public string UpdateActionText =>
        UiTextCatalog.Get(
            applicationUpdateService.InstallKind is
                ApplicationUpdateInstallKind.ReplaceAndRestart
                ? "UpdateAndRestart"
                : "DownloadAndInstallUpdate");

    private void StartUpdatePromptTimeout()
    {
        CancelUpdatePromptTimeout();
        _isUpdatePromptPinned = false;
        var cancellation = new CancellationTokenSource();
        _updatePromptTimeout = cancellation;
        _ = HideUpdatePromptAfterDelayAsync(cancellation.Token);
    }

    private async Task HideUpdatePromptAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                IsTelevision
                    ? TelevisionUpdatePromptLifetime
                    : UpdatePromptLifetime,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsDownloadingUpdate &&
                !_isUpdatePromptPinned)
            {
                IsUpdatePromptVisible = false;
            }
        });
    }

    private void CancelUpdatePromptTimeout()
    {
        _isUpdatePromptPinned = false;
        _updatePromptTimeout?.Cancel();
        _updatePromptTimeout?.Dispose();
        _updatePromptTimeout = null;
    }

    private void ClearRemoteSession()
    {
        // The party belongs to the session that is ending; its socket must
        // not keep speaking for an account that is no longer signed in.
        _ = _watchPartyService.ResetAsync();
        RefreshWatchPartyState();
        IsRemoteSignedIn = false;
        RemoteAccountDisplayName = string.Empty;
        _lastPublishedSharedList = null;
        RemoteSharedListUrl = string.Empty;
        IsRemoteRestoreConfirmationVisible = false;
        ApplyRemoteSnapshotState(
            revision: 0,
            updatedAt: null,
            hasSnapshot: false);
    }

    private void ApplyContentSourceSettings(
        ContentSourceProviderSettings settings)
    {
        _artworkPolicies =
            contentSourceCatalogService.GetArtworkPolicies(settings);
        _trustedArtworkOrigins = _artworkPolicies
            .Select(static policy => policy.Origin)
            .ToArray();
        _hasEnabledApiContentProviders = settings.Providers.Any(
            provider =>
                provider.Kind is ContentSourceProviderKind.Api &&
                provider.IsEnabled && contentSourceCatalogService.ProvidesUnitContent(provider.Id));
        ContentSourceProviders.Clear();
        foreach (var provider in settings.Providers)
        {
            ContentSourceProviders.Add(
                new ContentSourceProviderRowViewModel(
                    provider,
                    RemoveContentSourceAsync,
                    provider.Kind is ContentSourceProviderKind.Api
                        ? contentSourceCatalogService.GetApiProviderScope(
                            provider.Id)
                        : ApiContentProviderScope.All));
        }

        RebuildContentSourceGroups();
        RebuildProviderFilters(
            SelectedProviderFilter?.ContributorKey);
    }

    /// <summary>
    /// Arranges the providers by kind for the settings page. Grouped in the
    /// order the kinds are declared, so the page does not reshuffle itself as
    /// providers are switched on and off.
    /// </summary>
    private void RebuildContentSourceGroups()
    {
        ContentSourceProviderGroups.Clear();
        foreach (var group in ContentSourceProviders
                     .Where(row => row.Kind is not ContentSourceProviderKind.Api ||
                         contentSourceCatalogService.ProvidesUnitContent(row.Id))
                     .GroupBy(static row => row.Kind)
                     .OrderBy(static group => group.Key))
        {
            ContentSourceProviderGroups.Add(
                new ContentSourceProviderGroupViewModel(group.Key, group));
        }
    }

    private void ApplyMetadataSettings(MetadataProviderSettings settings)
    {
        var normalized = settings.Normalize();
        TmdbApiKey = normalized.TmdbApiKey;
        MyAnimeListClientId = normalized.MyAnimeListClientId;
        MetadataLanguage = normalized.PreferredLanguage;
    }

    /// <summary>
    /// Chapter order is a device preference, so every title opens the way the
    /// reader last left it.
    /// </summary>
    private void ApplyEntryOrderPreference(CatalogDetailsViewModel details)
    {
        details.ApplyEntryOrder(_readerDevicePreferences.ChapterOrderDescending);
        details.EntryOrderChangedAsync = async isDescending =>
        {
            _readerDevicePreferences = _readerDevicePreferences with
            {
                ChapterOrderDescending = isDescending
            };
            try
            {
                await UserMediaState.SaveReaderDevicePreferencesAsync(
                    _readerDevicePreferences);
            }
            catch
            {
                // The order still applies for this session.
            }
        };
    }

    private CatalogDetailsViewModel CreateDetailsViewModel(
        CatalogItemDetails details,
        string? contentSourceName = null,
        IReadOnlyList<Uri>? trustedRemoteOrigins = null,
        IReadOnlyList<ApiContentImageSet>? apiImageSets = null,
        IReadOnlyList<ApiContentVideoSet>? apiVideoSets = null,
        Uri? artworkReferrer = null,
        string? contentSourceProviderId = null)
    {
        trustedRemoteOrigins = (trustedRemoteOrigins ?? [])
            .Concat(_trustedArtworkOrigins).Distinct().ToArray();
        artworkReferrer ??=
            ContentSourceCatalogService.ResolveArtworkReferrer(
                details.Item.ThumbnailRef,
                _artworkPolicies);
        var completedContentKeys =
            _catalogProgressByContentKey.Values
                .Where(summary =>
                    summary.CatalogItemId == details.Item.Id &&
                    summary.Progress.IsCompleted)
                .Select(static summary =>
                    summary.Content.StableKey)
                .ToHashSet(StringComparer.Ordinal);
        var viewModel = new CatalogDetailsViewModel(
            details,
            mediaSessionService,
            OpenMediaSourceAsync,
            catalogMetadataRefreshService.GetProviderName(details),
            contentSourceName,
            trustedRemoteOrigins,
            apiImageSets,
            OpenApiImagesAsync,
            _hasEnabledApiContentProviders,
            ResolveAndOpenApiImagesAsync,
            apiVideoSets,
            OpenApiVideosAsync,
            _hasEnabledApiContentProviders,
            ResolveAndOpenApiVideosAsync,
            completedContentKeys,
            SetContentSeenAsync,
            SetAllContentSeenAsync,
            artworkReferrer,
            contentSourceProviderId,
            deleteEntryAsync: catalogBrowseService.CanDeleteUnits ? DeleteCatalogEntryAsync : null);
        ApplyEntryOrderPreference(viewModel);
        return viewModel;
    }

    private async Task HandleMagnetAsync(string magnetUri, bool userConfirmed)
    {
        if (string.IsNullOrWhiteSpace(magnetUri))
        {
            SetError(MagnetFeedback, UiTextCatalog.Get("PasteMagnetFirst"));
            return;
        }

        var activityTitle = UiTextCatalog.Get(
            "TorrentActivityManualMagnet");
        try
        {
            activityTitle =
                MagnetLink.Parse(magnetUri).DisplayName ??
                activityTitle;
        }
        catch (ArgumentException)
        {
            // The normal intake path below reports the detailed parse error.
        }

        var torrentActivityId = torrentActivityCoordinator.Begin(
            activityTitle,
            UiTextCatalog.Get("TorrentActivityOpeningMagnet"));
        try
        {
            IsBusy = true;
            SetError(MagnetFeedback, null);
            StatusMessage = null;

            var result = await magnetOpenService.ReceiveAsync(magnetUri, userConfirmed);
            switch (result.Status)
            {
                case MagnetOpenStatus.ConfirmationRequired:
                    torrentActivityCoordinator.Dismiss(torrentActivityId);
                    PendingMagnetLink = result.MagnetLink.OriginalUri;
                    StatusMessage =
                        UiTextCatalog.Get("MagnetConfirmationRequired");
                    break;

                case MagnetOpenStatus.EngineUnavailable:
                    torrentActivityCoordinator.Dismiss(torrentActivityId);
                    PendingMagnetLink = result.MagnetLink.OriginalUri;
                    SetError(MagnetFeedback, UiTextCatalog.Format(
                        "MagnetEngineUnavailable",
                        TorrentEngineName));
                    break;

                case MagnetOpenStatus.AlreadyOpen:
                    if (result.SessionId is { } existingSessionId)
                    {
                        torrentActivityId =
                            torrentActivityCoordinator.AttachSession(
                                torrentActivityId,
                                existingSessionId,
                                result.MagnetLink.DisplayName);
                    }
                    PendingMagnetLink = null;
                    MagnetLinkInput = string.Empty;
                    StatusMessage = UiTextCatalog.Get("MagnetAlreadyOpen");
                    break;

                case MagnetOpenStatus.Opened:
                    if (result.SessionId is { } sessionId)
                    {
                        torrentActivityId =
                            torrentActivityCoordinator.AttachSession(
                                torrentActivityId,
                                sessionId,
                                result.MagnetLink.DisplayName);
                    }
                    PendingMagnetLink = null;
                    MagnetLinkInput = string.Empty;
                    StatusMessage = BuildMagnetOpenedMessage(result);
                    if (result.ManifestImport?.Status is TorrentManifestImportStatus.Imported)
                    {
                        await RefreshAsync();
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(result.Status));
            }
        }
        catch (MagnetLinkFormatException exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "MagnetInvalid",
                exception.Message));
        }
        catch (MagnetCompatibilityException exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "MagnetIncompatible",
                exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "MagnetUnsafe",
                exception.Message));
        }
        catch (Exception exception)
        {
            torrentActivityCoordinator.MarkFailed(
                torrentActivityId,
                exception.Message);
            SetError(MagnetFeedback, UiTextCatalog.Format(
                "MagnetOpenFailed",
                exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildMagnetOpenedMessage(MagnetOpenResult result) =>
        BuildTorrentOpenedMessage(
            result.ManifestImport,
            result.Warning);

    private static string BuildTorrentOpenedMessage(
        TorrentManifestImportResult? manifestImport,
        string? warning)
    {
        var message = manifestImport?.Status switch
        {
            TorrentManifestImportStatus.Imported =>
                UiTextCatalog.Format(
                    "TorrentManifestImported",
                    manifestImport.CatalogImport?.CreatedCount ?? 0,
                    manifestImport.CatalogImport?.UpdatedCount ?? 0),
            TorrentManifestImportStatus.NotFound =>
                UiTextCatalog.Get("TorrentManifestNotFound"),
            TorrentManifestImportStatus.Pending =>
                UiTextCatalog.Get("TorrentManifestPending"),
            _ => UiTextCatalog.Get("TorrentOpened")
        };

        return string.IsNullOrWhiteSpace(warning)
            ? message
            : UiTextCatalog.Format(
                "ManifestWarning",
                message,
                warning);
    }

    private void OnManifestImportCompleted(
        object? sender,
        TorrentManifestImportCompletedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(
            () => _ = ApplyManifestImportCompletedAsync(eventArgs));

    private void OnTorrentActivityChanged(
        object? sender,
        TorrentActivityChangedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (eventArgs.WasRemoved)
            {
                _torrentActivities.Remove(eventArgs.Snapshot.Id);
                _dismissedTorrentActivities.Remove(eventArgs.Snapshot.Id);
            }
            else
            {
                _torrentActivities[eventArgs.Snapshot.Id] =
                    eventArgs.Snapshot;
            }

            SelectActiveTorrentActivity();
        });

    private void OnTorrentDownloadRecordChanged(
        object? sender,
        TorrentDownloadRecordChangedEventArgs eventArgs) =>
        _downloadRefresh.Post(RefreshDownloads);

    private void OnTorrentDownloadPersistenceFailed(
        object? sender,
        TorrentDownloadLibraryErrorEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            SetError(DownloadsFeedback, UiTextCatalog.Format(
                "DownloadsSaveFailed",
                eventArgs.Exception.Message));
        });

    private void RefreshDownloads()
    {
        if (_shutdownTask is not null) return;
        var records = torrentDownloadLibrary.Records;
        var recordIds = records
            .Select(static record => record.Id)
            .ToHashSet();
        for (var index = Downloads.Count - 1; index >= 0; index--)
        {
            if (!recordIds.Contains(Downloads[index].Id))
            {
                Downloads.RemoveAt(index);
            }
        }

        var existing = Downloads.ToDictionary(
            static download => download.Id);
        for (var targetIndex = 0;
             targetIndex < records.Count;
             targetIndex++)
        {
            var record = records[targetIndex];
            if (!existing.TryGetValue(record.Id, out var download))
            {
                download = new TorrentDownloadItemViewModel(
                    record,
                    NotifyDownloadSelectionChanged,
                    PauseDownloadAsync,
                    ResumeDownloadAsync,
                    RequestDeleteDownload);
                Downloads.Insert(
                    Math.Min(targetIndex, Downloads.Count),
                    download);
                existing[record.Id] = download;
            }
            else
            {
                download.Update(record);
                if (!ReferenceEquals(Downloads[targetIndex], download))
                {
                    Downloads.Move(Downloads.IndexOf(download), targetIndex);
                }
            }
        }

        NotifyDownloadsChanged();
    }

    private void SetPendingDownloadDeletion(
        IReadOnlyList<Guid> recordIds)
    {
        _pendingDownloadDeletion = recordIds
            .Distinct()
            .ToArray();
        IsDownloadDeleteConfirmationVisible =
            _pendingDownloadDeletion.Count > 0;
        OnPropertyChanged(nameof(DownloadDeleteConfirmationText));
    }

    private void NotifyDownloadSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedDownloads));
        OnPropertyChanged(nameof(SelectedDownloadCount));
        OnPropertyChanged(nameof(AllDownloadsSelected));
        OnPropertyChanged(nameof(DownloadSelectionSummary));
    }

    private void NotifyDownloadsChanged()
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(DownloadCount));
        OnPropertyChanged(nameof(ActiveDownloadCount));
        OnPropertyChanged(nameof(DownloadStorageSummary));
        NotifyDownloadSelectionChanged();
    }

    private void SelectActiveTorrentActivity()
    {
        var selected = _torrentActivities.Values
            .Where(activity =>
                !_dismissedTorrentActivities.Contains(activity.Id))
            .OrderByDescending(static activity => activity.StartedAt)
            .FirstOrDefault();
        if (Equals(ActiveTorrentActivity, selected))
        {
            OnActiveTorrentActivityChanged(selected);
            return;
        }

        ActiveTorrentActivity = selected;
    }

    private async Task ApplyManifestImportCompletedAsync(
        TorrentManifestImportCompletedEventArgs eventArgs)
    {
        try
        {
            StatusMessage = BuildTorrentOpenedMessage(
                eventArgs.Result,
                eventArgs.Warning);
            if (eventArgs.Result?.Status is
                TorrentManifestImportStatus.Imported)
            {
                await RefreshAsync();
            }
        }
        catch (Exception exception)
        {
            SetError(CatalogFeedback, UiTextCatalog.Format(
                "CatalogLoadFailed",
                exception.Message));
        }
    }

    private async Task ReloadUserListsAsync(
        Guid? selectedBrowsingListId = null,
        Guid? selectedDetailListId = null)
    {
        var browsingListId =
            selectedBrowsingListId ?? SelectedUserList?.Id;
        var detailListId =
            selectedDetailListId ?? SelectedDetailUserList?.Id;
        _userMediaLists = await OffUiThread.ReadAsync(
            () => UserMediaState.LoadUserListsAsync());

        UserLists.Clear();
        foreach (var list in _userMediaLists)
        {
            UserLists.Add(new UserMediaListViewModel(list));
        }

        OnPropertyChanged(nameof(HasUserLists));
        OnPropertyChanged(nameof(ShowNoUserLists));

        SelectedUserList = UserLists.FirstOrDefault(
                list => list.Id == browsingListId) ??
            UserLists.FirstOrDefault();
        SelectedDetailUserList = UserLists.FirstOrDefault(
                list => list.Id == detailListId) ??
            UserLists.FirstOrDefault();

        OnPropertyChanged(nameof(HasSelectedUserList));
        OnPropertyChanged(nameof(HasDetailUserListTarget));
        OnPropertyChanged(nameof(SelectedItemIsInDetailUserList));
        OnPropertyChanged(nameof(SelectedItemUserListActionText));
        RefreshSelectedItemUserLists();
        OnPropertyChanged(nameof(ShowEmptyUserList));
        if (IsUserListsOpen)
        {
            ApplyCatalogItems();
        }
    }

    private async Task ReloadCatalogUserStateAsync()
    {
        // Catalog data rather than user state, but it changes on the same
        // occasions the cards are rebuilt and is read by the same cards.
        try
        {
            _latestUnitsByItemId = await OffUiThread.ReadAsync(
                () => catalogBrowseService.GetLatestUnitsAsync());
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "catalog.latest-units-failed",
                exception.Message);
        }

        _favoriteCatalogItemIds = new HashSet<Guid>(
            await OffUiThread.ReadAsync(
                () => UserMediaState.LoadFavoriteCatalogItemIdsAsync()));
        var progress = await OffUiThread.ReadAsync(
            () => UserMediaState.LoadCatalogProgressAsync());
        _catalogProgressByContentKey = progress
            .GroupBy(
                static summary => summary.Content.StableKey,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(
                        static summary =>
                            summary.Progress.UpdatedAt)
                    .First(),
                StringComparer.Ordinal);
        RebuildLatestProgressIndex();
    }

    private bool TryNormalizeUserListName(
        string name,
        Guid? exceptListId,
        out string normalizedName)
    {
        SetError(ListsFeedback, null);
        StatusMessage = null;
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            SetError(ListsFeedback, UiTextCatalog.Get("UserListNameRequired"));
            return false;
        }

        try
        {
            normalizedName = UserMediaList.NormalizeName(name);
            var nameKey = UserMediaList.GetNameKey(normalizedName);
            if (UserLists.Any(list =>
                    list.Id != exceptListId &&
                    string.Equals(
                        list.NameKey,
                        nameKey,
                        StringComparison.Ordinal)))
            {
                SetError(ListsFeedback, UiTextCatalog.Get("UserListNameExists"));
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            SetError(ListsFeedback, UiTextCatalog.Format(
                "UserListSaveFailed",
                exception.Message));
            return false;
        }
    }

    private async Task RefreshAsync()
    {
        var mediaType = _selectedMediaType;
        var overview = await OffUiThread.ReadAsync(
            () => catalogBrowseService.GetOverviewAsync(mediaType));
        ApplyOverview(overview, resetProviderFilter: false);
    }

    private bool ShouldHideAdultContent(CatalogCard card) =>
        HideAdultContent && AdultContentCategoryFilter.Matches(
            card.Categories,
            card.Genres);

    private bool MatchesSelectedStatus(CatalogCard card) =>
        SelectedStatusFilter?.Status is not { } status ||
        (PublicationStatusApplicability.AppliesTo(
             card.MediaType,
             card.SubtypeCode) &&
         card.PublicationStatus == status);

    private void ApplyOverview(
        CatalogOverview overview,
        bool resetProviderFilter)
    {
        var visibleItems = overview.Items
            .Where(card => !ShouldHideAdultContent(card))
            .ToArray();
        TotalCount = visibleItems.Length;
        MovieCount = visibleItems.Count(
            static card => card.MediaType is MediaType.Movie);
        BookCount = visibleItems.Count(
            static card => card.MediaType is MediaType.Book);

        // The overview is fetched per media type; the anime split is a subtype
        // filter on top of it, so it is applied here rather than in the store.
        _catalogCards = visibleItems
            .Where(card => MediaSubtypeGroups.Matches(
                _selectedSubtypeGroup,
                card.MediaType,
                card.SubtypeCode))
            .ToArray();

        // An unfiltered overview is already the whole library, so take the copy
        // favourites need rather than fetching it again later.
        if (_selectedMediaType is null)
        {
            _allCatalogCards = visibleItems;
        }
        RebuildProviderFilters(
            resetProviderFilter
                ? null
                : SelectedProviderFilter?.ContributorKey,
            resetProviderFilter);
    }

    /// <param name="resetPage">
    /// Whether the result set is new enough that the current page number means
    /// nothing. Separate from <paramref name="resetCatalogFilters"/> because a
    /// search produces a new result set — so the page must go back to one —
    /// without that being a reason to discard what the user asked for.
    /// </param>
    private void RebuildProviderFilters(
        string? selectedContributorKey,
        bool resetCatalogFilters = false,
        bool? resetPage = null)
    {
        _isRebuildingProviderFilters = true;
        _isRebuildingCatalogFilters = true;
        try
        {
            // The category choices are derived from what came back, so the
            // selection is re-resolved against the new set below and falls
            // back to "all" only when the category is genuinely gone. Genre
            // and year are search criteria the user set, and a search running
            // with them must not clear them out from under itself.
            var selectedCategory = resetCatalogFilters
                ? null
                : SelectedCategoryFilter?.Category;
            if (resetCatalogFilters)
            {
                ShowFavoritesOnly = false;
            }

            ProviderFilters.Clear();
            var allProviders = new ProviderFilterViewModel(contributor: null);
            ProviderFilters.Add(allProviders);
            ProviderFilters.Add(ProviderFilterViewModel.Local());

            var availableOnlineProviders = ContentSourceProviders
                .Select(static provider => provider.ToConfiguration())
                .Where(provider =>
                    contentSourceCatalogService
                        .IsProviderAvailableForSearch(
                            provider,
                            _selectedMediaType,
                            _selectedSubtypeGroup))
                .ToArray();
            if (availableOnlineProviders.Length > 0)
            {
                ProviderFilters.Add(ProviderFilterViewModel.Online());
            }

            var cards = IsSearchActive
                ? _searchResults
                    .Where(result => !ShouldHideAdultContent(result.Card))
                    .Where(result => MatchesSelectedStatus(result.Card))
                    .Select(static result => result.Card)
                : _catalogCards
                    .Where(card => !ShouldHideAdultContent(card))
                    .Where(MatchesSelectedStatus);
            var cardArray = cards.ToArray();
            var providerNames = availableOnlineProviders
                .Where(provider => provider.Kind is not ContentSourceProviderKind.Api ||
                    contentSourceCatalogService.ProvidesUnitContent(provider.Id))
                .Select(static provider => provider.DisplayName)
                .Where(static name =>
                    !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    static name => name,
                    StringComparer.Create(
                        UiTextCatalog.CurrentCulture,
                        ignoreCase: true));
            foreach (var providerName in providerNames)
            {
                ProviderFilters.Add(
                    ProviderFilterViewModel.Named(providerName));
            }

            SelectedProviderFilter = ProviderFilters.FirstOrDefault(filter =>
                    string.Equals(
                        filter.ContributorKey,
                        selectedContributorKey,
                        StringComparison.Ordinal)) ??
                allProviders;

            CategoryFilters.Clear();
            foreach (var choice in CatalogFilterChoices.BuildCategories(
                         cardArray,
                         UiTextCatalog.CurrentCulture))
            {
                CategoryFilters.Add(choice);
            }

            SelectedCategoryFilter =
                CategoryFilters.FirstOrDefault(filter =>
                    string.Equals(
                        filter.Category,
                        selectedCategory,
                        StringComparison.OrdinalIgnoreCase)) ??
                CategoryFilters.FirstOrDefault();

            var selectedStatus = SelectedStatusFilter?.Status;
            StatusFilters.Clear();
            foreach (var choice in CatalogFilterChoices.BuildStatuses())
            {
                StatusFilters.Add(choice);
            }

            SelectedStatusFilter = StatusFilters.FirstOrDefault(filter =>
                    filter.Status == selectedStatus) ??
                StatusFilters.FirstOrDefault();

            RebuildGenreFilters();
            RebuildYearFilters();
        }
        finally
        {
            _isRebuildingProviderFilters = false;
            _isRebuildingCatalogFilters = false;
        }

        ApplyCatalogItems(resetPage: resetPage ?? resetCatalogFilters);
    }

    /// <summary>
    /// Rebuilt rather than cached so the labels follow a language change. The
    /// choices themselves come from <see cref="CatalogFilterChoices"/> and
    /// never depend on the current results.
    /// </summary>
    private void RebuildGenreFilters()
    {
        var selectedKey = SelectedGenreFilter?.Genre?.Key;
        GenreFilters.Clear();
        foreach (var choice in CatalogFilterChoices.BuildGenres())
        {
            GenreFilters.Add(choice);
        }

        SelectedGenreFilter =
            GenreFilters.FirstOrDefault(filter =>
                string.Equals(
                    filter.Genre?.Key,
                    selectedKey,
                    StringComparison.Ordinal)) ??
            GenreFilters.FirstOrDefault();
    }

    private void RebuildYearFilters()
    {
        var selectedYear = SelectedYearFilter?.Year;
        YearFilters.Clear();
        foreach (var choice in CatalogFilterChoices.BuildYears(
                     _catalogCards.Select(static card => card.ReleaseYear),
                     DateTimeOffset.UtcNow.Year + 1,
                     selectedYear))
        {
            YearFilters.Add(choice);
        }

        SelectedYearFilter =
            YearFilters.FirstOrDefault(filter =>
                filter.Year == selectedYear) ??
            YearFilters.FirstOrDefault();
    }

    private void ApplyCatalogItems(bool resetPage = false)
    {
        if (IsUserListsOpen)
        {
            var itemIds = SelectedUserList?.List.CatalogItemIds;
            CatalogCard[] userListCards = itemIds is null
                ? []
                : CatalogCardOrdering.FilterAndOrder(
                        // Favourite *titles*, not favourite providers: on a
                        // list the question is which of these you starred, and
                        // FilterAndOrder's own favourites flag answers the
                        // other one.
                        _catalogCards.Where(card =>
                            !ShouldHideAdultContent(card) &&
                            MatchesSelectedStatus(card) &&
                            itemIds.Contains(card.Id) &&
                            (!ShowFavoriteTitlesOnly ||
                             _favoriteCatalogItemIds.Contains(card.Id))),
                        null,
                        UiTextCatalog.CurrentCulture,
                        _favoriteCatalogItemIds)
                    .ToArray();
            UpdateCatalogPagination(
                userListCards.Length,
                resetPage);
            var visibleCards = CatalogPagination.GetPage(
                    userListCards,
                    CatalogPage)
                .Select(card => GetOrCreateCatalogCard(
                    card,
                    scope: "user-list"))
                .ToArray();
            ApplyFlatCatalogItems(visibleCards);

            OnPropertyChanged(nameof(ShowEmptyUserList));
            return;
        }

        if (IsSearchActive)
        {
            // One entry per title, not per provider. A search that matches the
            // local library and four providers used to answer with the same
            // film five times; the providers are now sources behind one card.
            var displayFilter = CreateSearchResultDisplayFilter();
            var searchResults = SearchResultGrouping
                .Group(_searchResults
                    .Where(displayFilter.Matches)
                // The star means the same thing here as on the catalog:
                // the titles the viewer starred.
                .Where(result =>
                    !ShouldHideAdultContent(result.Card) &&
                    MatchesSelectedStatus(result.Card) &&
                    (!ShowFavoritesOnly ||
                        _favoriteCatalogItemIds.Contains(result.Card.Id)))
                    .ToArray())
                .OrderByDescending(group =>
                    group.Primary.Card.UpdatedAt ??
                    DateTimeOffset.MinValue)
                .ThenByDescending(group =>
                    _favoriteCatalogItemIds.Contains(
                        group.Primary.Card.Id))
                .ThenBy(group =>
                    CatalogDisplayNameSelector.Select(
                        group.Primary.Card.Name,
                        group.Primary.Card.AlternativeNames),
                    StringComparer.Create(
                        UiTextCatalog.CurrentCulture,
                        ignoreCase: true))
                .ThenBy(static group => group.Primary.IsOnline)
                .ThenBy(
                    static group => group.Primary.Provider?.DisplayName,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            UpdateCatalogPagination(
                searchResults.Length,
                resetPage);
            var visibleResults = CatalogPagination.GetPage(
                    searchResults,
                    CatalogPage)
                .Select(GetOrCreateSearchCatalogCard)
                .ToArray();
            ApplyFlatCatalogItems(visibleResults);

            return;
        }

        // Favourites here means the titles the viewer starred, and the list
        // picker narrows to one of their lists. Both are applied before the
        // ordering, and FilterAndOrder's own favourites flag — which is about
        // favourite providers — is deliberately left off.
        var listMembers = SelectedCatalogListFilter?.List.CatalogItemIds;

        // Favourites are answered from the whole library only on the page that
        // is about the whole library. Everywhere else the page itself is the
        // question: asking for favourites on the manga page means the starred
        // manga, and answering with starred films as well is not a wider answer
        // but a wrong one. A category or a list narrows for the same reason.
        var source = (ShowFavoritesOnly &&
                     IsAllSection &&
                     IsFavoritesOnlySelection
            ? _allCatalogCards
            : _catalogCards)
            .Where(card =>
                !ShouldHideAdultContent(card) &&
                MatchesSelectedStatus(card));
        // No favourites first. Browsing is ordered by what changed most
        // recently, and lifting starred titles above that answers a question
        // the shelves were not asked: a star says "I like this", not "show me
        // this before the chapter that landed today". Filtering to favourites
        // is still there for when that is the question.
        var orderedCards = CatalogCardOrdering.FilterAndOrder(
                source.Where(card =>
                    (!ShowFavoritesOnly ||
                     _favoriteCatalogItemIds.Contains(card.Id)) &&
                    (listMembers is null || listMembers.Contains(card.Id))),
                SelectedProviderFilter?.ContributorKey,
                UiTextCatalog.CurrentCulture,
                favoriteCatalogItemIds: null,
                SelectedCategoryFilter?.Category,
                SelectedYearFilter?.Year);

        // Home splits the same filtered, ordered cards one way — by kind — and
        // the media-type pages below split them another. Both work from this
        // one ordered list, so every filter applies to rows and grid alike.
        //
        // Asking for favourites is the exception. Split into rows, the answer
        // is a handful of shelves holding one or two titles each and a scroll
        // to see them all, when the question was simply which titles are
        // starred. Favourites go to the flat grid, which is the whole list at
        // once.
        if (IsAllSection && !IsSearchActive && !ShowFavoritesOnly)
        {
            UpdateCatalogPagination(
                orderedCards.Count,
                resetPage: true);
            var sections = new List<CatalogSectionViewModel>();
            foreach (var section in CatalogHomeSectionPlanner.Plan(
                         orderedCards,
                         _catalogRecentActivityByItemId,
                         IsTelevision
                             ? 6
                             : CatalogHomeSectionPlanner.ShelfLimit))
            {
                var title = UiTextCatalog.Get(
                    CatalogHomeSectionPlanner.GetTitleKey(section.Kind));
                var cards = section.Cards
                    .Select(card => GetOrCreateCatalogCard(
                        card,
                        $"home:{section.Kind}"))
                    .ToArray();
                if (!_catalogSectionCache.TryGetValue(
                        section.Kind,
                        out var sectionViewModel))
                {
                    sectionViewModel = new CatalogSectionViewModel(
                        title,
                        cards,
                        section.TotalCount,
                        GetSectionCommand(section.Kind));
                    _catalogSectionCache[section.Kind] =
                        sectionViewModel;
                }
                else
                {
                    sectionViewModel.Update(
                        title,
                        cards,
                        section.TotalCount,
                        GetSectionCommand(section.Kind));
                }

                sections.Add(sectionViewModel);
            }

            ApplyHomeCatalogSections(sections);
            return;
        }

        // Movies, anime and books read like home: rows, not one long grid.
        // The grid stays reachable through the view toggle, which is also the
        // only view that pages through everything. Favourites skip the rows for
        // the same reason home does.
        if (IsBrowseSection && !PrefersCatalogGrid && !ShowFavoritesOnly)
        {
            var shelves = BuildBrowseShelves(orderedCards);
            if (shelves.Count > 0)
            {
                // Rows are not paged, so the page resets rather than being
                // carried into a view that cannot show it.
                UpdateCatalogPagination(
                    orderedCards.Count,
                    resetPage: true);
                ApplyBrowseShelves(shelves);
                return;
            }

            // Too small a library to make rows out of. Fall through to the
            // grid rather than showing an empty page.
        }

        UpdateCatalogPagination(
            orderedCards.Count,
            resetPage);
        var visibleBrowseCards = CatalogPagination.GetPage(
                orderedCards,
                CatalogPage)
            .Select(card => GetOrCreateCatalogCard(
                card,
                scope: "flat"))
            .ToArray();
        ApplyFlatCatalogItems(visibleBrowseCards);
    }

    private IReadOnlyList<CatalogSectionViewModel> BuildBrowseShelves(
        IReadOnlyList<CatalogCard> orderedCards)
    {
        var shelves = new List<CatalogSectionViewModel>();
        var section = $"{_selectedMediaType}:{_selectedSubtypeGroup}";
        var live = new HashSet<(string, CatalogBrowseShelfKey)>();
        foreach (var shelf in CatalogBrowseShelfPlanner.Plan(
                     orderedCards,
                     IsTelevision
                         ? 6
                         : CatalogBrowseShelfPlanner.ShelfLimit))
        {
            var title = shelf.Genre is { } genre
                ? new CatalogGenreFilterViewModel(genre).Label
                : UiTextCatalog.Get(
                    CatalogBrowseShelfPlanner.GetTitleKey(shelf.Key.Kind));
            var command = GetGenreShelfCommand(shelf.Genre);
            var cards = shelf.Cards
                .Select(card => GetOrCreateCatalogCard(
                    card,
                    $"shelf:{shelf.Key.Kind}:{shelf.Key.GenreKey}"))
                .ToArray();
            var cacheKey = (section, shelf.Key);
            if (!_browseShelfCache.TryGetValue(cacheKey, out var shelfViewModel))
            {
                shelfViewModel = new CatalogSectionViewModel(
                    title,
                    cards,
                    shelf.TotalCount,
                    command,
                    leadsBeyondTheShelf: shelf.Genre is not null);
                _browseShelfCache[cacheKey] = shelfViewModel;
            }
            else
            {
                shelfViewModel.Update(
                    title,
                    cards,
                    shelf.TotalCount,
                    command,
                    leadsBeyondTheShelf: shelf.Genre is not null);
            }

            shelves.Add(shelfViewModel);
            live.Add(cacheKey);
        }

        // Drop this section's rows that no longer qualify. Only twelve genres
        // earn a shelf at a time, but a session that plays with the provider,
        // category, year and favourites filters admits every genre eventually,
        // and nothing ever removed the losers — so each retired row sat here
        // holding twelve card view models, and their artwork, for the life of
        // the process. Other sections are left alone: their rows are still on
        // screen the moment the viewer switches back.
        foreach (var stale in _browseShelfCache.Keys
                     .Where(key =>
                         string.Equals(
                             key.Section,
                             section,
                             StringComparison.Ordinal) &&
                         !live.Contains(key))
                     .ToArray())
        {
            _browseShelfCache.Remove(stale);
        }

        return shelves;
    }

    /// <summary>
    /// Only a genre row has somewhere to go. "Recently added" is the whole of
    /// what it stands for, so it stays a row without a destination — the same
    /// way the audio shelf does on home.
    /// </summary>
    private IRelayCommand? GetGenreShelfCommand(ApiContentGenre? genre)
    {
        if (genre is null)
        {
            return null;
        }

        if (!_genreShelfCommands.TryGetValue(genre.Key, out var command))
        {
            command = new RelayCommand(() => OpenGenreShelf(genre));
            _genreShelfCommands[genre.Key] = command;
        }

        return command;
    }

    private void UpdateCatalogPagination(
        int resultCount,
        bool resetPage)
    {
        CatalogResultCount = resultCount;
        CatalogPageCount = CatalogPagination.GetPageCount(resultCount);
        CatalogPage = resetPage
            ? 1
            : CatalogPagination.ClampPage(
                CatalogPage,
                resultCount);
        OnPropertyChanged(nameof(ShowCatalogPagination));
    }

    /// <summary>
    /// The page a home shelf opens. Audio has no section of its own yet, so its
    /// shelf stays a preview without a destination.
    /// </summary>
    private IRelayCommand? GetSectionCommand(CatalogHomeSectionKind kind) =>
        kind switch
        {
            CatalogHomeSectionKind.MoviesAndSeries => ShowMoviesCommand,
            CatalogHomeSectionKind.Anime => ShowAnimeCommand,
            CatalogHomeSectionKind.BooksAndManga => ShowBooksCommand,
            _ => null
        };

    private CatalogCardViewModel GetOrCreateCatalogCard(
        CatalogCard card,
        string scope)
    {
        var favorite = _favoriteCatalogItemIds.Contains(card.Id);
        var progress = _catalogProgressByItemId.GetValueOrDefault(card.Id);

        // Every chapter reached, not only the ones far enough in to resume
        // from, so a title announces what has arrived even when the chapter
        // that was opened was never marked seen.
        var activity =
            _catalogRecentActivityByItemId.GetValueOrDefault(card.Id);
        var latestUnit = _latestUnitsByItemId.GetValueOrDefault(card.Id);
        var artworkReferrer =
            ContentSourceCatalogService.ResolveArtworkReferrer(
                card.ThumbnailRef,
                _artworkPolicies);
        var key = new CatalogCardReuseKey(
            scope,
            card.Id.ToString("N"));
        var signature = BuildCatalogCardSignature(
            card,
            favorite,
            progress,
            UiTextCatalog.Get("LocalResult"),
            isOnline: false,
            _trustedArtworkOrigins,
            artworkReferrer,
            activity,
            latestUnit);
        return GetOrCreateCatalogCard(
            key,
            signature,
            () => new CatalogCardViewModel(
                card,
                mediaSessionService,
                _trustedArtworkOrigins,
                OpenDetailsAsync,
                favorite,
                progress,
                SetCatalogItemFavoriteAsync,
                ContinueProgressAsync,
                artworkReferrer,
                activity,
                latestUnit));
    }

    private CatalogCardViewModel GetOrCreateSearchCatalogCard(
        SearchResultGroup group)
    {
        var searchResult = group.Primary;
        var favorite =
            _favoriteCatalogItemIds.Contains(searchResult.Card.Id);
        var progress = _catalogProgressByItemId.GetValueOrDefault(
            searchResult.Card.Id);
        var artworkPolicies =
            contentSourceCatalogService.GetArtworkPolicies(searchResult);
        var origins = artworkPolicies
            .Select(static policy => policy.Origin)
            .ToArray();
        var artworkReferrer =
            ContentSourceCatalogService.ResolveArtworkReferrer(
                searchResult.Card.ThumbnailRef,
                artworkPolicies);
        var originBadge = searchResult.IsOnline
            ? UiTextCatalog.Format(
                "OnlineResult",
                searchResult.Provider?.DisplayName ??
                UiTextCatalog.Get("Unknown"))
            : UiTextCatalog.Get("LocalResult");
        if (group.HasAlternates)
        {
            originBadge = UiTextCatalog.Format(
                "SearchResultSources",
                originBadge,
                group.SourceCount);
        }

        var key = new CatalogCardReuseKey(
            $"search:{_searchGeneration}",
            group.Identity);
        var signature = BuildCatalogCardSignature(
            searchResult.Card,
            favorite,
            progress,
            originBadge,
            searchResult.IsOnline,
            origins,
            artworkReferrer);
        return GetOrCreateCatalogCard(
            key,
            signature,
            () => new CatalogCardViewModel(
                searchResult,
                mediaSessionService,
                origins,
                _ => OpenSearchGroupAsync(group),
                favorite,
                progress,
                SetCatalogItemFavoriteAsync,
                ContinueProgressAsync,
                originBadge,
                artworkReferrer));
    }

    /// <summary>
    /// Opens a deduplicated search entry, then folds in what the other
    /// providers hold for the same title.
    ///
    /// The importer already converges matching titles onto one catalog item,
    /// so loading each remaining provider leaves the details view listing
    /// every provider's media sources instead of sending the viewer back to
    /// search to try the next one. The primary opens first and stays on screen
    /// while the rest arrive, because usually the first source is the wanted
    /// one and the others are only a fallback.
    /// </summary>
    private async Task OpenSearchGroupAsync(SearchResultGroup group)
    {
        await OpenSearchResultAsync(group.Primary);
        var alternates = group.OnlineAlternates.ToArray();
        if (alternates.Length == 0 || SelectedDetails is null)
        {
            return;
        }

        var itemId = SelectedDetails.Details.Item.Id;
        var load = BeginDetailsLoad();
        var loadedCount = 0;
        try
        {
            foreach (var alternate in alternates)
            {
                if (!IsCurrentDetailsLoad(load))
                {
                    return;
                }

                try
                {
                    await OffUiThread.ReadAsync(() => contentSourceCatalogService.LoadOnlineItemAsync(
                        alternate,
                        load.Token));
                    loadedCount++;
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException)
                {
                    // One provider failing is not the others' problem, and the
                    // primary source is already open and playable.
                }
            }

            if (loadedCount == 0 || !IsCurrentDetailsLoad(load))
            {
                return;
            }

            await RefreshAsync();
            if (!IsCurrentDetailsLoad(load))
            {
                return;
            }

            await OpenDetailsAsync(itemId);

        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndDetailsLoad(load);
        }
    }

    private CatalogCardViewModel GetOrCreateCatalogCard(
        CatalogCardReuseKey key,
        string signature,
        Func<CatalogCardViewModel> factory)
    {
        var use = ++_catalogCardCacheClock;
        if (_catalogCardCache.TryGetValue(key, out var cached) &&
            string.Equals(
                cached.Signature,
                signature,
                StringComparison.Ordinal))
        {
            cached.LastUse = use;
            return cached.ViewModel;
        }

        var viewModel = factory();
        // The outgoing card owns a decoded poster. Overwriting the entry
        // without this leaves that image to a finalizer.
        cached?.ViewModel.Dispose();
        _catalogCardCache[key] =
            new CachedCatalogCard(signature, viewModel, use);
        return viewModel;
    }

    private void ApplyFlatCatalogItems(
        IReadOnlyList<CatalogCardViewModel> items)
    {
        ObservableCollectionSynchronizer.Synchronize(Items, items);
        ObservableCollectionSynchronizer.Synchronize(
            HomeSections,
            Array.Empty<CatalogSectionViewModel>());
        ObservableCollectionSynchronizer.Synchronize(
            BrowseShelves,
            Array.Empty<CatalogSectionViewModel>());
        CompleteCatalogCollectionUpdate();
    }

    private void ApplyHomeCatalogSections(
        IReadOnlyList<CatalogSectionViewModel> sections)
    {
        ObservableCollectionSynchronizer.Synchronize(
            Items,
            Array.Empty<CatalogCardViewModel>());
        ObservableCollectionSynchronizer.Synchronize(
            BrowseShelves,
            Array.Empty<CatalogSectionViewModel>());
        ObservableCollectionSynchronizer.Synchronize(
            HomeSections,
            sections);
        CompleteCatalogCollectionUpdate();
    }

    private void ApplyBrowseShelves(
        IReadOnlyList<CatalogSectionViewModel> shelves)
    {
        ObservableCollectionSynchronizer.Synchronize(
            Items,
            Array.Empty<CatalogCardViewModel>());
        ObservableCollectionSynchronizer.Synchronize(
            HomeSections,
            Array.Empty<CatalogSectionViewModel>());
        ObservableCollectionSynchronizer.Synchronize(
            BrowseShelves,
            shelves);
        CompleteCatalogCollectionUpdate();
    }

    private void CompleteCatalogCollectionUpdate()
    {
        if (_catalogCardCache.Count > CatalogCardCacheCapacity)
        {
            var removeCount =
                _catalogCardCache.Count - CatalogCardCacheCapacity;
            foreach (var key in _catalogCardCache
                         .OrderBy(static pair => pair.Value.LastUse)
                         .Take(removeCount)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                if (_catalogCardCache.Remove(key, out var evicted))
                {
                    evicted.ViewModel.Dispose();
                }
            }
        }

        OnPropertyChanged(nameof(ShowHomeSections));
        OnPropertyChanged(nameof(ShowBrowseShelves));
        OnPropertyChanged(nameof(ShowCatalogViewToggle));
        OnPropertyChanged(nameof(ShowFlatCatalogList));
        OnPropertyChanged(nameof(ShowCatalogPagination));
    }

    private string BuildCatalogCardSignature(
        CatalogCard card,
        bool isFavorite,
        CatalogProgressSummary? progress,
        string originBadge,
        bool isOnline,
        IReadOnlyList<Uri> trustedOrigins,
        Uri? artworkReferrer,
        CatalogProgressSummary? activity = null,
        CatalogLatestUnit? latestUnit = null)
    {
        var progressSignature = progress is null
            ? string.Empty
            : string.Join(
                '\u001e',
                progress.Content.StableKey,
                progress.Progress.PositionKind.ToString(),
                progress.Progress.Position.ToString(
                    "R",
                    CultureInfo.InvariantCulture),
                progress.Progress.IsCompleted.ToString(),
                progress.Progress.UpdatedAt.ToString(
                    "O",
                    CultureInfo.InvariantCulture));

        // A chapter arriving changes nothing else about the card, so without
        // this the cached view model would keep its old badge.
        var waitingSignature = activity is null
            ? string.Empty
            : string.Join(
                '\u001e',
                activity.LaterUnitCount.ToString(CultureInfo.InvariantCulture),
                activity.LatestUnitNumber ?? string.Empty,
                activity.Content.UnitKind?.ToString() ?? string.Empty,
                latestUnit?.DisplayNumber ?? string.Empty,
                latestUnit?.Kind.ToString() ?? string.Empty);
        return string.Join(
            '\u001f',
            card.Id.ToString("N"),
            card.MediaType.ToString(),
            card.SubtypeCode,
            card.Name,
            card.Description,
            card.ThumbnailRef ?? string.Empty,
             card.ReleaseDate ?? string.Empty,
             card.PublicationStatus.ToString(),
             card.UpdatedAt?.ToString(
                "O",
                CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join('\u001e', card.Genres),
            string.Join('\u001e', card.Categories),
            string.Join(
                '\u001e',
                card.AlternativeNames.Select(static name =>
                    $"{name.Name}\u001d{name.Language}\u001d{name.Kind}")),
            string.Join(
                '\u001e',
                card.Distributors.Select(static distributor =>
                    $"{distributor.StableKey}\u001d{distributor.Name}")),
            isFavorite.ToString(),
            progressSignature,
            waitingSignature,
            originBadge,
            isOnline.ToString(),
            artworkReferrer?.AbsoluteUri ?? string.Empty,
            UiTextCatalog.CurrentCulture.Name,
            string.Join(
                '\u001e',
                trustedOrigins
                    .Select(static origin => origin.AbsoluteUri)
                    .Order(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The display-narrowing rules for the current picker state. Rebuilt per
    /// use because every input is a live view-model property.
    /// </summary>
    private SearchResultDisplayFilter CreateSearchResultDisplayFilter() =>
        new(
            SelectedProviderFilter?.ContributorKey,
            SelectedCategoryFilter?.Category,
            provider =>
                contentSourceCatalogService.IsProviderAvailableForSearch(
                    provider,
                    _selectedMediaType,
                    _selectedSubtypeGroup));

    private async ValueTask<IReadOnlyList<PlatformSubtitleTrack>> PrepareSubtitlesAsync(
        IReadOnlyList<CatalogMediaSourceDetails> sources)
    {
        var result = new List<PlatformSubtitleTrack>();
        foreach (var source in sources.Where(source =>
                     source.Layout is CatalogMediaSourceLayout.SingleFile &&
                     CatalogMediaSourceViewModel.IsSubtitle(source.AssetRole)))
        {
            try
            {
                var prepared = await mediaSessionService.ResolveSingleFileAsync(source);
                var uri = await mediaSessionService.GetPlaybackUriWithoutSelectingAsync(prepared);
                if (uri is not null)
                {
                    var language = string.IsNullOrWhiteSpace(source.Language)
                        ? "und"
                        : source.Language;
                    result.Add(new PlatformSubtitleTrack(
                        uri,
                        source.Language ?? UiTextCatalog.Get("Subtitles"),
                        language,
                        result.Count == 0));
                }
            }
            catch
            {
                // A broken optional subtitle must not prevent the primary media from playing.
            }
        }

        return result;
    }

    private async ValueTask<IReadOnlyList<PlatformMediaVariant>> PrepareAlternateAudioVariantsAsync(
        CatalogMediaSourceSelection selection)
    {
        if (selection.MediaType is not (MediaType.Movie or MediaType.Audio) ||
            !CatalogMediaSourceViewModel.IsPrimary(selection.Source.AssetRole))
        {
            return [];
        }

        var selectedIsAudio = IsAudio(selection);
        var result = new List<PlatformMediaVariant>();
        foreach (var source in selection.RelatedSources
                     .Where(source =>
                         !Equals(source, selection.Source) &&
                         source.Layout is CatalogMediaSourceLayout.SingleFile &&
                         CatalogMediaSourceViewModel.IsPrimary(source.AssetRole) &&
                         IsAudioSource(source, selection.MediaType) == selectedIsAudio)
                     .OrderBy(static source => source.SortOrder)
                     .Take(15))
        {
            try
            {
                var prepared = await mediaSessionService.ResolveSingleFileAsync(source);
                var playbackUri =
                    await mediaSessionService.GetPlaybackUriWithoutSelectingAsync(prepared);
                result.Add(CreatePlatformMediaVariant(
                    selection.MediaType,
                    source,
                    prepared,
                    playbackUri));
            }
            catch
            {
                // A broken optional rendition must not prevent the selected source from playing.
            }
        }

        return result;
    }

    private PlatformMediaVariant CreatePlatformMediaVariant(
        MediaType mediaType,
        CatalogMediaSourceDetails source,
        PreparedTorrentFile prepared,
        Uri? playbackUri) =>
        new(
            new PlatformMediaSource(
                playbackUri,
                cancellationToken => mediaSessionService.OpenReadAsync(
                    prepared,
                    cancellationToken)),
            BuildPlaybackVariantLabel(source, prepared),
            IsAudioSource(source, mediaType),
            prepared.MimeType);

    private static string BuildPlaybackVariantLabel(
        CatalogMediaSourceDetails source,
        PreparedTorrentFile prepared)
    {
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(source.Language) ||
            string.Equals(source.Language, "und", StringComparison.OrdinalIgnoreCase)
                ? UiTextCatalog.Get("OriginalUnspecified")
                : source.Language.Trim()
        };
        if (!string.IsNullOrWhiteSpace(source.VideoQuality))
        {
            parts.Add(source.VideoQuality.Trim());
        }

        if (!string.IsNullOrWhiteSpace(source.Container))
        {
            parts.Add(source.Container.Trim().ToUpperInvariant());
        }
        else
        {
            var extension = Path.GetExtension(prepared.Path);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                parts.Add(extension.TrimStart('.').ToUpperInvariant());
            }
        }

        return string.Join(" · ", parts);
    }

    private void StartPlaybackMonitor(CatalogContentIdentity contentIdentity)
    {
        _activePlaybackIdentity = contentIdentity;
        _playbackMonitorCancellation = new CancellationTokenSource();
        _playbackMonitorTask = MonitorPlaybackAsync(
            contentIdentity,
            _playbackMonitorCancellation.Token);

        // Off the start path: the party learns what started, and a member who
        // opened the party's own title is aligned to the shared timeline.
        _ = NotifyWatchPartyPlaybackStartedAsync(contentIdentity);
    }

    private void OnPlaybackSurfaceChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshPlaybackSurface();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshPlaybackSurface);
        }
    }

    private void RefreshPlaybackSurface()
    {
        OnPropertyChanged(nameof(EmbeddedMediaView));
        IsEmbeddedMediaOpen =
            platformMediaPlayer.EmbeddedView is not null || _activeApiVideoSelection is not null;
    }

    private void OnNativePlaybackStarted(
        object? sender,
        EventArgs eventArgs)
    {
        // The capture hook may run on WebView2's callback thread or Android's
        // network thread. View ownership and the progress monitor both belong
        // to Avalonia's dispatcher.
        Dispatcher.UIThread.Post(
            () =>
            {
                RefreshPlaybackSurface();
                if (_activePlaybackIdentity is null &&
                    _playbackMonitorCancellation is null &&
                    _activeApiVideoSelection is { } selection)
                {
                    StartPlaybackMonitor(selection.ContentIdentity);
                }
            });
    }

    private void OnEmbeddedFullscreenChanged(
        object? sender,
        EmbeddedWebFullscreenChangedEventArgs eventArgs) =>
        SetEmbeddedFullscreenOnUiThread(eventArgs.IsFullscreen);

    private void OnEmbeddedFullscreenExitRequested(
        object? sender,
        EventArgs eventArgs) =>
        SetEmbeddedFullscreenOnUiThread(isFullscreen: false);

    private void SetEmbeddedFullscreenOnUiThread(bool isFullscreen)
    {
        void Apply()
        {
            if (IsEmbeddedMediaOpen &&
                IsPlayerFullscreen != isFullscreen)
            {
                IsPlayerFullscreen = isFullscreen;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply, DispatcherPriority.Input);
        }
    }

    private async Task MonitorPlaybackAsync(
        CatalogContentIdentity contentIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            await PlaybackProgressMonitor.RunAsync(
                token => platformMediaPlayer.GetStateAsync(token),
                async (state, token) =>
                {
                    ApplyPlaybackStateFeedback(state);

                    await _watchPartyService.OnPlaybackTickAsync(state, token);
                    RefreshWatchPartyTransportState();
                },
                (progress, token) => UserMediaState.SaveProgressAsync(
                    contentIdentity, progress, token),
                () => IsContentSeen(contentIdentity),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(PlaybackFeedback, UiTextCatalog.Format(
                "PlaybackProgressSaveFailed",
                exception.Message));
        }
    }

    private async Task CloseActivePlaybackAsync(
        bool clearApiVideoServers = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clearApiVideoServers)
        {
            CancelApiUnitLookup();
            IsEmbeddedMediaOpen = false;
        }
        var wasPlaying = _activePlaybackIdentity is not null;
        if (wasPlaying && platformMediaPlayer.IsAvailable)
        {
            try
            {
                var state = await platformMediaPlayer.GetStateAsync(cancellationToken);
                await SavePlaybackProgressAsync(
                    _activePlaybackIdentity!,
                    state);
            }
            catch
            {
                // Closing or replacing a player must continue even if its final snapshot fails.
            }
        }

        // After the final position is written, so the upload it releases
        // carries where playback actually stopped.
        cancellationToken.ThrowIfCancellationRequested();
        if (wasPlaying)
        {
            FlushProgressSync();
        }

        await StopPlaybackMonitorAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (wasPlaying)
        {
            await _watchPartyService.NotifyPlaybackStartedAsync(null, 0);
        }

        if (clearApiVideoServers)
        {
            _activeApiVideoSelection = null;
            ActiveVideoServers.Clear();
            PlaybackFeedback.Message = null;
            ClearApiProviderChoices();
            OnPropertyChanged(nameof(HasActiveVideoServers));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await platformMediaPlayer.CloseAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _activePlaybackIdentity = null;
        IsEmbeddedMediaOpen = !clearApiVideoServers && _activeApiVideoSelection is not null;
        OnPropertyChanged(nameof(EmbeddedMediaView));
        ActiveMediaTitle = IsEmbeddedMediaOpen ? _activeApiVideoSelection?.Title : null;
    }

    private async Task StopPlaybackMonitorAsync()
    {
        var cancellation = Interlocked.Exchange(ref _playbackMonitorCancellation, null);
        if (cancellation is null)
        {
            return;
        }
        var monitor = Interlocked.Exchange(ref _playbackMonitorTask, null);
        try
        {
            await cancellation.CancelAsync();
            if (monitor is not null) await monitor;
        }
        finally { cancellation.Dispose(); }
    }

    private Task SavePlaybackProgressAsync(
        CatalogContentIdentity contentIdentity,
        PlatformMediaState state)
    {
        var reachedEnd = state.IsEnded ||
            state.DurationSeconds is > 0 &&
            state.PositionSeconds >= state.DurationSeconds.Value - 2;
        return UserMediaState.SaveProgressAsync(
                contentIdentity,
                new MediaProgressState(
                    reachedEnd ? 0 : Math.Max(0, state.PositionSeconds),
                    MediaPositionKind.Seconds,
                    reachedEnd || IsContentSeen(contentIdentity),
                    DateTimeOffset.UtcNow))
            .AsTask();
    }

    private static bool IsAudioOrVideo(CatalogMediaSourceSelection selection) =>
        selection.MediaType is MediaType.Movie or MediaType.Audio ||
        selection.Source.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) is true ||
        selection.Source.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) is true;

    private static bool IsAudio(CatalogMediaSourceSelection selection) =>
        IsAudioSource(selection.Source, selection.MediaType);

    private static bool IsAudioSource(
        CatalogMediaSourceDetails source,
        MediaType mediaType) =>
        source.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) is true ||
        mediaType is MediaType.Audio;

    private static bool IsCbz(CatalogMediaSourceSelection selection) =>
        selection.MediaType is MediaType.Book &&
        (string.Equals(selection.Source.Container, "CBZ", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(selection.Source.MimeType, "application/vnd.comicbook+zip", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(selection.Source.MimeType, "application/x-cbz", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Path.GetExtension(selection.Source.FilePath), ".cbz", StringComparison.OrdinalIgnoreCase));

    private static bool IsEpub(CatalogMediaSourceSelection selection) =>
        selection.MediaType is MediaType.Book &&
        (string.Equals(selection.Source.Container, "EPUB", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(selection.Source.MimeType, "application/epub+zip", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Path.GetExtension(selection.Source.FilePath), ".epub", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the chapter currently opening was reached by stepping from
    /// another one.
    /// <para>
    /// A chapter stepped into starts at its first page. The position the reader
    /// restores belongs to the last time that chapter was read, which is not
    /// where someone moving through a book wants to be put.
    /// </para>
    /// </summary>
    internal bool IsSteppingReaderChapter { get; private set; }

    private async Task OpenAdjacentReaderChapterAsync(int offset)
    {
        var navigation = _readerChapterNavigation;
        if (navigation is null)
        {
            return;
        }

        var targetIndex = navigation.CurrentIndex + offset;
        if (targetIndex < 0 ||
            targetIndex >= navigation.Chapters.Count)
        {
            return;
        }

        var unit = navigation.Chapters[targetIndex];
        var title = BuildUnitTitle(unit);
        IsSteppingReaderChapter = true;
        try
        {
            if (unit.Sources.Count > 0)
            {
                await OpenMediaSourceAsync(new CatalogMediaSourceSelection(
                    unit.Identity,
                    navigation.Details.Identity,
                    title,
                    navigation.Details.Item.MediaType,
                    unit.Sources[0],
                    unit.Sources));
                return;
            }

            var imageSet = SelectedDetails?.ApiImageSets.FirstOrDefault(
                candidate => candidate.Matches(
                    unit.Kind,
                    unit.DisplayNumber,
                    unit.Identity.SeasonNumber));
            if (imageSet is not null)
            {
                await OpenApiImagesAsync(new ApiContentImageSelection(
                    unit.Identity,
                    navigation.Details.Identity,
                    title,
                    imageSet,
                    navigation.PreferredApiProviderId));
                return;
            }

            await ResolveAndOpenApiImagesAsync(
                new ApiContentUnitLookupSelection(
                    navigation.Details,
                    unit,
                    title),
                navigation.PreferredApiProviderId,
                automaticallyChooseProvider: true);
        }
        finally
        {
            IsSteppingReaderChapter = false;
        }
    }

    /// <summary>
    /// Opens the episode before or after the one playing, preferring a local
    /// source and falling back to the same provider flow the details page uses.
    /// </summary>
    private async Task OpenAdjacentEpisodeAsync(int offset)
    {
        var navigation = _playerEpisodeNavigation;
        if (navigation is null)
        {
            return;
        }

        var targetIndex = navigation.CurrentIndex + offset;
        if (targetIndex < 0 || targetIndex >= navigation.Chapters.Count)
        {
            return;
        }

        var unit = navigation.Chapters[targetIndex];
        var title = BuildUnitTitle(unit);
        if (unit.Sources.Count > 0)
        {
            await OpenMediaSourceAsync(new CatalogMediaSourceSelection(
                unit.Identity,
                navigation.Details.Identity,
                title,
                navigation.Details.Item.MediaType,
                unit.Sources[0],
                unit.Sources));
            return;
        }

        var videoSet = SelectedDetails?.ApiVideoSets.FirstOrDefault(
            candidate => candidate.Matches(
                unit.Kind,
                unit.DisplayNumber,
                unit.Identity.SeasonNumber));
        if (videoSet is not null)
        {
            await OpenApiVideosAsync(new ApiContentVideoSelection(
                unit.Identity,
                navigation.Details.Identity,
                title,
                videoSet,
                navigation.PreferredApiProviderId));
            return;
        }

        await ResolveAndOpenAdjacentEpisodeAsync(
            new ApiContentUnitLookupSelection(
                navigation.Details,
                unit,
                title),
            navigation.PreferredApiProviderId);
    }

    /// <summary>
    /// Opens the next or previous episode without asking which source to use,
    /// preferring the one already playing.
    /// <para>
    /// The provider picker lives in the episode row on the details page, which
    /// is behind the open player. Failed attempts keep the player open and
    /// expose those provider choices beside its server selector.
    /// </para>
    /// </summary>
    private async Task ResolveAndOpenAdjacentEpisodeAsync(
        ApiContentUnitLookupSelection selection,
        string? preferredProviderId)
    {
        var lookupCancellation = BeginApiUnitLookup();
        var cancellationToken = lookupCancellation.Token;
        try
        {
            IsBusy = true;
            SetError(PlaybackFeedback, null);
            StatusMessage = null;
            ClearApiProviderChoices();
            var candidates = await contentSourceCatalogService
                .FindApiVideoCandidatesForUnitAsync(
                    selection.Details,
                    selection.Unit,
                    cancellationToken);
            var preferredIndex =
                ApiProviderPreference.FindPreferredProviderIndex(
                    candidates,
                    preferredProviderId,
                    static candidate => candidate.ProviderId);
            for (var attempt = 0; attempt < candidates.Count; attempt++)
            {
                var candidateIndex = ApiProviderPreference.GetAttemptIndex(
                    attempt,
                    candidates.Count,
                    preferredIndex);
                try
                {
                    var resolution = await contentSourceCatalogService
                        .ResolveApiVideosForUnitAsync(
                            selection.Details,
                            selection.Unit,
                            candidates[candidateIndex],
                            cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await ApplyApiVideoResolutionAsync(selection, resolution, cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ApplicationDiagnosticLog.Warning(
                        "api.provider-picker",
                        $"media=video; stage=adjacent-episode-rejected; " +
                        $"provider={candidates[candidateIndex].ProviderId}; " +
                        $"failure={exception.GetType().Name}");
                    await TryCloseFailedApiPlaybackAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            await ShowEpisodeProvidersInPlayerAsync(
                selection,
                candidates);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await TryCloseFailedApiPlaybackAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            SetError(PlaybackFeedback, UiTextCatalog.Format(
                "OnlineEpisodeResolveFailed",
                exception.Message));
        }
        finally
        {
            SelectedDetails?.EndProviderSearch();
            if (EndApiUnitLookup(lookupCancellation))
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Keeps the player open with provider choices for the requested episode.
    /// <para>
    /// Providers that were found but failed to open are still offered: one of
    /// them refusing a single video endpoint does not mean the rest will, and
    /// choosing explicitly is the only way left to try them.
    /// </para>
    /// </summary>
    private Task ShowEpisodeProvidersInPlayerAsync(
        ApiContentUnitLookupSelection selection,
        IReadOnlyList<ApiContentUnitVideoCandidate> candidates)
    {
        if (candidates.Count > 0)
        {
            ShowApiVideoProviderChoices(selection, candidates);
            SetError(PlaybackFeedback, UiTextCatalog.Format(
                "AdjacentEpisodeNeedsProvider",
                selection.Title));
            return Task.CompletedTask;
        }

        SetError(PlaybackFeedback, UiTextCatalog.Format(
            "AdjacentEpisodeNotFound",
            selection.Title));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records where the playing episode sits among the title's episodes.
    /// Seasons are flattened in order so stepping forward crosses a season
    /// boundary the same way it crosses an episode one.
    /// </summary>
    private void SetPlayerEpisodeNavigation(
        CatalogContentIdentity contentIdentity,
        CatalogItemDetails? details,
        string? preferredApiProviderId = null)
    {
        _playerEpisodeNavigation = null;
        if (details is not null &&
            contentIdentity.UnitKind is ContentUnitKind.Episode)
        {
            var episodes = details.Units
                .OrderBy(static unit => unit.SortOrder)
                .Concat(details.Seasons
                    .OrderBy(static season => season.SortOrder)
                    .ThenBy(static season => season.Number)
                    .SelectMany(season => season.Units
                        .OrderBy(static unit => unit.SortOrder)))
                .Where(static unit => unit.Kind is ContentUnitKind.Episode)
                .ToArray();
            var currentIndex = Array.FindIndex(
                episodes,
                unit => string.Equals(
                    unit.Identity.StableKey,
                    contentIdentity.StableKey,
                    StringComparison.Ordinal));
            if (currentIndex >= 0)
            {
                _playerEpisodeNavigation = new ReaderChapterNavigation(
                    details,
                    episodes,
                    currentIndex,
                    preferredApiProviderId);
            }
        }

        NotifyPlayerEpisodeNavigationChanged();
    }

    private void NotifyPlayerEpisodeNavigationChanged()
    {
        OnPropertyChanged(nameof(HasPlayerEpisodeNavigation));
        OnPropertyChanged(nameof(CanOpenPreviousEpisode));
        OnPropertyChanged(nameof(CanOpenNextEpisode));
    }

    private void SetReaderChapterNavigation(
        CatalogContentIdentity contentIdentity,
        CatalogItemDetails? details,
        string? preferredApiProviderId = null)
    {
        _readerChapterNavigation = null;
        if (details is null ||
            contentIdentity.UnitKind is not ContentUnitKind.Chapter)
        {
            NotifyReaderChapterNavigationChanged();
            return;
        }

        var chapters = details.Units
            .OrderBy(static unit => unit.SortOrder)
            .Concat(details.Seasons
                .OrderBy(static season => season.SortOrder)
                .ThenBy(static season => season.Number)
                .SelectMany(season => season.Units
                    .OrderBy(static unit => unit.SortOrder)))
            .Where(static unit =>
                unit.Kind is ContentUnitKind.Chapter)
            .ToArray();
        var currentIndex = Array.FindIndex(
            chapters,
            unit => string.Equals(
                unit.Identity.StableKey,
                contentIdentity.StableKey,
                StringComparison.Ordinal));
        if (currentIndex >= 0)
        {
            _readerChapterNavigation = new ReaderChapterNavigation(
                details,
                chapters,
                currentIndex,
                preferredApiProviderId);
        }

        NotifyReaderChapterNavigationChanged();
    }

    private void NotifyReaderChapterNavigationChanged()
    {
        OnPropertyChanged(nameof(ReaderChapterLabel));
        OnPropertyChanged(nameof(HasReaderChapterNavigation));
        OnPropertyChanged(nameof(CanOpenPreviousReaderChapter));
        OnPropertyChanged(nameof(CanOpenNextReaderChapter));
    }

    private void NotifySectionNavigationChanged()
    {
        OnPropertyChanged(nameof(IsAllSection));
        OnPropertyChanged(nameof(ShowHomeSections));
        OnPropertyChanged(nameof(ShowBrowseShelves));
        OnPropertyChanged(nameof(ShowCatalogViewToggle));
        OnPropertyChanged(nameof(ShowFlatCatalogList));
        // Derived from ShowFlatCatalogList, so leaving the catalog for
        // settings or downloads has to retract the pager with it.
        OnPropertyChanged(nameof(ShowCatalogPagination));
        OnPropertyChanged(nameof(IsMoviesSection));
        OnPropertyChanged(nameof(IsAnimeSection));
        OnPropertyChanged(nameof(IsBooksSection));
        OnPropertyChanged(nameof(IsBrowseSection));
        OnPropertyChanged(nameof(IsLiveTvSection));
        OnPropertyChanged(nameof(IsUserListsSection));
        OnPropertyChanged(nameof(IsDownloadsSection));
        OnPropertyChanged(nameof(ShowCatalogOverviewTools));
    }

    private static string BuildUnitTitle(
        CatalogContentUnitDetails unit)
    {
        var unitName = UiTextCatalog.Get(unit.Kind switch
        {
            ContentUnitKind.Episode => "UnitEpisode",
            ContentUnitKind.Chapter => "UnitChapter",
            ContentUnitKind.Track => "UnitTrack",
            ContentUnitKind.AudioChapter => "UnitAudioChapter",
            _ => "CatalogItem"
        });
        var title = $"{unitName} {unit.DisplayNumber}";
        return string.IsNullOrWhiteSpace(unit.Name)
            ? title
            : $"{title} — {unit.Name}";
    }

    private void CloseReaders()
    {
        var wasReading = Reader is not null || EpubReader is not null;
        Reader?.Dispose();
        ReaderFeedback.Message = null;
        Reader = null;
        EpubReader?.Dispose();
        EpubReader = null;
        _readerChapterNavigation = null;
        NotifyReaderChapterNavigationChanged();
        if (wasReading)
        {
            FlushProgressSync();
        }
    }

    private static string FormatPosition(double totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{Math.Max(0, duration.Seconds)}s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:0.#} {suffixes[suffix]}";
    }

    private static string GetDefaultMetadataLanguage()
    {
        var culture = UiTextCatalog.CurrentCulture;
        return string.IsNullOrWhiteSpace(culture.Name)
            ? "en-US"
            : culture.Name;
    }

    private static string LocalizePlatformName(RuntimeProfile profile)
    {
        if (profile.IsTelevision)
        {
            return UiTextCatalog.Get("PlatformAndroidTv");
        }

        return profile.PlatformName switch
        {
            "Native desktop" => UiTextCatalog.Get("PlatformNativeDesktop"),
            "Browser / WebAssembly" => UiTextCatalog.Get("PlatformBrowser"),
            "Android" => UiTextCatalog.Get("PlatformAndroid"),
            "iOS / iPadOS" => UiTextCatalog.Get("PlatformIos"),
            var value => value
        };
    }

    private sealed record ReaderChapterNavigation(
        CatalogItemDetails Details,
        IReadOnlyList<CatalogContentUnitDetails> Chapters,
        int CurrentIndex,
        string? PreferredApiProviderId);

    private readonly record struct CatalogCardReuseKey(
        string Scope,
        string Identity);

    private sealed class CachedCatalogCard(
        string signature,
        CatalogCardViewModel viewModel,
        long lastUse)
    {
        public string Signature { get; } = signature;

        public CatalogCardViewModel ViewModel { get; } = viewModel;

        public long LastUse { get; set; } = lastUse;
    }
}
