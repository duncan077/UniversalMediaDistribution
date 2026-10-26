using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed class CatalogCardViewModel : IDisposable
{
    public CatalogCardViewModel(
        CatalogCard card,
        TorrentMediaSessionService mediaSessionService,
        IReadOnlyList<Uri>? trustedRemoteOrigins,
        Func<Guid, Task> openAsync,
        bool isFavorite,
        CatalogProgressSummary? progress,
        Func<Guid, bool, Task> toggleFavoriteAsync,
        Func<CatalogProgressSummary, Task> continueAsync,
        Uri? remoteArtworkReferrer = null,
        CatalogProgressSummary? activity = null,
        CatalogLatestUnit? latestUnit = null)
        : this(
            card,
            mediaSessionService,
            trustedRemoteOrigins,
            remoteArtworkReferrer,
            () => openAsync(card.Id),
            UiTextCatalog.Get("LocalResult"),
            isOnline: false,
            isFavorite,
            progress,
            () => toggleFavoriteAsync(card.Id, !isFavorite),
            progress is null
                ? null
                : () => continueAsync(progress),
            activity,
            latestUnit)
    {
    }

    public CatalogCardViewModel(
        ContentSourceCatalogSearchResult searchResult,
        TorrentMediaSessionService mediaSessionService,
        IReadOnlyList<Uri>? trustedRemoteOrigins,
        Func<ContentSourceCatalogSearchResult, Task> openAsync,
        bool isFavorite,
        CatalogProgressSummary? progress,
        Func<Guid, bool, Task>? toggleFavoriteAsync,
        Func<CatalogProgressSummary, Task>? continueAsync,
        // A deduplicated entry stands for several providers, so the caller
        // composes the badge rather than it being derived from one result.
        string? originBadgeOverride = null,
        Uri? remoteArtworkReferrer = null)
        : this(
            searchResult.Card,
            mediaSessionService,
            trustedRemoteOrigins,
            remoteArtworkReferrer,
            () => openAsync(searchResult),
            originBadgeOverride ??
            (searchResult.IsOnline
                ? UiTextCatalog.Format(
                    "OnlineResult",
                    searchResult.Provider?.DisplayName ??
                    UiTextCatalog.Get("Unknown"))
                : UiTextCatalog.Get("LocalResult")),
            searchResult.IsOnline,
            isFavorite,
            progress,
            !searchResult.IsOnline && toggleFavoriteAsync is not null
                ? () => toggleFavoriteAsync(
                    searchResult.Card.Id,
                    !isFavorite)
                : null,
            !searchResult.IsOnline &&
            progress is not null &&
            continueAsync is not null
                ? () => continueAsync(progress)
                : null)
    {
    }

    private CatalogCardViewModel(
        CatalogCard card,
        TorrentMediaSessionService mediaSessionService,
        IReadOnlyList<Uri>? trustedRemoteOrigins,
        Uri? remoteArtworkReferrer,
        Func<Task> openAsync,
        string originBadge,
        bool isOnline,
        bool isFavorite,
        CatalogProgressSummary? progress,
        Func<Task>? toggleFavoriteAsync,
        Func<Task>? continueAsync,
        // Separate from progress on purpose: the Continue button only offers
        // a chapter that was actually started, while the badge has to speak
        // for any chapter reached — otherwise a title only announces new
        // chapters once one of them has been marked seen.
        CatalogProgressSummary? activity = null,
        CatalogLatestUnit? latestUnit = null)
    {
        Id = card.Id;
        Name = CatalogDisplayNameSelector.Select(
            card.Name,
            card.AlternativeNames);
        MediaTypeName = UiTextCatalog.MediaTypeName(card.MediaType);
        Subtype = UiTextCatalog.SubtypeName(card.SubtypeCode);
        Description = card.Description;
        GenreText = card.Genres.Count == 0
            ? UiTextCatalog.Get("Uncategorized")
            : string.Join(" · ", card.Genres);
        CategoryText = string.Join("  ·  ", card.Categories);
        var taxonomy = card.Categories
            .Concat(card.Genres)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        TaxonomyText = taxonomy.Length == 0
            ? UiTextCatalog.Get("Uncategorized")
            : string.Join(" · ", taxonomy);
        var distributorNames = string.Join(
            " · ",
            card.Distributors
                .Where(static distributor => !CatalogMetadataSource.IsSyntheticDistributor(distributor))
                .Select(static distributor => distributor.Name));
        ProviderText = distributorNames.Length == 0
            ? string.Empty
            : UiTextCatalog.Format("ProviderList", distributorNames);
        TypeBadge = card.MediaType switch
        {
            MediaType.Movie => "▶",
            MediaType.Book => "▤",
            MediaType.Audio => "♪",
            _ => "•"
        };
        OriginBadge = originBadge;
        IsOnline = isOnline;
        IsFavorite = isFavorite;
        FavoriteGlyph = isFavorite ? "\u2605" : "\u2606";
        FavoriteActionText = UiTextCatalog.Get(
            isFavorite
                ? "RemoveFavoriteItem"
                : "AddFavoriteItem");
        ProgressText = progress is null
            ? string.Empty
            : FormatProgress(progress);
        NewContentText = FormatLatestUnit(latestUnit, activity ?? progress);
        if (!string.IsNullOrWhiteSpace(card.ThumbnailRef) &&
            CatalogArtworkViewModel.IsCacheableRemoteReference(
                card.ThumbnailRef))
        {
            Artwork = new CatalogArtworkViewModel(
                card.ThumbnailRef,
                torrentContext: null,
                mediaSessionService,
                trustedRemoteOrigins,
                remoteReferrer: remoteArtworkReferrer);
        }

        OpenCommand = new AsyncRelayCommand(openAsync);
        ToggleFavoriteCommand = toggleFavoriteAsync is null
            ? null
            : new AsyncRelayCommand(toggleFavoriteAsync);
        ContinueCommand = continueAsync is null
            ? null
            : new AsyncRelayCommand(continueAsync);
    }

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand? ToggleFavoriteCommand { get; }

    public IAsyncRelayCommand? ContinueCommand { get; }

    public Guid Id { get; }

    public string Name { get; }

    public string MediaTypeName { get; }

    public string Subtype { get; }

    public string Description { get; }

    public string GenreText { get; }

    public string CategoryText { get; }

    public string TaxonomyText { get; }

    public string ProviderText { get; }

    public bool HasProviders => ProviderText.Length > 0;

    public string TypeBadge { get; }

    public string OriginBadge { get; }

    public bool IsOnline { get; }

    public bool IsFavorite { get; }

    public string FavoriteGlyph { get; }

    public string FavoriteActionText { get; }

    public bool CanToggleFavorite =>
        ToggleFavoriteCommand is not null;

    public string ProgressText { get; }

    public bool HasProgress => ProgressText.Length > 0;

    /// <summary>
    /// Names what has arrived past where the viewer stopped, so a title that
    /// gained a chapter says so on the shelf instead of only revealing it once
    /// the page is opened.
    /// </summary>
    public string NewContentText { get; }

    public bool HasNewContent => NewContentText.Length > 0;

    public CatalogArtworkViewModel? Artwork { get; }

    public bool HasArtwork => Artwork is not null;

    /// <summary>
    /// Names the title's newest chapter or episode — always, so a shelf says
    /// how far a series has got whether or not it has been opened — and marks
    /// it "New" when it is past where the viewer stopped.
    /// </summary>
    private static string FormatLatestUnit(
        CatalogLatestUnit? latestUnit,
        CatalogProgressSummary? activity)
    {
        if (latestUnit is null)
        {
            return string.Empty;
        }

        var unitName = UiTextCatalog.Get(latestUnit.Kind switch
        {
            ContentUnitKind.Episode => "UnitEpisode",
            ContentUnitKind.Chapter => "UnitChapter",
            ContentUnitKind.Track => "UnitTrack",
            ContentUnitKind.AudioChapter => "UnitAudioChapter",
            _ => "Continue"
        });
        var latest = $"{unitName} {latestUnit.DisplayNumber}";
        return activity?.HasLaterUnits is true
            ? $"{UiTextCatalog.Get("NewContentBadge")} · {latest}"
            : latest;
    }

    private static string FormatProgress(
        CatalogProgressSummary summary)
    {
        var content = summary.Content;
        var position = summary.Progress.PositionKind switch
        {
            MediaPositionKind.Page => UiTextCatalog.Format(
                "ProgressPage",
                Math.Max(1, (int)Math.Floor(
                    summary.Progress.Position) + 1)),
            MediaPositionKind.Seconds => UiTextCatalog.Format(
                "ProgressTime",
                FormatDuration(summary.Progress.Position)),
            _ => string.Empty
        };
        if (content.UnitKind is null)
        {
            return UiTextCatalog.Format(
                "ContinueProgress",
                position);
        }

        var unitName = UiTextCatalog.Get(content.UnitKind.Value switch
        {
            ContentUnitKind.Episode => "UnitEpisode",
            ContentUnitKind.Chapter => "UnitChapter",
            ContentUnitKind.Track => "UnitTrack",
            ContentUnitKind.AudioChapter => "UnitAudioChapter",
            _ => "Continue"
        });
        var unit = content.SeasonNumber is { } season
            ? UiTextCatalog.Format(
                "SeasonUnitProgress",
                season,
                unitName,
                content.DisplayNumber ?? string.Empty)
            : $"{unitName} {content.DisplayNumber}";
        return string.IsNullOrWhiteSpace(position)
            ? unit
            : $"{unit} \u00b7 {position}";
    }

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:" +
              $"{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    /// <summary>
    /// Releases the poster.
    /// <para>
    /// The artwork holds a decoded image, which is native memory the collector
    /// does not account for and so is slow to reclaim. A card dropped from the
    /// reuse cache has to say so rather than wait to be finalized.
    /// </para>
    /// </summary>
    public void Dispose() => Artwork?.Dispose();
}
