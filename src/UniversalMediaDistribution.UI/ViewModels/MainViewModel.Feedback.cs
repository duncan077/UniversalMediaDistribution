using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class MainViewModel
{
    public FeedbackViewModel CatalogFeedback { get; } = new();
    public FeedbackViewModel DetailsFeedback { get; } = new();
    public FeedbackViewModel PlaybackFeedback { get; } = new();
    public FeedbackViewModel ReaderFeedback { get; } = new();
    public FeedbackViewModel LiveTvFeedback { get; } = new();
    public FeedbackViewModel DownloadsFeedback { get; } = new();
    public FeedbackViewModel ProfilesFeedback { get; } = new();
    public FeedbackViewModel AccountFeedback { get; } = new();
    public FeedbackViewModel UpdatesFeedback { get; } = new();
    public FeedbackViewModel SourcesFeedback { get; } = new();
    public FeedbackViewModel MetadataFeedback { get; } = new();
    public FeedbackViewModel MagnetFeedback { get; } = new();
    public FeedbackViewModel FiltersFeedback { get; } = new();
    public FeedbackViewModel DownloadOptionsFeedback { get; } = new();
    public FeedbackViewModel FilesFeedback { get; } = new();
    public FeedbackViewModel BackupFeedback { get; } = new();
    public FeedbackViewModel FriendsFeedback { get; } = new();
    public FeedbackViewModel ListsFeedback { get; } = new();
    public FeedbackViewModel PartyFeedback { get; } = new();
    public FeedbackViewModel? LatestErrorTarget { get; private set; }

    private FeedbackViewModel ContentFeedback => IsEmbeddedMediaOpen
        ? PlaybackFeedback
        : IsReaderOpen || IsEpubReaderOpen ? ReaderFeedback : DetailsFeedback;

    private FeedbackViewModel EntryFeedback(CatalogContentIdentity identity) =>
        IsEmbeddedMediaOpen || IsReaderOpen || IsEpubReaderOpen
            ? ContentFeedback
            : SelectedDetails?.Entries.FirstOrDefault(entry =>
                entry.Identity.StableKey == identity.StableKey)?.Feedback ?? DetailsFeedback;

    internal void SetError(FeedbackViewModel target, string? message)
    {
        target.Message = message;
        // Retained for callers that check whether opening content succeeded.
        ErrorMessage = message;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = null;
            if (ReferenceEquals(target, PlaybackFeedback))
            {
                IsPlayerFullscreen = false;
            }

            LatestErrorTarget = target;
            OnPropertyChanged(nameof(LatestErrorTarget));
        }
    }

    private int _statusGeneration;

    internal void ApplyPlaybackStateFeedback(PlatformMediaState state)
    {
        if (!string.IsNullOrWhiteSpace(state.ErrorMessage))
        {
            IsEmbeddedMediaOpen = true;
            foreach (var option in ActiveVideoServers)
            {
                option.SetSelected(false);
            }
            var message = UiTextCatalog.Format("PlaybackFailed", state.ErrorMessage);
            if (PlaybackFeedback.Message != message)
            {
                SetError(PlaybackFeedback, message);
            }
        }
        else if (state.IsClosed && platformMediaPlayer.EmbeddedView is null)
        {
            // Back in an external native player is an explicit exit, not a
            // failed feed. Only failures retain the recovery shell.
            _activeApiVideoSelection = null;
            ActiveVideoServers.Clear();
            PlaybackFeedback.Message = null;
            ClearApiProviderChoices();
            ActiveMediaTitle = null;
            IsEmbeddedMediaOpen = false;
            OnPropertyChanged(nameof(HasActiveVideoServers));
        }
    }

    private async Task ExpireStatusAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (generation == _statusGeneration)
        {
            StatusMessage = null;
        }
    }
}
