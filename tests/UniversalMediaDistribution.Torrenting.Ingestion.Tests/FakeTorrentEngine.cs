using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

internal sealed class FakeTorrentEngine : ITorrentEngine
{
    private readonly IReadOnlyList<TorrentFileEntry> _files;
    private readonly IReadOnlyDictionary<int, byte[]> _contents;
    private readonly Task<IReadOnlyList<TorrentFileEntry>>? _pendingFiles;
    private readonly TorrentSessionId _sessionId = TorrentSessionId.Create();

    public FakeTorrentEngine(
        TorrentEngineCapabilities capabilities,
        IReadOnlyList<TorrentFileEntry>? files = null,
        IReadOnlyDictionary<int, byte[]>? contents = null,
        Task<IReadOnlyList<TorrentFileEntry>>? pendingFiles = null)
    {
        Capabilities = capabilities;
        _files = files ?? [];
        _contents = contents ?? new Dictionary<int, byte[]>();
        _pendingFiles = pendingFiles;
    }

    public TorrentEngineCapabilities Capabilities { get; }

    public int AddCount { get; private set; }

    public TorrentDescriptor? AddedDescriptor { get; private set; }

    public TorrentAddOptions? AddedOptions { get; private set; }

    public List<(int FileIndex, TorrentFilePriority Priority)> PriorityChanges { get; } = [];

    public event EventHandler<TorrentSessionChangedEventArgs>? SessionChanged
    {
        add { }
        remove { }
    }

    public ValueTask<TorrentSessionId> AddAsync(
        TorrentDescriptor descriptor,
        TorrentAddOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddCount++;
        AddedDescriptor = descriptor;
        AddedOptions = options;
        return ValueTask.FromResult(_sessionId);
    }

    public async ValueTask<IReadOnlyList<TorrentFileEntry>> GetFilesAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSession(sessionId);
        return _pendingFiles is null
            ? _files
            : await _pendingFiles.WaitAsync(cancellationToken);
    }

    public ValueTask<IMediaReadHandle> OpenReadAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSession(sessionId);

        if (!_contents.TryGetValue(fileIndex, out var content))
        {
            throw new FileNotFoundException($"Fake torrent file {fileIndex} was not configured.");
        }

        return ValueTask.FromResult<IMediaReadHandle>(new MemoryReadHandle(content));
    }

    public ValueTask<Uri?> GetPlaybackUriAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSession(sessionId);
        return ValueTask.FromResult<Uri?>(null);
    }

    public ValueTask SetFilePriorityAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        TorrentFilePriority priority,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSession(sessionId);
        PriorityChanges.Add((fileIndex, priority));
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSession(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSession(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(
        TorrentSessionId sessionId,
        bool deleteData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSession(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void AssertSession(TorrentSessionId sessionId)
    {
        if (sessionId != _sessionId)
        {
            throw new ArgumentException("Unknown fake torrent session.", nameof(sessionId));
        }
    }

    private sealed class MemoryReadHandle(byte[] content) : IMediaReadHandle
    {
        public Stream? Stream { get; } = new MemoryStream(content, writable: false);

        public Uri? PlaybackUri => null;

        public async ValueTask DisposeAsync()
        {
            if (Stream is not null)
            {
                await Stream.DisposeAsync();
            }
        }
    }
}
