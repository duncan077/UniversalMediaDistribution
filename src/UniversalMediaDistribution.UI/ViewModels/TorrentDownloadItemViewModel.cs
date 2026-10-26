using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class TorrentDownloadItemViewModel : ViewModelBase
{
    private TorrentDownloadRecord _record;
    private readonly Action _selectionChanged;

    public TorrentDownloadItemViewModel(
        TorrentDownloadRecord record,
        Action selectionChanged,
        Func<TorrentDownloadItemViewModel, Task> pause,
        Func<TorrentDownloadItemViewModel, Task> resume,
        Action<TorrentDownloadItemViewModel> requestDelete)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _selectionChanged = selectionChanged ??
            throw new ArgumentNullException(nameof(selectionChanged));
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(requestDelete);
        PauseCommand = new AsyncRelayCommand(() => pause(this));
        ResumeCommand = new AsyncRelayCommand(() => resume(this));
        RequestDeleteCommand = new RelayCommand(
            () => requestDelete(this));
    }

    [ObservableProperty]
    private bool _isSelected;

    public Guid Id => _record.Id;

    public IAsyncRelayCommand PauseCommand { get; }

    public IAsyncRelayCommand ResumeCommand { get; }

    public IRelayCommand RequestDeleteCommand { get; }

    public TorrentDownloadRecord Record => _record;

    public string Title => _record.Title;

    public string Detail => _record.Detail ?? string.Empty;

    public bool HasDetail => !string.IsNullOrWhiteSpace(_record.Detail);

    public string StateText => UiTextCatalog.Get(
        _record.State switch
        {
            TorrentDownloadState.Adding => "DownloadStateAdding",
            TorrentDownloadState.FetchingMetadata => "DownloadStateMetadata",
            TorrentDownloadState.CheckingFiles => "DownloadStateChecking",
            TorrentDownloadState.FindingPeers => "DownloadStatePeers",
            TorrentDownloadState.Downloading => "DownloadStateDownloading",
            TorrentDownloadState.Complete => "DownloadStateComplete",
            TorrentDownloadState.Seeding => "DownloadStateSeeding",
            TorrentDownloadState.Paused => "DownloadStatePaused",
            TorrentDownloadState.Failed => "DownloadStateFailed",
            TorrentDownloadState.Removing => "DownloadStateRemoving",
            _ => "DownloadStateStopped"
        });

    public bool IsActive => _record.IsActive;

    public bool IsComplete => _record.IsComplete;

    public bool IsFailed => _record.State is TorrentDownloadState.Failed;

    public bool CanPause =>
        _record.ActivityId is not null &&
        _record.State is (TorrentDownloadState.FetchingMetadata or
            TorrentDownloadState.CheckingFiles or
            TorrentDownloadState.FindingPeers or
            TorrentDownloadState.Downloading or
            TorrentDownloadState.Seeding);

    public bool CanResume =>
        _record.ActivityId is not null &&
        _record.State is TorrentDownloadState.Paused;

    public bool IsProgressIndeterminate =>
        _record.Progress is not (>= 0 and <= 1);

    public double ProgressPercent =>
        Math.Clamp(_record.Progress ?? 0, 0, 1) * 100;

    public string ProgressText
    {
        get
        {
            if (_record.Progress is not { } progress)
            {
                return StateText;
            }

            var downloaded =
                _record.WantedDownloadedBytes ?? _record.DownloadedBytes;
            var total = _record.WantedBytes ?? _record.TotalBytes;
            return total is > 0
                ? UiTextCatalog.Format(
                    "DownloadProgressWithSize",
                    progress * 100,
                    FormatBytes(downloaded),
                    FormatBytes(total.Value))
                : UiTextCatalog.Format(
                    "DownloadProgressOnly",
                    progress * 100);
        }
    }

    public string StoredSizeText =>
        UiTextCatalog.Format(
            "DownloadStoredSize",
            FormatBytes(_record.StoredBytes));

    public string UpdatedText =>
        UiTextCatalog.Format(
            "DownloadUpdated",
            _record.UpdatedAt
                .ToLocalTime()
                .ToString("g", CultureInfo.CurrentCulture));

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();

    public void Update(TorrentDownloadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_record == record) return;
        _record = record;
        OnPropertyChanged(nameof(Record));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(StoredSizeText));
        OnPropertyChanged(nameof(UpdatedText));
    }

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        var size = (double)value;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.##} {units[unit]}";
    }
}
