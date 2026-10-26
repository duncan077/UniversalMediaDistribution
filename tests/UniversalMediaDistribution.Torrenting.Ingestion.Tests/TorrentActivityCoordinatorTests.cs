using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class TorrentActivityCoordinatorTests
{
    [Fact]
    public void AttachSession_AppliesProgressPublishedBeforeContextWasKnown()
    {
        var engine = new ActivityTorrentEngine();
        using var coordinator = new TorrentActivityCoordinator(engine);
        var activityId = coordinator.Begin(
            "Chapter 73",
            "Preparing torrent");

        engine.Publish(new TorrentSessionSnapshot(
            engine.SessionId,
            TorrentSessionState.Downloading,
            DownloadedBytes: 256,
            UploadedBytes: 0,
            DownloadBytesPerSecond: 128,
            UploadBytesPerSecond: 0,
            ConnectedPeers: 4,
            TotalBytes: 2_000,
            WantedBytes: 1_000,
            WantedDownloadedBytes: 250,
            Progress: 0.25,
            EstimatedTimeRemaining: TimeSpan.FromSeconds(6),
            DisplayName: "Example manga"));

        var attachedId = coordinator.AttachSession(
            activityId,
            engine.SessionId);

        var activity = Assert.Single(coordinator.Activities);
        Assert.Equal(activityId, attachedId);
        Assert.Equal(TorrentActivityPhase.Downloading, activity.Phase);
        Assert.Equal(0.25, activity.Progress);
        Assert.Equal(1_000, activity.WantedBytes);
        Assert.Equal(250, activity.WantedDownloadedBytes);
        Assert.Equal(4, activity.ConnectedPeers);
        Assert.Equal("Example manga", activity.TorrentName);
    }

    [Fact]
    public void SessionLifecycle_UsesIndeterminateMetadataAndWaitingForPeers()
    {
        var engine = new ActivityTorrentEngine();
        using var coordinator = new TorrentActivityCoordinator(engine);
        var activityId = coordinator.Begin("Manual magnet");
        coordinator.AttachSession(activityId, engine.SessionId);

        engine.Publish(new TorrentSessionSnapshot(
            engine.SessionId,
            TorrentSessionState.Metadata,
            0,
            0,
            0,
            0,
            0));

        var metadata = Assert.Single(coordinator.Activities);
        Assert.Equal(
            TorrentActivityPhase.FetchingMetadata,
            metadata.Phase);
        Assert.True(metadata.IsIndeterminate);

        engine.Publish(new TorrentSessionSnapshot(
            engine.SessionId,
            TorrentSessionState.Downloading,
            0,
            0,
            0,
            0,
            0,
            WantedBytes: 1_000,
            WantedDownloadedBytes: 0,
            Progress: 0));

        var waiting = Assert.Single(coordinator.Activities);
        Assert.Equal(TorrentActivityPhase.FindingPeers, waiting.Phase);
        Assert.False(waiting.IsIndeterminate);
    }

    [Fact]
    public async Task Cancel_RemovesEngineSessionAndActivity()
    {
        var engine = new ActivityTorrentEngine();
        using var coordinator = new TorrentActivityCoordinator(engine);
        var activityId = coordinator.Begin("Manual magnet");
        coordinator.AttachSession(activityId, engine.SessionId);

        await coordinator.CancelAsync(activityId, deleteData: true);

        Assert.Equal(
            (engine.SessionId, true),
            Assert.Single(engine.RemovedSessions));
        Assert.Empty(coordinator.Activities);
    }

    private sealed class ActivityTorrentEngine : ITorrentEngine
    {
        public TorrentSessionId SessionId { get; } =
            TorrentSessionId.Create();

        public TorrentEngineCapabilities Capabilities =>
            TorrentEngineCapabilities.MonoTorrentBaseline;

        public List<(TorrentSessionId SessionId, bool DeleteData)>
            RemovedSessions
        { get; } = [];

        public event EventHandler<TorrentSessionChangedEventArgs>?
            SessionChanged;

        public void Publish(TorrentSessionSnapshot snapshot) =>
            SessionChanged?.Invoke(
                this,
                new TorrentSessionChangedEventArgs(snapshot));

        public ValueTask<TorrentSessionId> AddAsync(
            TorrentDescriptor descriptor,
            TorrentAddOptions options,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SessionId);

        public ValueTask<IReadOnlyList<TorrentFileEntry>> GetFilesAsync(
            TorrentSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TorrentFileEntry>>([]);

        public ValueTask<IMediaReadHandle> OpenReadAsync(
            TorrentSessionId sessionId,
            int fileIndex,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Uri?> GetPlaybackUriAsync(
            TorrentSessionId sessionId,
            int fileIndex,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Uri?>(null);

        public ValueTask SetFilePriorityAsync(
            TorrentSessionId sessionId,
            int fileIndex,
            TorrentFilePriority priority,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask PauseAsync(
            TorrentSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ResumeAsync(
            TorrentSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask RemoveAsync(
            TorrentSessionId sessionId,
            bool deleteData,
            CancellationToken cancellationToken = default)
        {
            RemovedSessions.Add((sessionId, deleteData));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
