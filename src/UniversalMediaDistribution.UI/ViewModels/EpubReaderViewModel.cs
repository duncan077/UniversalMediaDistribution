using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed class EpubChapterViewModel : IDisposable
{
    public EpubChapterViewModel(
        PreparedEpubChapter chapter,
        PreparedEpubBook book,
        double fontSize)
    {
        Position = chapter.Position;
        Path = chapter.Path;
        Title = chapter.Title;
        Blocks = chapter.Blocks
            .Select(block => new EpubContentBlockViewModel(block, book, fontSize))
            .ToArray();
    }

    public int Position { get; }

    public string Path { get; }

    public string Title { get; }

    public IReadOnlyList<EpubContentBlockViewModel> Blocks { get; }

    public void ReleaseImages()
    {
        foreach (var block in Blocks)
        {
            block.Release();
        }
    }

    public void Dispose()
    {
        foreach (var block in Blocks)
        {
            block.Dispose();
        }
    }
}

public partial class EpubReaderViewModel : ViewModelBase, IDisposable
{
    private const double MinimumFontSize = 14;
    private const double MaximumFontSize = 32;
    private readonly CatalogContentIdentity _contentIdentity;
    private readonly IUserMediaStateStore _userMediaStateStore;
    private readonly PreparedEpubBook _book;
    private bool _isInitialized;
    private bool _disposed;
    // Set when the chapter was already seen, so chapter saves never clear it.
    private bool _wasSeen;

    private EpubReaderViewModel(
        string title,
        CatalogContentIdentity contentIdentity,
        PreparedEpubBook book,
        IUserMediaStateStore userMediaStateStore)
    {
        Title = title;
        PackageTitle = book.PackageTitle;
        Language = book.Language;
        _contentIdentity = contentIdentity;
        _userMediaStateStore = userMediaStateStore;
        _book = book;
        Chapters = book.Chapters
            .Select(chapter => new EpubChapterViewModel(chapter, book, FontSize))
            .ToArray();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChapter))]
    [NotifyPropertyChangedFor(nameof(ChapterStatus))]
    private int _currentChapterIndex;

    [ObservableProperty]
    private double _fontSize = 19;

    public string Title { get; }

    public string? PackageTitle { get; }

    public string? Language { get; }

    public IReadOnlyList<EpubChapterViewModel> Chapters { get; }

    public EpubChapterViewModel? CurrentChapter => Chapters.Count == 0
        ? null
        : Chapters[CurrentChapterIndex];

    public string ChapterStatus => Chapters.Count == 0
        ? UiTextCatalog.Get("NoChapters")
        : UiTextCatalog.Format(
            "ChapterOf",
            CurrentChapterIndex + 1,
            Chapters.Count);

    public static async ValueTask<EpubReaderViewModel> CreateAsync(
        string title,
        CatalogContentIdentity contentIdentity,
        PreparedEpubBook book,
        IUserMediaStateStore userMediaStateStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(contentIdentity);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(userMediaStateStore);
        var reader = new EpubReaderViewModel(
            title,
            contentIdentity,
            book,
            userMediaStateStore);
        try
        {
            await reader.RestoreStateAsync(cancellationToken);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    [RelayCommand]
    private Task PreviousChapterAsync() =>
        MoveToChapterAsync(CurrentChapterIndex - 1);

    [RelayCommand]
    private Task NextChapterAsync() =>
        MoveToChapterAsync(CurrentChapterIndex + 1);

    [RelayCommand]
    private void DecreaseFontSize() =>
        FontSize = Math.Max(MinimumFontSize, FontSize - 1);

    [RelayCommand]
    private void IncreaseFontSize() =>
        FontSize = Math.Min(MaximumFontSize, FontSize + 1);

    partial void OnFontSizeChanged(double value)
    {
        if (Chapters is null)
        {
            return;
        }

        foreach (var block in Chapters.SelectMany(static chapter => chapter.Blocks))
        {
            block.SetFontSize(value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var chapter in Chapters)
        {
            chapter.Dispose();
        }

        _book.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task MoveToChapterAsync(int chapterIndex)
    {
        if (chapterIndex < 0 ||
            chapterIndex >= Chapters.Count ||
            chapterIndex == CurrentChapterIndex)
        {
            return;
        }

        CurrentChapter?.ReleaseImages();
        CurrentChapterIndex = chapterIndex;
        await SaveProgressAsync();
    }

    private async ValueTask RestoreStateAsync(CancellationToken cancellationToken)
    {
        var progress = await _userMediaStateStore.LoadProgressAsync(
            _contentIdentity,
            cancellationToken);
        var chapterIndex = progress?.PositionKind is MediaPositionKind.Page
            ? (int)Math.Floor(progress.Position)
            : 0;
        CurrentChapterIndex = Math.Clamp(
            chapterIndex,
            0,
            Math.Max(0, Chapters.Count - 1));
        _wasSeen = progress?.IsCompleted is true;
        _isInitialized = true;
    }

    private Task SaveProgressAsync()
    {
        if (!_isInitialized || Chapters.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _userMediaStateStore.SaveProgressAsync(
            _contentIdentity,
            new MediaProgressState(
                CurrentChapterIndex,
                MediaPositionKind.Page,
                _wasSeen || CurrentChapterIndex == Chapters.Count - 1,
                DateTimeOffset.UtcNow)).AsTask();
    }
}
