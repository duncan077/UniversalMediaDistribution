using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed record CatalogMediaSourceSelection(
    CatalogContentIdentity ContentIdentity,
    CatalogContentIdentity CatalogItemIdentity,
    string Title,
    MediaType MediaType,
    CatalogMediaSourceDetails Source,
    IReadOnlyList<CatalogMediaSourceDetails> RelatedSources);

public sealed record ApiContentImageSelection(
    CatalogContentIdentity ContentIdentity,
    CatalogContentIdentity CatalogItemIdentity,
    string Title,
    ApiContentImageSet ImageSet,
    string? ProviderId = null);

public sealed record ApiContentVideoSelection(
    CatalogContentIdentity ContentIdentity,
    CatalogContentIdentity CatalogItemIdentity,
    string Title,
    ApiContentVideoSet VideoSet,
    string? ProviderId = null);

public sealed record ApiContentUnitLookupSelection(
    CatalogItemDetails Details,
    CatalogContentUnitDetails Unit,
    string Title);

public sealed class CatalogAlternativeNameViewModel(CatalogAlternativeName alternativeName)
{
    public string Name { get; } = alternativeName.Name;

    public string Language { get; } =
        UiTextCatalog.LanguageName(alternativeName.Language);

    public string Kind { get; } =
        UiTextCatalog.AlternativeNameKind(alternativeName.Kind);
}

public sealed class CatalogContributorViewModel(CatalogContributor contributor)
{
    public string Name { get; } = contributor.Name;

    public string Kind { get; } = contributor.Kind switch
    {
        "organization" => UiTextCatalog.Get("ContributorOrganization"),
        _ => UiTextCatalog.Get("ContributorPerson")
    };

    public string CreditText { get; } = string.Join(
        " · ",
        contributor.Credits
            .Select(static credit => string.IsNullOrWhiteSpace(credit.CharacterName)
                ? credit.Role
                : $"{credit.Role} ({credit.CharacterName})")
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public string Url { get; } = contributor.Url?.AbsoluteUri ?? string.Empty;

    public bool HasCredits => CreditText.Length > 0;

    public bool HasUrl => Url.Length > 0;
}

public sealed class CatalogMediaSourceViewModel
{
    public CatalogMediaSourceViewModel(
        CatalogMediaSourceSelection selection,
        Func<CatalogMediaSourceSelection, Task> openAsync)
    {
        Selection = selection;
        OpenCommand = new AsyncRelayCommand(() => openAsync(selection));
    }

    public CatalogMediaSourceSelection Selection { get; }

    public IAsyncRelayCommand OpenCommand { get; }

    public string ActionText
    {
        get
        {
            if (Selection.Source.Layout is CatalogMediaSourceLayout.ImageSequence)
            {
                return UiTextCatalog.Get("Read");
            }

            if (IsSubtitle(Selection.Source.AssetRole))
            {
                return UiTextCatalog.Get("Subtitle");
            }

            if (Selection.MediaType is MediaType.Book)
            {
                return UiTextCatalog.Get("Read");
            }

            var language = EffectiveLanguage(Selection.Source);
            return IsPrimary(Selection.Source.AssetRole) &&
                   !string.Equals(
                       language,
                       LanguageTag.Undetermined,
                       StringComparison.OrdinalIgnoreCase)
                ? UiTextCatalog.Format(
                    "PlayLanguage",
                    UiTextCatalog.LanguageName(language))
                : UiTextCatalog.Get("Play");
        }
    }

    public bool CanOpen => !IsSubtitle(Selection.Source.AssetRole);

    public string Summary
    {
        get
        {
            var source = Selection.Source;
            var parts = new List<string> { FormatAssetRole(source.AssetRole) };
            Add(parts, source.VideoQuality);
            parts.Add(UiTextCatalog.LanguageName(EffectiveLanguage(source)));
            Add(parts, source.Container?.ToUpperInvariant());
            if (source.SizeBytes is > 0)
            {
                parts.Add(FormatBytes(source.SizeBytes.Value));
            }

            return string.Join(" · ", parts);
        }
    }

