using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class TorrentDownloadLibraryTests
{
    [Fact]
    public async Task ActivityProgress_IsSavedAndRestoredAsStopped()
    {
        var store = new InMemoryTorrentDownloadStore();
        var preferences = new InMemoryTorrentDownloadPreferencesStore();
        var engine = new DownloadEngine();
        using var coordinator = new TorrentActivityCoordinator(engine);
        await using (var library = new TorrentDownloadLibrary(
                         coordinator,
                         store,
                         preferences,
                         engine))
        {
            await library.InitializeAsync();
            var activityId = coordinator.Begin("Chapter 73", "Example manga");
            coordinator.AttachSession(activityId, engine.SessionId);
            engine.Publish(new TorrentSessionSnapshot(
                engine.SessionId,
                TorrentSessionState.Downloading,
                DownloadedBytes: 500,
                UploadedBytes: 0,
                DownloadBytesPerSecond: 100,
                UploadBytesPerSecond: 0,
                ConnectedPeers: 3,
                TotalBytes: 1_000,
                WantedBytes: 1_000,
                WantedDownloadedBytes: 500,
                Progress: 0.5,
                DisplayName: "Example manga"));

            var active = Assert.Single(library.Records);
            Assert.Equal(TorrentDownloadState.Downloading, active.State);
            Assert.Equal(0.5, active.Progress);
            Assert.Equal(engine.SessionId, active.SessionId);
            await WaitUntilAsync(() =>
                library.Records.Single().Storage is not null);
        }

        using var restoredCoordinator = new TorrentActivityCoordinator(null);
        await using var restored = new TorrentDownloadLibrary(
            restoredCoordinator,
            store,
            preferences);
        await restored.InitializeAsync();

        var record = Assert.Single(restored.Records);
        Assert.Equal(TorrentDownloadState.Stopped, record.State);
        Assert.Null(record.SessionId);
        Assert.NotNull(record.Storage);
    }

    [Fact]
    public async Task DeleteActiveDownload_StopsTorrentAndDeletesHistory()
    {
        var store = new InMemoryTorrentDownloadStore();
        var engine = new DownloadEngine();
        using var coordinator = new TorrentActivityCoordinator(engine);
        await using var library = new TorrentDownloadLibrary(
            coordinator,
            store,
            new InMemoryTorrentDownloadPreferencesStore(),
            engine);
        await library.InitializeAsync();
        var activityId = coordinator.Begin("Episode 4");
        coordinator.AttachSession(activityId, engine.SessionId);
        var record = Assert.Single(library.Records);

        await library.DeleteAsync([record.Id]);

        Assert.Equal(
            (engine.SessionId, true),
            Assert.Single(engine.RemovedSessions));
        Assert.Empty(library.Records);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task SessionEndDeletePreference_RemovesActiveTorrentData()
    {
        var store = new InMemoryTorrentDownloadStore();
        var preferences = new InMemoryTorrentDownloadPreferencesStore(
            new TorrentDownloadPreferences(
                TorrentSessionEndBehavior.DeleteDownloads));
        var engine = new DownloadEngine();
        using var coordinator = new TorrentActivityCoordinator(engine);
        await using var library = new TorrentDownloadLibrary(
            coordinator,
            store,
            preferences,
            engine);
        await library.InitializeAsync();
        var activityId = coordinator.Begin("Manual magnet");
        coordinator.AttachSession(activityId, engine.SessionId);

        await library.ShutdownAsync();

        Assert.Equal(
            (engine.SessionId, true),
            Assert.Single(engine.RemovedSessions));
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task DismissedPendingIntake_DoesNotCreateDownloadHistory()
    {
        var store = new InMemoryTorrentDownloadStore();
        using var coordinator = new TorrentActivityCoordinator(null);
        await using var library = new TorrentDownloadLibrary(
            coordinator,
            store,
            new InMemoryTorrentDownloadPreferencesStore());
        await library.InitializeAsync();
        var activityId = coordinator.Begin("Unconfirmed magnet");

        coordinator.Dismiss(activityId);
        await library.ShutdownAsync();

        Assert.Empty(library.Records);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task DeleteInactiveDownload_UsesCapturedManagedFiles()
    {
        var store = new InMemoryTorrentDownloadStore();
        var record = new TorrentDownloadRecord(
            Guid.CreateVersion7(),
            ActivityId: null,
            SessionId: null,
            "Saved episode",
            Detail: null,
            TorrentDownloadState.Complete,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            DownloadedBytes: 1_000,
            TotalBytes: 1_000,
            WantedDownloadedBytes: 1_000,
            WantedBytes: 1_000,
            Progress: 1,
            StorageKey: "storage",
            Storage: new TorrentSessionStorage(
            [
                new TorrentStoredFile(
                    Path.Combine(Path.GetTempPath(), "saved.mkv"),
                    Path.Combine(Path.GetTempPath(), "saved.mkv.!mt"),
                    1_000)
            ]));
        await store.UpsertAsync(record);
        var engine = new DownloadEngine();
        using var coordinator = new TorrentActivityCoordinator(engine);
        await using var library = new TorrentDownloadLibrary(
            coordinator,
            store,
            new InMemoryTorrentDownloadPreferencesStore(),
            engine);
        await library.InitializeAsync();

        await library.DeleteAsync([record.Id]);

        Assert.Equal(1, engine.StoredDataDeleteCount);
        Assert.Empty(await store.LoadAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class DownloadEngine :
        ITorrentEngine,
        ITorrentDownloadStorage
    {
        public TorrentSessionId SessionId { get; } =
            TorrentSessionId.Create();

        public TorrentEngineCapabilities Capabilities =>
            TorrentEngineCapabilities.MonoTorrentBaseline;

        public List<(TorrentSessionId SessionId, bool DeleteData)>
            RemovedSessions
        { get; } = [];

        public int StoredDataDeleteCount { get; private set; }

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

        public ValueTask<TorrentSessionStorage?> GetSessionStorageAsync(
            TorrentSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TorrentSessionStorage?>(
                new TorrentSessionStorage(
                [
                    new TorrentStoredFile(
                        Path.Combine(Path.GetTempPath(), "episode.mkv"),
                        Path.Combine(Path.GetTempPath(), "episode.mkv.!mt"),
                        1_000)
                ]));

        public ValueTask DeleteStoredDataAsync(
            TorrentSessionStorage storage,
            CancellationToken cancellationToken = default)
        {
            StoredDataDeleteCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
