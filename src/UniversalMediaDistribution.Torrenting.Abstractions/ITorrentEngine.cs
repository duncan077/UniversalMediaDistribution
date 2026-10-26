namespace UniversalMediaDistribution.Torrenting;

public interface ITorrentEngine : IAsyncDisposable
{
    TorrentEngineCapabilities Capabilities { get; }

    event EventHandler<TorrentSessionChangedEventArgs>? SessionChanged;

    ValueTask<TorrentSessionId> AddAsync(
        TorrentDescriptor descriptor,
        TorrentAddOptions options,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TorrentFileEntry>> GetFilesAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<IMediaReadHandle> OpenReadAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default);

    ValueTask<Uri?> GetPlaybackUriAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default);

    ValueTask SetFilePriorityAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        TorrentFilePriority priority,
        CancellationToken cancellationToken = default);

    ValueTask PauseAsync(TorrentSessionId sessionId, CancellationToken cancellationToken = default);

    ValueTask ResumeAsync(TorrentSessionId sessionId, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(
        TorrentSessionId sessionId,
        bool deleteData,
        CancellationToken cancellationToken = default);
}