    public string SourceKind => Selection.Source.Layout is CatalogMediaSourceLayout.ImageSequence
        ? UiTextCatalog.Get("SourceImageSequence")
        : IsSingleBookImage(Selection)
            ? UiTextCatalog.Get("SourceSingleImage")
        : string.Equals(Selection.Source.Container, "CBZ", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(Path.GetExtension(Selection.Source.FilePath), ".cbz", StringComparison.OrdinalIgnoreCase)
            ? UiTextCatalog.Get("SourceCbz")
        : string.Equals(Selection.Source.Container, "EPUB", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(Path.GetExtension(Selection.Source.FilePath), ".epub", StringComparison.OrdinalIgnoreCase)
            ? UiTextCatalog.Get("SourceEpub")
        : IsPdf(Selection)
            ? UiTextCatalog.Get("SourcePdf")
        : Selection.Source.FilePath ?? UiTextCatalog.Get("SourceTorrentFile");

    private static void Add(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }

    private static string EffectiveLanguage(CatalogMediaSourceDetails source) =>
        !string.IsNullOrWhiteSpace(source.Language)
            ? LanguageTag.Normalize(source.Language)
            : source.Torrent.Language;

    private static string FormatAssetRole(string value) =>
        UiTextCatalog.AssetRoleName(value);

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:0.#} {suffixes[suffix]}";
    }

    internal static bool IsSubtitle(string role) =>
        role.Contains("subtitle", StringComparison.OrdinalIgnoreCase) ||
        role.Contains("caption", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPrimary(string role) =>
        string.Equals(role.Trim(), "primary", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSingleBookImage(CatalogMediaSourceSelection selection)
    {
        var extension = Path.GetExtension(selection.Source.FilePath);
        return selection.MediaType is MediaType.Book &&
               selection.Source.Layout is CatalogMediaSourceLayout.SingleFile &&
               (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase) ||
                selection.Source.MimeType?.Equals(
                    "image/jpeg",
                    StringComparison.OrdinalIgnoreCase) is true ||
                selection.Source.MimeType?.Equals(
                    "image/png",
                    StringComparison.OrdinalIgnoreCase) is true ||
                selection.Source.MimeType?.Equals(
                    "image/webp",
                    StringComparison.OrdinalIgnoreCase) is true);
    }

    internal static bool IsPdf(CatalogMediaSourceSelection selection) =>
        selection.MediaType is MediaType.Book &&
        selection.Source.Layout is CatalogMediaSourceLayout.SingleFile &&
        (string.Equals(selection.Source.Container, "PDF", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(selection.Source.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(
             Path.GetExtension(selection.Source.FilePath),
             ".pdf",
             StringComparison.OrdinalIgnoreCase));
}

public sealed class CatalogContentEntryViewModel : ViewModelBase
{
    public FeedbackViewModel Feedback { get; } = new();

    private readonly Func<
        CatalogContentIdentity,
        bool,
        Task<bool>>? _setSeenAsync;
    private readonly ObservableCollection<ApiContentProviderOptionViewModel>
        _providerChoices = [];
    private bool _isSeen;
    private string _providerChoiceMessage = string.Empty;
    private IRelayCommand? _cancelProviderChoiceCommand;
    private bool _isProviderSearchRunning;

    private bool _isDeleteConfirmationVisible;
    private bool _isDeleting;
    public bool IsDeleteConfirmationVisible
    {
        get => _isDeleteConfirmationVisible;
        private set => SetProperty(ref _isDeleteConfirmationVisible, value);
    }
    public bool IsDeleting
    {
        get => _isDeleting;
        private set => SetProperty(ref _isDeleting, value);
    }
    public bool CanDelete => RequestDeleteCommand is not null;
    public string DeleteActionText => UiTextCatalog.Get(
        Identity.UnitKind == ContentUnitKind.Episode ? "DeleteEpisode" : "DeleteChapter");
    public string DeleteConfirmationText => UiTextCatalog.Format("ConfirmDeleteEntry", Heading);
    public IRelayCommand? RequestDeleteCommand { get; private set; }
    public IRelayCommand? CancelDeleteCommand { get; private set; }
    public IAsyncRelayCommand? ConfirmDeleteCommand { get; private set; }

    internal void ConfigureDeletion(Func<CatalogContentIdentity, Task<bool>> deleteAsync)
    {
        if (Identity.UnitKind is not (ContentUnitKind.Chapter or ContentUnitKind.Episode)) return;
        RequestDeleteCommand = new RelayCommand(() => IsDeleteConfirmationVisible = true);
        CancelDeleteCommand = new RelayCommand(() => IsDeleteConfirmationVisible = false);
        ConfirmDeleteCommand = new AsyncRelayCommand(async () =>
        {
            if (!IsDeleteConfirmationVisible) return;
            IsDeleting = true;
            try
            {
                if (await deleteAsync(Identity)) IsDeleteConfirmationVisible = false;
            }
            finally { IsDeleting = false; }
        });
    }

    public CatalogContentEntryViewModel(
        string heading,
        string? subheading,
        string? thumbnailRef,
        CatalogContentIdentity identity,
        CatalogContentIdentity itemIdentity,
        MediaType mediaType,
        IReadOnlyList<CatalogMediaSourceDetails> sources,
        TorrentMediaSessionService mediaSessionService,
        Func<CatalogMediaSourceSelection, Task> openAsync,
        IReadOnlyList<Uri>? trustedRemoteOrigins = null,
        ApiContentImageSet? apiImageSet = null,
        Func<ApiContentImageSelection, Task>? openApiImagesAsync = null,
        Func<Task>? findApiImagesAsync = null,
        ApiContentVideoSet? apiVideoSet = null,
        Func<ApiContentVideoSelection, Task>? openApiVideosAsync = null,
        Func<Task>? findApiVideosAsync = null,
        bool isSeen = false,
        Func<CatalogContentIdentity, bool, Task<bool>>?
            setSeenAsync = null,
        Uri? remoteArtworkReferrer = null,
        string? apiContentProviderId = null,
        string? seasonLabel = null)
    {
        SeasonLabel = seasonLabel ?? string.Empty;
        Identity = identity;
        _isSeen = isSeen;
        _setSeenAsync = identity.IsUnit
            ? setSeenAsync
            : null;
        Heading = heading;
        Subheading = subheading ?? string.Empty;
        ThumbnailRef = thumbnailRef;
        ApiImagePageCount = apiImageSet?.Pages.Count ?? 0;
        ApiVideoServerCount = apiVideoSet?.Sources.Count ?? 0;
        Sources = sources.Select(source => new CatalogMediaSourceViewModel(
            new CatalogMediaSourceSelection(
                identity,
                itemIdentity,
                heading,
                mediaType,
                source,
                sources),
            openAsync)).ToArray();
        if (apiImageSet is not null && openApiImagesAsync is not null)
        {
            OpenApiImagesCommand = new AsyncRelayCommand(() =>
                openApiImagesAsync(new ApiContentImageSelection(
                    identity,
                    itemIdentity,
                    heading,
                    apiImageSet,
                    apiContentProviderId)));
        }

        if (findApiImagesAsync is not null)
        {
            FindApiImagesCommand = new AsyncRelayCommand(findApiImagesAsync);
        }

        if (apiVideoSet is not null && openApiVideosAsync is not null)
        {
            OpenApiVideosCommand = new AsyncRelayCommand(() =>
                openApiVideosAsync(new ApiContentVideoSelection(
                    identity,
                    itemIdentity,
                    heading,
                    apiVideoSet,
                    apiContentProviderId)));
        }

        if (findApiVideosAsync is not null)
        {
            FindApiVideosCommand = new AsyncRelayCommand(findApiVideosAsync);
        }

        if (_setSeenAsync is not null)
        {
            ToggleSeenCommand =
                new AsyncRelayCommand(ToggleSeenAsync);
        }

        if (!string.IsNullOrWhiteSpace(thumbnailRef) &&
            (sources.Count > 0 ||
             CatalogArtworkViewModel.IsSupportedRemoteReference(
                 thumbnailRef,
                 trustedRemoteOrigins)))
        {
            // An entry row draws an 84 px square, so it decodes a thumbnail
            // rather than a poster.
            Artwork = new CatalogArtworkViewModel(
                thumbnailRef,
                sources.FirstOrDefault(),
                mediaSessionService,
                trustedRemoteOrigins,
                CatalogArtworkViewModel.ThumbnailDecodeWidth,
                remoteArtworkReferrer);
        }

        var preferredSource = Sources.FirstOrDefault(
            static source => source.CanOpen);
        if (preferredSource is not null)
        {
            OpenPreferredCommand = new AsyncRelayCommand(
                () => preferredSource.OpenCommand.ExecuteAsync(null));
        }
        else if (OpenApiImagesCommand is not null)
        {
            OpenPreferredCommand = new AsyncRelayCommand(
                () => OpenApiImagesCommand.ExecuteAsync(null));
        }
        else if (OpenApiVideosCommand is not null)
        {
            OpenPreferredCommand = new AsyncRelayCommand(
                () => OpenApiVideosCommand.ExecuteAsync(null));
        }
        else if (FindApiImagesCommand is not null)
        {
            OpenPreferredCommand = new AsyncRelayCommand(
                () => FindApiImagesCommand.ExecuteAsync(null));
        }
        else if (FindApiVideosCommand is not null)
        {
            OpenPreferredCommand = new AsyncRelayCommand(
                () => FindApiVideosCommand.ExecuteAsync(null));
        }
    }

    public string Heading { get; }

    public CatalogContentIdentity Identity { get; }

    public string Subheading { get; }

    /// <summary>
    /// Which season this belongs to, empty for a title that has none.
    /// </summary>
    public string SeasonLabel { get; }

    /// <summary>
    /// Set on the first row of each season so the list draws a divider there.
    /// <para>
    /// A season used to be one fragment of grey subheading, which left
    /// "Episode 1" of one season looking exactly like "Episode 1" of the next.
    /// It is a property of position rather than of the entry, so it is assigned
    /// after the rows are ordered and again whenever that order is flipped.
    /// </para>
    /// </summary>
    public string? SeasonHeader => _seasonHeader;

    public bool ShowsSeasonHeader => !string.IsNullOrEmpty(_seasonHeader);

    private string? _seasonHeader;

    internal void SetSeasonHeader(string? header)
    {
        if (string.Equals(_seasonHeader, header, StringComparison.Ordinal))
        {
            return;
        }

        _seasonHeader = header;
        OnPropertyChanged(nameof(SeasonHeader));
        OnPropertyChanged(nameof(ShowsSeasonHeader));
    }

    public string? ThumbnailRef { get; }

    public IReadOnlyList<CatalogMediaSourceViewModel> Sources { get; }

    public CatalogArtworkViewModel? Artwork { get; }

    public IAsyncRelayCommand? OpenApiImagesCommand { get; }

    public IAsyncRelayCommand? FindApiImagesCommand { get; }

    public IAsyncRelayCommand? OpenApiVideosCommand { get; }

    public IAsyncRelayCommand? FindApiVideosCommand { get; }

    public IAsyncRelayCommand? ToggleSeenCommand { get; }

    public IAsyncRelayCommand? OpenPreferredCommand { get; }

    public bool HasSources => Sources.Count > 0;

    public bool HasApiImages => OpenApiImagesCommand is not null;

    public bool CanFindApiImages => FindApiImagesCommand is not null;

    public bool HasApiVideos => OpenApiVideosCommand is not null;

    public bool CanFindApiVideos => FindApiVideosCommand is not null;

    public bool CanToggleSeen => ToggleSeenCommand is not null;

    public bool CanOpenPreferred => OpenPreferredCommand is not null;

    public bool IsSeen => _isSeen;

    public string SeenGlyph => IsSeen ? "\u2713" : "\u25cb";

    public string SeenActionText => UiTextCatalog.Get(
        IsSeen
            ? "MarkUnitUnseen"
            : "MarkUnitSeen");

    public bool HasAnySources =>
        HasSources ||
        HasApiImages ||
        CanFindApiImages ||
        HasApiVideos ||
        CanFindApiVideos;

    public int ApiImagePageCount { get; }

    public int ApiVideoServerCount { get; }

    public string ApiImageActionText => UiTextCatalog.Format(
        "ReadOnlinePages",
        ApiImagePageCount);

    public string FindApiImagesActionText =>
        UiTextCatalog.Get("FindChapterOnline");

    public string ApiVideoActionText => UiTextCatalog.Format(
        "WatchOnlineServers",
        ApiVideoServerCount);

    public string FindApiVideosActionText =>
        UiTextCatalog.Get("FindEpisodeOnline");

    public bool HasArtwork => Artwork is not null;

    /// <summary>
    /// Providers that can serve this chapter or episode. Shown on the row the
    /// user pressed rather than at the top of the page, so choosing a source
    /// never means scrolling away from the entry.
    /// </summary>
    public IReadOnlyList<ApiContentProviderOptionViewModel> ProviderChoices =>
        _providerChoices;

    public bool HasProviderChoices => _providerChoices.Count > 0;

    /// <summary>
    /// Whether the lookup that fills <see cref="ProviderChoices"/> is still
    /// running.
    /// <para>
    /// Providers answer one at a time and slow ones take seconds, so without
    /// this the row sat inert until the first result arrived and the search
    /// read as a button that did nothing.
    /// </para>
    /// </summary>
    public bool IsProviderSearchRunning => _isProviderSearchRunning;

    /// <summary>
    /// Whether the in-row picker should be shown at all: while the search runs
    /// so its progress is visible, and afterwards while there are results.
    /// </summary>
    public bool IsProviderPickerVisible =>
        _isProviderSearchRunning || _providerChoices.Count > 0;

    public string ProviderChoiceMessage => _providerChoiceMessage;

    public IRelayCommand? CancelProviderChoiceCommand =>
        _cancelProviderChoiceCommand;

    internal void BeginProviderSearch(string message, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        _providerChoices.Clear();
        _isProviderSearchRunning = true;
        _providerChoiceMessage = message ?? string.Empty;
        _cancelProviderChoiceCommand = new RelayCommand(cancel);
        OnPropertyChanged(nameof(ProviderChoiceMessage));
        OnPropertyChanged(nameof(CancelProviderChoiceCommand));
        OnPropertyChanged(nameof(HasProviderChoices));
        OnPropertyChanged(nameof(IsProviderSearchRunning));
        OnPropertyChanged(nameof(IsProviderPickerVisible));
    }

    internal void EndProviderSearch()
    {
        if (!_isProviderSearchRunning)
        {
            return;
        }

        _isProviderSearchRunning = false;
        OnPropertyChanged(nameof(IsProviderSearchRunning));
        OnPropertyChanged(nameof(IsProviderPickerVisible));
    }

    internal void ShowProviderChoices(
        string message,
        IReadOnlyList<ApiContentProviderOptionViewModel> options,
        Action cancel)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cancel);
        _providerChoices.Clear();
        foreach (var option in options)
        {
            _providerChoices.Add(option);
        }

        _providerChoiceMessage = message ?? string.Empty;
        _cancelProviderChoiceCommand = new RelayCommand(cancel);
        OnPropertyChanged(nameof(ProviderChoiceMessage));
        OnPropertyChanged(nameof(CancelProviderChoiceCommand));
        OnPropertyChanged(nameof(HasProviderChoices));
        OnPropertyChanged(nameof(IsProviderPickerVisible));
    }

    internal void ClearProviderChoices()
    {
        if (_providerChoices.Count == 0 && !_isProviderSearchRunning)
        {
            return;
        }

        _providerChoices.Clear();
        _providerChoiceMessage = string.Empty;
        _cancelProviderChoiceCommand = null;
        _isProviderSearchRunning = false;
        OnPropertyChanged(nameof(ProviderChoiceMessage));
        OnPropertyChanged(nameof(CancelProviderChoiceCommand));
        OnPropertyChanged(nameof(HasProviderChoices));
        OnPropertyChanged(nameof(IsProviderSearchRunning));
        OnPropertyChanged(nameof(IsProviderPickerVisible));
    }

    internal event EventHandler? SeenChanged;

    internal void SetSeen(bool value)
    {
        if (!SetProperty(ref _isSeen, value, nameof(IsSeen)))
        {
            return;
        }

        OnPropertyChanged(nameof(SeenGlyph));
        OnPropertyChanged(nameof(SeenActionText));
        SeenChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ToggleSeenAsync()
    {
        var target = !IsSeen;
        if (_setSeenAsync is not null &&
            await _setSeenAsync(Identity, target))
        {
            SetSeen(target);
        }
    }
}

public sealed partial class CatalogDetailsViewModel : ViewModelBase
{
    private readonly IReadOnlyList<CatalogContentEntryViewModel>
        _trackableEntries;
    private readonly Func<
        IReadOnlyList<CatalogContentIdentity>,
        bool,
        Task<bool>>? _setAllSeenAsync;

    public CatalogDetailsViewModel(
        CatalogItemDetails details,
        TorrentMediaSessionService mediaSessionService,
        Func<CatalogMediaSourceSelection, Task> openAsync,
        string? metadataProviderName = null,
        string? contentSourceName = null,
        IReadOnlyList<Uri>? trustedRemoteOrigins = null,
        IReadOnlyList<ApiContentImageSet>? apiImageSets = null,
        Func<ApiContentImageSelection, Task>? openApiImagesAsync = null,
        bool canResolveApiImages = false,
        Func<ApiContentUnitLookupSelection, Task>? resolveApiImagesAsync = null,
        IReadOnlyList<ApiContentVideoSet>? apiVideoSets = null,
        Func<ApiContentVideoSelection, Task>? openApiVideosAsync = null,
        bool canResolveApiVideos = false,
        Func<ApiContentUnitLookupSelection, Task>? resolveApiVideosAsync = null,
        IReadOnlySet<string>? completedContentKeys = null,
        Func<CatalogContentIdentity, bool, Task<bool>>?
            setSeenAsync = null,
        Func<
            IReadOnlyList<CatalogContentIdentity>,
            bool,
            Task<bool>>? setAllSeenAsync = null,
        Uri? remoteArtworkReferrer = null,
        string? contentSourceProviderId = null,
        Func<CatalogContentIdentity, Task<bool>>? deleteEntryAsync = null)
    {
        Details = details;
        MetadataProviderName = metadataProviderName ?? string.Empty;
        ContentSourceName = contentSourceName ?? string.Empty;
        ContentSourceProviderId = string.IsNullOrWhiteSpace(
            contentSourceProviderId)
                ? null
                : contentSourceProviderId.Trim();
        TrustedRemoteOrigins = trustedRemoteOrigins?.ToArray() ?? [];
        ArtworkReferrer = remoteArtworkReferrer;
        ApiImageSets = apiImageSets?.ToArray() ?? [];
        ApiVideoSets = apiVideoSets?.ToArray() ?? [];
        completedContentKeys ??=
            new HashSet<string>(StringComparer.Ordinal);
        _setAllSeenAsync = setAllSeenAsync;
        Name = CatalogDisplayNameSelector.Select(
            details.Item.Name,
            details.Item.AlternativeNames);
        Subtype = UiTextCatalog.SubtypeName(details.Item.SubtypeCode);
        StatusText = UiTextCatalog.PublicationStatusName(
            details.Item.PublicationStatus);
        GenreText = details.Item.Genres.Count == 0
            ? UiTextCatalog.Get("Uncategorized")
            : string.Join(" · ", details.Item.Genres);
        CategoryText = string.Join(" · ", details.Item.Categories);
        AlternativeNames = BuildAlternativeNames(details, Name);
        Contributors = details.Item.Contributors
            .Where(static contributor => !CatalogMetadataSource.IsSyntheticDistributor(contributor))
            .OrderBy(
                static contributor => contributor.Name,
                StringComparer.Create(
                    UiTextCatalog.CurrentCulture,
                    ignoreCase: true))
            .Select(static contributor => new CatalogContributorViewModel(contributor))
            .ToArray();
        Metadata = BuildMetadata(details);
        var artworkContext = details.Sources
            .Concat(details.Units.SelectMany(static unit => unit.Sources))
            .Concat(details.Seasons.SelectMany(static season =>
                season.Units.SelectMany(static unit => unit.Sources)))
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(details.Item.ThumbnailRef) &&
            (artworkContext is not null ||
             CatalogArtworkViewModel.IsSupportedRemoteReference(
                 details.Item.ThumbnailRef,
                 trustedRemoteOrigins)))
        {
            Artwork = new CatalogArtworkViewModel(
                details.Item.ThumbnailRef,
                artworkContext,
                mediaSessionService,
                trustedRemoteOrigins,
                remoteReferrer: remoteArtworkReferrer);
        }

        // Wide art for the header. Deliberately falls back to nothing rather
        // than to the poster: stretching 2:3 across a 16:9 header looks worse
        // than a plain surface, and its own view model instance because
        // releasing one artwork view nulls the image the other is showing.
        if (!string.IsNullOrWhiteSpace(details.Item.BackdropRef) &&
            (artworkContext is not null ||
             CatalogArtworkViewModel.IsSupportedRemoteReference(
                 details.Item.BackdropRef,
                 trustedRemoteOrigins)))
        {
            Backdrop = new CatalogArtworkViewModel(
                details.Item.BackdropRef,
                artworkContext,
                mediaSessionService,
                trustedRemoteOrigins,
                CatalogArtworkViewModel.BackdropDecodeWidth,
                remoteArtworkReferrer);
        }

        var imageSets = ApiImageSets;
        var videoSets = ApiVideoSets;
        var itemImageSet = imageSets.FirstOrDefault(
            static imageSet => imageSet.IsItemLevel);
        var entries = new List<CatalogContentEntryViewModel>();
        if (details.Sources.Count > 0 || itemImageSet is not null)
        {
            entries.Add(new CatalogContentEntryViewModel(
                details.Item.MediaType is MediaType.Audio
                    ? UiTextCatalog.Get("FullRecording")
                    : UiTextCatalog.Get("MainRelease"),
                BuildItemSourceSubheading(details),
                details.Item.ThumbnailRef,
                details.Identity,
                details.Identity,
                details.Item.MediaType,
                details.Sources,
                mediaSessionService,
                openAsync,
                trustedRemoteOrigins,
                itemImageSet,
                openApiImagesAsync,
                remoteArtworkReferrer: remoteArtworkReferrer,
                apiContentProviderId: ContentSourceProviderId));
        }

        entries.AddRange(details.Units.Select(unit => CreateUnitEntry(
            unit,
            seasonLabel: null,
            details,
            mediaSessionService,
            openAsync,
            trustedRemoteOrigins,
            remoteArtworkReferrer,
            imageSets,
            openApiImagesAsync,
            canResolveApiImages,
            resolveApiImagesAsync,
            videoSets,
            openApiVideosAsync,
            canResolveApiVideos,
            resolveApiVideosAsync,
            completedContentKeys,
            setSeenAsync,
            ContentSourceProviderId)));
        foreach (var season in details.Seasons)
        {
            var seasonLabel = season.DisplayName ??
                UiTextCatalog.Format("SeasonNumber", season.Number);
            entries.AddRange(season.Units.Select(unit => CreateUnitEntry(
                unit,
                seasonLabel,
                details,
                mediaSessionService,
                openAsync,
                trustedRemoteOrigins,
                remoteArtworkReferrer,
                imageSets,
                openApiImagesAsync,
                canResolveApiImages,
                resolveApiImagesAsync,
                videoSets,
                openApiVideosAsync,
                canResolveApiVideos,
                resolveApiVideosAsync,
                completedContentKeys,
                setSeenAsync,
                ContentSourceProviderId)));
        }

        if (deleteEntryAsync is not null)
            foreach (var entry in entries) entry.ConfigureDeletion(deleteEntryAsync);

        _orderedEntries = entries;
        ApplySeasonHeaders(entries);
        Entries = new ObservableCollection<CatalogContentEntryViewModel>(
            entries);
        BuildSeasonOptions();
        _trackableEntries = entries
            .Where(static entry => entry.CanToggleSeen)
            .ToArray();
        foreach (var entry in _trackableEntries)
        {
            entry.SeenChanged += OnEntrySeenChanged;
        }

        ContinueCommand = new AsyncRelayCommand(
            ContinueAsync,
            () => CanContinue);
        if (_trackableEntries.Count > 0 &&
            _setAllSeenAsync is not null)
        {
            ToggleAllSeenCommand =
                new AsyncRelayCommand(ToggleAllSeenAsync);
        }
    }

    public CatalogItemDetails Details { get; }

    public string Name { get; }

    public string Description => Details.Item.Description;

    public string MetadataProviderName { get; }

    public string ContentSourceName { get; }

    public string? ContentSourceProviderId { get; }

    public IReadOnlyList<Uri> TrustedRemoteOrigins { get; }

    public Uri? ArtworkReferrer { get; }

    public IReadOnlyList<ApiContentImageSet> ApiImageSets { get; }

    public IReadOnlyList<ApiContentVideoSet> ApiVideoSets { get; }

    public string OriginBadge => ContentSourceName.Length == 0
        ? UiTextCatalog.Get("LocalResult")
        : UiTextCatalog.Format("OnlineResult", ContentSourceName);

    public bool CanRefreshMetadata => MetadataProviderName.Length > 0;

    public string MetadataRefreshButtonText =>
        UiTextCatalog.Format("UpdateFromProvider", MetadataProviderName);

    public string Subtype { get; }

    public string StatusText { get; }

    public bool HasStatus =>
        Details.Item.PublicationStatus is not PublicationStatus.Unknown &&
        PublicationStatusApplicability.AppliesTo(
            Details.Item.MediaType,
            Details.Item.SubtypeCode);

    public string GenreText { get; }

    public string CategoryText { get; }

    public bool HasCategories => CategoryText.Length > 0;

    public IReadOnlyList<CatalogAlternativeNameViewModel> AlternativeNames { get; }

    public bool HasAlternativeNames => AlternativeNames.Count > 0;

    public IReadOnlyList<CatalogContributorViewModel> Contributors { get; }

    public bool HasContributors => Contributors.Count > 0;

    public string Metadata { get; }

    public CatalogArtworkViewModel? Artwork { get; }

    public bool HasArtwork => Artwork is not null;

    public CatalogArtworkViewModel? Backdrop { get; }

    public bool HasBackdrop => Backdrop is not null;

    /// <summary>
    /// The rows as shown. The natural order is ascending; flipping it reverses
    /// this collection without disturbing seen state or numbering.
    /// </summary>
    public ObservableCollection<CatalogContentEntryViewModel> Entries { get; }

    private readonly IReadOnlyList<CatalogContentEntryViewModel>
        _orderedEntries;

    private bool _isDescending;

    public bool IsEntryOrderDescending => _isDescending;

    public string EntryOrderActionText =>
        UiTextCatalog.Get(
            _isDescending ? "SortOldestFirst" : "SortNewestFirst");

    /// <summary>
    /// The seasons this title has, plus the choice that leaves them all on
    /// screen. Empty for anything without seasons.
    /// </summary>
    public ObservableCollection<SeasonOptionViewModel> SeasonOptions { get; } =
        [];

    public bool HasSeasonSelector => SeasonOptions.Count > 0;

    private SeasonOptionViewModel? _selectedSeason;

    public SeasonOptionViewModel? SelectedSeason
    {
        get => _selectedSeason;
        set
        {
            if (ReferenceEquals(_selectedSeason, value))
            {
                return;
            }

            _selectedSeason = value;
            foreach (var option in SeasonOptions)
            {
                option.IsSelected = ReferenceEquals(option, value);
            }

            OnPropertyChanged();
            RebuildEntries();
        }
    }


    private void BuildSeasonOptions()
    {
        var seasons = _orderedEntries
            .Select(static entry => entry.SeasonLabel)
            .Where(static label => label.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // One season needs no picking: "all seasons" and that season would be
        // the same list twice, and the divider above its rows already names it.
        if (seasons.Length < 2)
        {
            return;
        }

        SeasonOptions.Add(
            new SeasonOptionViewModel(
                UiTextCatalog.Get("AllSeasons"),
                seasonLabel: null,
                option => SelectedSeason = option));
        foreach (var season in seasons)
        {
            SeasonOptions.Add(
                new SeasonOptionViewModel(
                    season,
                    season,
                    option => SelectedSeason = option));
        }

        // Assigned to the backing field: the list is already everything, so
        // going through the setter would only rebuild it into itself.
        _selectedSeason = SeasonOptions[0];
        _selectedSeason.IsSelected = true;
    }

    /// <summary>
    /// Fills the visible list from the ordered rows, narrowed to the chosen
    /// season. One place, because the order and the season each change what the
    /// other produced.
    /// </summary>
    private void RebuildEntries()
    {
        var ordered = _isDescending
            ? _orderedEntries.Reverse()
            : _orderedEntries;
        var season = _selectedSeason?.SeasonLabel;
        Entries.Clear();
        foreach (var entry in ordered)
        {
            if (season is not null &&
                !string.Equals(entry.SeasonLabel, season, StringComparison.Ordinal))
            {
                continue;
            }

            Entries.Add(entry);
        }

        // Both the order and the season change which row opens a season, so
        // the dividers are worked out here rather than travelling with a row.
        ApplySeasonHeaders(Entries);
    }

    /// <summary>
    /// Applies a stored sort direction when the page is built.
    /// </summary>
    internal void ApplyEntryOrder(bool isDescending)
    {
        if (_isDescending == isDescending)
        {
            return;
        }

        _isDescending = isDescending;
        RebuildEntries();
        OnPropertyChanged(nameof(IsEntryOrderDescending));
        OnPropertyChanged(nameof(EntryOrderActionText));
    }

    /// <summary>
    /// Marks the row that opens each season, so the list can show where one
    /// season ends and the next begins instead of running them together.
    /// </summary>
    internal static void ApplySeasonHeaders(
        IReadOnlyList<CatalogContentEntryViewModel> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.SeasonLabel.Length == 0)
            {
                entry.SetSeasonHeader(null);
                continue;
            }

            var previous = index == 0
                ? null
                : entries[index - 1].SeasonLabel;
            entry.SetSeasonHeader(
                string.Equals(previous, entry.SeasonLabel, StringComparison.Ordinal)
                    ? null
                    : entry.SeasonLabel);
        }
    }

    internal Func<bool, Task>? EntryOrderChangedAsync { get; set; }

    [RelayCommand]
    private async Task ToggleEntryOrderAsync()
    {
        ApplyEntryOrder(!_isDescending);
        if (EntryOrderChangedAsync is { } notify)
        {
            await notify(_isDescending);
        }
    }

    public bool HasEntries => Entries.Count > 0;

    public IAsyncRelayCommand ContinueCommand { get; }

    public bool CanContinue => ResolveContinueEntry() is not null;

    public string ContinueActionText
    {
        get
        {
            var entry = ResolveContinueEntry();
            return entry is null
                ? UiTextCatalog.Get("Continue")
                : UiTextCatalog.Format(
                    "ContinueCatalogEntry",
                    entry.Heading);
        }
    }

    public IAsyncRelayCommand? ToggleAllSeenCommand { get; }

    public bool CanToggleAllSeen =>
        ToggleAllSeenCommand is not null;

    public bool AllEntriesSeen =>
        _trackableEntries.Count > 0 &&
        _trackableEntries.All(static entry => entry.IsSeen);

    public string ToggleAllSeenText => UiTextCatalog.Get(
        AllEntriesSeen
            ? "MarkAllUnitsUnseen"
            : "MarkAllUnitsSeen");

    public string EmptyMessage => UiTextCatalog.Get("NoReadableSource");

    /// <summary>
    /// Shows the source picker on the entry the lookup was started from and
    /// clears it everywhere else.
    /// </summary>
    internal void ShowProviderChoices(
        CatalogContentIdentity target,
        string message,
        IReadOnlyList<ApiContentProviderOptionViewModel> options,
        Action cancel)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var entry in Entries)
        {
            if (string.Equals(
                    entry.Identity.StableKey,
                    target.StableKey,
                    StringComparison.Ordinal))
            {
                entry.ShowProviderChoices(message, options, cancel);
            }
            else
            {
                entry.ClearProviderChoices();
            }
        }
    }

