using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Reading;
using UniversalMediaDistribution.Reading;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class ImageReaderViewModel : ViewModelBase, IDisposable
{
    private const double MinimumZoomFactor = 0.5;
    private const double MaximumZoomFactor = 2;
    private const double ZoomStep = 0.1;
    private const double MinimumPageWidth = 240;
    private const double MaximumPageWidth = 2400;

    /// <summary>
    /// How many pages either side of the current one keep their decoded image.
    /// Everything outside is released, so reading a three-hundred page chapter
    /// costs the same memory as reading a short one; without this every page
    /// ever shown stayed decoded until the chapter was closed.
    /// </summary>
    private static int RetainedPagesPerSide => ImageMemoryPolicy.RetainedReaderPagesPerSide;

    /// <summary>
    /// How many pages either side of the visible one are decoded before being
    /// reached, so a page is ready by the time it arrives rather than starting
    /// to decode once it is already on screen.
    /// </summary>
    private static int PreloadedPagesPerSide => ImageMemoryPolicy.PreloadedReaderPagesPerSide;

    /// <summary>
    /// How long the reading position must hold still before it is written.
    /// A continuous scroll crosses pages faster than a database can usefully
    /// record them, and only where it stops is worth keeping.
    /// </summary>
    private static readonly TimeSpan ProgressSettleDelay =
        TimeSpan.FromSeconds(2);

    private int _visiblePageIndex = -1;
    private CancellationTokenSource? _progressSettleCancellation;
    private readonly CancellationTokenSource _measurement = new();
    private readonly CatalogContentIdentity _contentIdentity;
    private readonly CatalogContentIdentity _catalogItemIdentity;
    private readonly IUserMediaStateStore _userMediaStateStore;
    private readonly IDisposable? _ownedPageSource;
    private bool _isInitialized;
    // Guards the restore pass: applying the stored zoom must not immediately
    // write it back.
    private bool _isRestoringPreferences;
    private bool _disposed;
    private double _viewportWidth = 1000;
    // Set when the chapter was already seen, so page saves never clear it.
    private bool _wasSeen;

    private ImageReaderViewModel(
        string title,
        CatalogContentIdentity contentIdentity,
        CatalogContentIdentity catalogItemIdentity,
        IReadOnlyList<ReaderPageViewModel> pages,
        IUserMediaStateStore userMediaStateStore,
        IDisposable? ownedPageSource = null)
    {
        Title = title;
        _contentIdentity = contentIdentity;
        _catalogItemIdentity = catalogItemIdentity;
        _userMediaStateStore = userMediaStateStore;
        _ownedPageSource = ownedPageSource;
        Pages = pages;

        // One estimate for the chapter, so a page that has not been decoded
        // reserves the shape this chapter's pages actually are rather than a
        // fixed guess. Chapters differ enormously — a scanned paperback and a
        // vertical strip share no proportions — so the guess has to be per
        // chapter, not per application.
        var aspectEstimate = new ReaderPageAspectEstimate();
        foreach (var page in pages)
        {
            page.UseAspectEstimate(aspectEstimate);
        }

        ApplyPageDisplayWidth();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyPropertyChangedFor(nameof(PageStatus))]
    [NotifyPropertyChangedFor(nameof(CanGoToPreviousPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToNextPage))]
    [NotifyPropertyChangedFor(nameof(IsLastPage))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaged))]
    [NotifyPropertyChangedFor(nameof(IsContinuous))]
    private ReaderDisplayMode _displayMode = ReaderDisplayMode.Paged;

    [ObservableProperty]
    private ReaderPageViewModel? _selectedContinuousPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentage))]
    [NotifyPropertyChangedFor(nameof(CanZoomOut))]
    [NotifyPropertyChangedFor(nameof(CanZoomIn))]
    private double _zoomFactor = 1;

    public string Title { get; }

    public IReadOnlyList<ReaderPageViewModel> Pages { get; }

    public ReaderPageViewModel? CurrentPage => Pages.Count == 0 ? null : Pages[CurrentPageIndex];

    public string PageStatus => Pages.Count == 0
        ? UiTextCatalog.Get("NoPages")
        : UiTextCatalog.Format(
            "PageOf",
            CurrentPageIndex + 1,
            Pages.Count);

    public bool IsPaged => DisplayMode is ReaderDisplayMode.Paged;

    public bool IsContinuous => DisplayMode is ReaderDisplayMode.Continuous;

    public bool CanGoToPreviousPage =>
        Pages.Count > 0 && CurrentPageIndex > 0;

    public bool CanGoToNextPage =>
        Pages.Count > 0 && CurrentPageIndex < Pages.Count - 1;

    public bool IsLastPage =>
        Pages.Count > 0 && CurrentPageIndex == Pages.Count - 1;

    public string ZoomPercentage =>
        $"{Math.Round(ZoomFactor * 100):0}%";

    public bool CanZoomOut => ZoomFactor > MinimumZoomFactor;

    public bool CanZoomIn => ZoomFactor < MaximumZoomFactor;

    /// <summary>
    /// Puts the reader on the first page, discarding the position restored
    /// from the last time this chapter was read.
    /// </summary>
    public void GoToFirstPage()
    {
        if (Pages.Count == 0)
        {
            return;
        }

        CurrentPageIndex = 0;
        SelectedContinuousPage = Pages[0];
    }

    public static async ValueTask<ImageReaderViewModel> CreateAsync(
        string title,
        CatalogContentIdentity contentIdentity,
        CatalogContentIdentity catalogItemIdentity,
        PreparedImageSequence sequence,
        TorrentMediaSessionService mediaSessionService,
        IUserMediaStateStore userMediaStateStore,
        CancellationToken cancellationToken = default)
    {
        var reader = new ImageReaderViewModel(
            title,
            contentIdentity,
            catalogItemIdentity,
            sequence.Pages
                .Select(page => new ReaderPageViewModel(page, mediaSessionService))
                .ToArray(),
            userMediaStateStore);
        await reader.RestoreStateAsync(cancellationToken);
        return reader;
    }

    public static async ValueTask<ImageReaderViewModel> CreateAsync(
        string title,
        CatalogContentIdentity contentIdentity,
        CatalogContentIdentity catalogItemIdentity,
        ApiContentImageSet imageSet,
        IUserMediaStateStore userMediaStateStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageSet);
        var reader = new ImageReaderViewModel(
            title,
            contentIdentity,
            catalogItemIdentity,
            imageSet.Pages
                .Select(static page => new ReaderPageViewModel(page))
                .ToArray(),
            userMediaStateStore);
        await reader.RestoreStateAsync(cancellationToken);
        return reader;
    }

    public static async ValueTask<ImageReaderViewModel> CreateAsync(
        string title,
        CatalogContentIdentity contentIdentity,
        CatalogContentIdentity catalogItemIdentity,
        PreparedImageArchive archive,
        IUserMediaStateStore userMediaStateStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var reader = new ImageReaderViewModel(
            title,
            contentIdentity,
            catalogItemIdentity,
            archive.Pages
                .Select(page => new ReaderPageViewModel(page, archive))
                .ToArray(),
            userMediaStateStore,
            archive);
        await reader.RestoreStateAsync(cancellationToken);
        return reader;
    }

    public static async ValueTask<ImageReaderViewModel> CreateAsync(
        string title,
        CatalogContentIdentity contentIdentity,
        CatalogContentIdentity catalogItemIdentity,
        IPdfDocument document,
        IUserMediaStateStore userMediaStateStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var reader = new ImageReaderViewModel(
            title,
            contentIdentity,
            catalogItemIdentity,
            document.Pages
                .Select(page => new ReaderPageViewModel(page, document))
                .ToArray(),
            userMediaStateStore,
            document);
        await reader.RestoreStateAsync(cancellationToken);
        return reader;
    }

    private async ValueTask RestoreStateAsync(CancellationToken cancellationToken)
    {
        // Zoom and page mode belong to the device, so the next chapter opens
        // the way the last one was left rather than resetting, and neither is
        // something the reader wants to re-pick per title.
        var devicePreferences = (await _userMediaStateStore
            .LoadReaderDevicePreferencesAsync(cancellationToken))
            .Normalize();
        _isRestoringPreferences = true;
        try
        {
            ZoomFactor = devicePreferences.ZoomFactor;
        }
        finally
        {
            _isRestoringPreferences = false;
        }

        var mode = devicePreferences.DisplayMode;
        var progress = await _userMediaStateStore.LoadProgressAsync(_contentIdentity, cancellationToken);
        var pageIndex = progress?.PositionKind is MediaPositionKind.Page
            ? (int)Math.Floor(progress.Position)
            : 0;
        _wasSeen = progress?.IsCompleted is true;

        DisplayMode = mode;
        CurrentPageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, Pages.Count - 1));
        SelectedContinuousPage = CurrentPage;
        _isInitialized = true;
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPageIndex > 0)
        {
            CurrentPageIndex--;
            await SaveProgressAsync();
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPageIndex < Pages.Count - 1)
        {
            CurrentPageIndex++;
            await SaveProgressAsync();
        }
    }

    [RelayCommand]
    private Task UsePagedModeAsync() => SetDisplayModeAsync(ReaderDisplayMode.Paged);

    [RelayCommand]
    private Task UseContinuousModeAsync() => SetDisplayModeAsync(ReaderDisplayMode.Continuous);

    [RelayCommand]
    private void ZoomOut() =>
        ZoomFactor = Math.Max(
            MinimumZoomFactor,
            Math.Round(ZoomFactor - ZoomStep, 2));

    [RelayCommand]
    private void ZoomIn() =>
        ZoomFactor = Math.Min(
            MaximumZoomFactor,
            Math.Round(ZoomFactor + ZoomStep, 2));

    [RelayCommand]
    private void ResetZoom() => ZoomFactor = 1;

    /// <summary>
    /// Continuous reading gives every page a container, so the pages must stop
    /// loading themselves on being attached and wait to be asked. Paged reading
    /// shows one page and goes back to loading on sight.
    /// </summary>
    partial void OnDisplayModeChanged(ReaderDisplayMode value)
    {
        IsAutoScrollEnabled = false;
        var isContinuous = value is ReaderDisplayMode.Continuous;
        foreach (var page in Pages)
        {
            page.DefersLoadingToReader = isContinuous;
        }

        if (isContinuous)
        {
            // Nothing has reported a viewport yet, so seed from the restored
            // position rather than waiting for the first scroll.
            _visiblePageIndex = -1;
            LoadPagesAround(CurrentPageIndex);
            _ = MeasurePagesAsync(_measurement.Token);
        }
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (Pages.Count > 0)
        {
            SelectedContinuousPage = Pages[Math.Clamp(value, 0, Pages.Count - 1)];
        }

        ReleaseDistantPages(value);
    }

    partial void OnSelectedContinuousPageChanged(ReaderPageViewModel? value)
    {
        if (!_isInitialized || value is null || value.Position == CurrentPageIndex)
        {
            return;
        }

        CurrentPageIndex = value.Position;
        _ = SaveProgressAsync();
    }

    partial void OnZoomFactorChanged(double value)
    {
        ApplyPageDisplayWidth();
        if (!_isRestoringPreferences)
        {
            _ = SaveDevicePreferencesAsync(value);
        }
    }

    private async Task SaveDevicePreferencesAsync(double zoomFactor)
    {
        try
        {
            var current = await _userMediaStateStore
                .LoadReaderDevicePreferencesAsync();
            await _userMediaStateStore.SaveReaderDevicePreferencesAsync(
                current with { ZoomFactor = zoomFactor });
        }
        catch
        {
            // Losing a zoom preference is not worth interrupting reading for.
        }
    }

    /// <summary>
    /// Reports which page the continuous viewport is showing.
    /// <para>
    /// Decoding used to start when a container was realized, which tied what is
    /// in memory to how the list virtualizes. That is what forced the page boxes
    /// to be guessed at, and guessed boxes are what made the extent — and so the
    /// scroll position — move under the reader. Driving it from the visible page
    /// instead lets every page keep a real container, and leaves this the only
    /// thing deciding what is decoded.
    /// </para>
    /// </summary>
    public void ReportVisiblePage(int index)
    {
        if (Pages.Count == 0 || _disposed)
        {
            return;
        }

        var clamped = Math.Clamp(index, 0, Pages.Count - 1);
        if (clamped == _visiblePageIndex)
        {
            return;
        }

        _visiblePageIndex = clamped;

        // Moving the position releases the distant pages on its own, so only
        // the loading half is asked for here.
        SetCurrentPageQuietly(clamped);
        LoadPagesAround(clamped);
    }

    /// <summary>
    /// Moves the reading position without the write that normally follows it.
    /// Scrolling passes through pages far faster than progress is worth
    /// recording, so the write waits for the scroll to settle.
    /// </summary>
    private void SetCurrentPageQuietly(int index)
    {
        if (CurrentPageIndex != index)
        {
            CurrentPageIndex = index;
            SelectedContinuousPage = Pages[index];
        }

        _progressSettleCancellation?.Cancel();
        _progressSettleCancellation?.Dispose();
        var settle = new CancellationTokenSource();
        _progressSettleCancellation = settle;
        _ = SaveProgressWhenSettledAsync(settle);
    }

    private async Task SaveProgressWhenSettledAsync(
        CancellationTokenSource settle)
    {
        try
        {
            await Task.Delay(ProgressSettleDelay, settle.Token);
            await SaveProgressAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later position; that one carries the write.
        }
        finally
        {
            if (ReferenceEquals(_progressSettleCancellation, settle))
            {
                _progressSettleCancellation = null;
            }

            settle.Dispose();
        }
    }

    /// <summary>
    /// Decodes the visible page and the few either side of it.
    /// </summary>
    private void LoadPagesAround(int centerIndex)
    {
        for (var offset = 0; offset <= PreloadedPagesPerSide; offset++)
        {
            StartLoad(centerIndex + offset);
            if (offset > 0)
            {
                StartLoad(centerIndex - offset);
            }
        }
    }

    private void StartLoad(int index)
    {
        if (index >= 0 && index < Pages.Count)
        {
            _ = Pages[index].LoadAsync();
        }
    }

    /// <summary>
    /// Measures every page's header once, in order, so each page's box is the
    /// right height before it is scrolled to.
    /// <para>
    /// One page at a time and off the reading path: this exists to stop boxes
    /// changing size under the reader, and a burst of parallel reads competing
    /// with the decodes would trade one stutter for another. Pages whose header
    /// cannot be read cheaply — anything remote — are skipped and keep the
    /// chapter's estimate.
    /// </para>
    /// </summary>
    private async Task MeasurePagesAsync(CancellationToken cancellationToken)
    {
        foreach (var page in Pages)
        {
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            await page.ProbeSizeAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Frees the decoded image of every page outside the window around
    /// <paramref name="centerIndex"/>. A released page reloads when it scrolls
    /// back into view.
    /// </summary>
    private void ReleaseDistantPages(int centerIndex)
    {
        for (var index = 0; index < Pages.Count; index++)
        {
            if (!ReaderPageRetention.ShouldRetain(
                    index,
                    centerIndex,
                    Pages.Count,
                    RetainedPagesPerSide))
            {
                Pages[index].Release();
            }
        }
    }

    public void SetViewportWidth(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return;
        }

        _viewportWidth = value;
        ApplyPageDisplayWidth();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsAutoScrollEnabled = false;
        _measurement.Cancel();
        _measurement.Dispose();

        // A position still waiting out its settle delay would otherwise be lost
        // with the reader, which is exactly the position worth keeping: where
        // the chapter was left.
        var pendingProgress = _progressSettleCancellation;
        _progressSettleCancellation = null;
        if (pendingProgress is not null)
        {
            pendingProgress.Cancel();
            _ = SaveProgressAsync();
        }

        foreach (var page in Pages)
        {
            page.Dispose();
        }

        _ownedPageSource?.Dispose();

        GC.SuppressFinalize(this);
    }

    private async Task SetDisplayModeAsync(ReaderDisplayMode displayMode)
    {
        if (DisplayMode == displayMode)
        {
            return;
        }

        DisplayMode = displayMode;

        // Device-wide, alongside zoom: picking continuous once should hold for
        // everything the reader opens, not just this title.
        var current = await _userMediaStateStore
            .LoadReaderDevicePreferencesAsync();
        await _userMediaStateStore.SaveReaderDevicePreferencesAsync(
            current with { DisplayMode = displayMode });
    }

    private Task SaveProgressAsync()
    {
        if (!_isInitialized || Pages.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _userMediaStateStore.SaveProgressAsync(
            _contentIdentity,
            new MediaProgressState(
                CurrentPageIndex,
                MediaPositionKind.Page,
                _wasSeen || CurrentPageIndex == Pages.Count - 1,
                DateTimeOffset.UtcNow)).AsTask();
    }

    private void ApplyPageDisplayWidth()
    {
        var pageWidth = Math.Round(
            Math.Clamp(
                _viewportWidth * ZoomFactor,
                MinimumPageWidth,
                MaximumPageWidth),
            2);
        foreach (var page in Pages)
        {
            page.SetDisplayWidth(pageWidth);
        }

        // Zooming in past the decoded width re-decodes the pages on screen so
        // they stay sharp; the rest reload when they are reached.
        foreach (var page in Pages)
        {
            if (page.Image is not null)
            {
                _ = page.LoadAsync();
            }
        }
    }
}