    internal void ClearProviderChoices()
    {
        foreach (var entry in Entries)
        {
            entry.ClearProviderChoices();
        }
    }

    /// <summary>
    /// Marks the entry the lookup was started from as searching, so the row
    /// shows progress from the moment it is pressed instead of only once a
    /// provider has answered.
    /// </summary>
    internal void BeginProviderSearch(
        CatalogContentIdentity target,
        string message,
        Action cancel)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var entry in Entries)
        {
            if (string.Equals(
                    entry.Identity.StableKey,
                    target.StableKey,
                    StringComparison.Ordinal))
            {
                entry.BeginProviderSearch(message, cancel);
            }
            else
            {
                entry.ClearProviderChoices();
            }
        }
    }

    internal void EndProviderSearch()
    {
        foreach (var entry in Entries)
        {
            entry.EndProviderSearch();
        }
    }

    /// <summary>
    /// Refreshes every entry after a change that can touch more than the one
    /// the user pressed, such as marking a chapter and everything before it.
    /// </summary>
    internal void ApplySeenState(IReadOnlySet<string> completedContentKeys)
    {
        ArgumentNullException.ThrowIfNull(completedContentKeys);
        foreach (var entry in _trackableEntries)
        {
            entry.SetSeen(
                completedContentKeys.Contains(entry.Identity.StableKey));
        }
    }

    private static IReadOnlyList<CatalogAlternativeNameViewModel> BuildAlternativeNames(
        CatalogItemDetails details,
        string displayName)
    {
        var values = details.Item.AlternativeNames.ToList();
        if (!string.Equals(
                displayName,
                details.Item.Name,
                StringComparison.OrdinalIgnoreCase) &&
            values.All(value =>
                !string.Equals(
                    value.Name,
                    details.Item.Name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(new CatalogAlternativeName(
                details.Item.Name,
                language: null,
                "canonical"));
        }

        if (!string.IsNullOrWhiteSpace(details.OriginalName) &&
            !string.Equals(
                details.OriginalName.Trim(),
                details.Item.Name,
                StringComparison.OrdinalIgnoreCase) &&
            values.All(value =>
                !string.Equals(
                    value.Name,
                    details.OriginalName.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                values.Add(new CatalogAlternativeName(
                    details.OriginalName,
                    details.OriginalLanguage,
                    "original"));
            }
            catch (ArgumentException)
            {
                values.Add(new CatalogAlternativeName(
                    details.OriginalName,
                    language: null,
                    "original"));
            }
        }

        return values
            .Where(value => !string.Equals(
                value.Name,
                displayName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static value => new CatalogAlternativeNameViewModel(value))
            .ToArray();
    }

    private static CatalogContentEntryViewModel CreateUnitEntry(
        CatalogContentUnitDetails unit,
        string? seasonLabel,
        CatalogItemDetails details,
        TorrentMediaSessionService mediaSessionService,
        Func<CatalogMediaSourceSelection, Task> openAsync,
        IReadOnlyList<Uri>? trustedRemoteOrigins,
        Uri? remoteArtworkReferrer,
        IReadOnlyList<ApiContentImageSet> apiImageSets,
        Func<ApiContentImageSelection, Task>? openApiImagesAsync,
        bool canResolveApiImages,
        Func<ApiContentUnitLookupSelection, Task>? resolveApiImagesAsync,
        IReadOnlyList<ApiContentVideoSet> apiVideoSets,
        Func<ApiContentVideoSelection, Task>? openApiVideosAsync,
        bool canResolveApiVideos,
        Func<ApiContentUnitLookupSelection, Task>? resolveApiVideosAsync,
        IReadOnlySet<string> completedContentKeys,
        Func<CatalogContentIdentity, bool, Task<bool>>? setSeenAsync,
        string? apiContentProviderId)
    {
        var prefix = unit.Kind switch
        {
            ContentUnitKind.Episode => UiTextCatalog.Get("UnitEpisode"),
            ContentUnitKind.Chapter => UiTextCatalog.Get("UnitChapter"),
            ContentUnitKind.Track => UiTextCatalog.Get("UnitTrack"),
            ContentUnitKind.AudioChapter => UiTextCatalog.Get("UnitAudioChapter"),
            _ => UiTextCatalog.Get("UnitPart")
        };
        var heading = $"{prefix} {unit.DisplayNumber}";
        if (!string.IsNullOrWhiteSpace(unit.Name))
        {
            heading += " — " + unit.Name;
        }

        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(seasonLabel))
        {
            detailParts.Add(seasonLabel);
        }

        if (unit.DurationSeconds is > 0)
        {
            detailParts.Add(FormatDuration(unit.DurationSeconds.Value));
        }

        if (unit.PageCount is > 0)
        {
            detailParts.Add(UiTextCatalog.Format("PageCount", unit.PageCount));
        }

        var imageSet = apiImageSets.FirstOrDefault(candidate =>
            candidate.Matches(
                unit.Kind,
                unit.DisplayNumber,
                unit.Identity.SeasonNumber));
        Func<Task>? findApiImagesAsync =
            imageSet is null &&
            canResolveApiImages &&
            details.Item.MediaType is MediaType.Book &&
            unit.Kind is ContentUnitKind.Chapter &&
            resolveApiImagesAsync is not null
                ? () => resolveApiImagesAsync(
                    new ApiContentUnitLookupSelection(
                        details,
                        unit,
                        heading))
                : null;
        var videoSet = apiVideoSets.FirstOrDefault(candidate =>
            candidate.Matches(
                unit.Kind,
                unit.DisplayNumber,
                unit.Identity.SeasonNumber));
        Func<Task>? findApiVideosAsync =
            videoSet is null &&
            canResolveApiVideos &&
            details.Item.MediaType is MediaType.Movie &&
            unit.Kind is ContentUnitKind.Episode &&
            resolveApiVideosAsync is not null
                ? () => resolveApiVideosAsync(
                    new ApiContentUnitLookupSelection(
                        details,
                        unit,
                        heading))
                : null;
        return new CatalogContentEntryViewModel(
            heading,
            string.Join(" · ", detailParts),
            unit.ThumbnailRef,
            unit.Identity,
            details.Identity,
            details.Item.MediaType,
            unit.Sources,
            mediaSessionService,
            openAsync,
            trustedRemoteOrigins,
            imageSet,
            openApiImagesAsync,
            findApiImagesAsync,
            videoSet,
            openApiVideosAsync,
            findApiVideosAsync,
            completedContentKeys.Contains(
                unit.Identity.StableKey),
            setSeenAsync,
            remoteArtworkReferrer,
            apiContentProviderId,
            seasonLabel);
    }

    private async Task ToggleAllSeenAsync()
    {
        var target = !AllEntriesSeen;
        if (_setAllSeenAsync is null ||
            !await _setAllSeenAsync(
                _trackableEntries
                    .Select(static entry => entry.Identity)
                    .ToArray(),
                target))
        {
            return;
        }

        foreach (var entry in _trackableEntries)
        {
            entry.SetSeen(target);
        }
    }

    private CatalogContentEntryViewModel? ResolveContinueEntry()
    {
        var unfinished = _trackableEntries.FirstOrDefault(
            static entry =>
                !entry.IsSeen &&
                entry.CanOpenPreferred);
        if (unfinished is not null)
        {
            return unfinished;
        }

        return _trackableEntries.Count == 0
            ? _orderedEntries.FirstOrDefault(
                static entry => entry.CanOpenPreferred)
            : null;
    }

    private Task ContinueAsync()
    {
        var command = ResolveContinueEntry()?.OpenPreferredCommand;
        return command is null
            ? Task.CompletedTask
            : command.ExecuteAsync(null);
    }

    private void OnEntrySeenChanged(
        object? sender,
        EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(AllEntriesSeen));
        OnPropertyChanged(nameof(ToggleAllSeenText));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueActionText));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    private static string BuildMetadata(CatalogItemDetails details)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(details.ReleaseDate))
        {
            parts.Add(details.ReleaseDate);
        }

        if (!string.IsNullOrWhiteSpace(details.ContentRating))
        {
            parts.Add(details.ContentRating);
        }

        if (details.RuntimeSeconds is > 0)
        {
            parts.Add(FormatDuration(details.RuntimeSeconds.Value));
        }

        if (details.PageCount is > 0)
        {
            parts.Add(UiTextCatalog.Format("PageCount", details.PageCount));
        }

        if (!string.IsNullOrWhiteSpace(details.OriginalLanguage))
        {
            parts.Add(details.OriginalLanguage);
        }

        return string.Join(" · ", parts);
    }

    private static string BuildItemSourceSubheading(CatalogItemDetails details)
    {
        if (details.RuntimeSeconds is > 0)
        {
            return FormatDuration(details.RuntimeSeconds.Value);
        }

        return details.PageCount is > 0
            ? UiTextCatalog.Format("PageCount", details.PageCount)
            : string.Empty;
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);
        return duration.TotalHours >= 1
            ? UiTextCatalog.Format(
                "DurationHoursMinutes",
                (int)duration.TotalHours,
                duration.Minutes)
            : UiTextCatalog.Format(
                "DurationMinutes",
                Math.Max(1, duration.Minutes));
    }
}
