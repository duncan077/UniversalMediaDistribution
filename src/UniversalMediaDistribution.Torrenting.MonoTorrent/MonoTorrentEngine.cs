using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Streaming;
using NativeClientEngine = MonoTorrent.Client.ClientEngine;
using NativeTorrentManager = MonoTorrent.Client.TorrentManager;

namespace UniversalMediaDistribution.Torrenting.MonoTorrent;

public sealed class MonoTorrentEngine : ITorrentEngine, ITorrentDownloadStorage
{
    private static readonly TimeSpan ProgressUpdateInterval =
        TimeSpan.FromMilliseconds(250);

    private sealed class NativeSession(
        NativeTorrentManager manager,
        EventHandler<TorrentStateChangedEventArgs> stateChangedHandler)
    {
        public NativeTorrentManager Manager { get; } = manager;

        public EventHandler<TorrentStateChangedEventArgs> StateChangedHandler { get; } =
            stateChangedHandler;

        public SemaphoreSlim PlaybackGate { get; } = new(1, 1);

        public IHttpStream? HttpStream { get; set; }

        public int? HttpStreamFileIndex { get; set; }

        public bool IsClosing { get; set; }

        public void DisposePlayback()
        {
            HttpStream?.Dispose();
            HttpStream = null;
            HttpStreamFileIndex = null;
        }
    }

    private readonly NativeClientEngine _engine;
    private readonly string _downloadDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<TorrentSessionId, NativeSession> _sessions = [];
    private readonly CancellationTokenSource _progressCancellation = new();
    private readonly Task _progressTask;
    private bool _disposed;

    public MonoTorrentEngine(
        string dataDirectory,
        MonoTorrentEngineOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Options = options ?? MonoTorrentEngineOptions.Default;
        Options.Validate();

        var rootDirectory = Path.GetFullPath(dataDirectory);
        _downloadDirectory = Path.Combine(rootDirectory, "downloads");
        var cacheDirectory = Path.Combine(rootDirectory, "cache");
        Directory.CreateDirectory(_downloadDirectory);
        Directory.CreateDirectory(cacheDirectory);

        _engine = new NativeClientEngine(CreateEngineSettings(cacheDirectory, Options));
        _progressTask = PumpProgressAsync(_progressCancellation.Token);
    }

    public MonoTorrentEngineOptions Options { get; }

    public TorrentEngineCapabilities Capabilities => TorrentEngineCapabilities.MonoTorrentBaseline;

    public event EventHandler<TorrentSessionChangedEventArgs>? SessionChanged;

    internal static EngineSettings CreateEngineSettings(
        string cacheDirectory,
        MonoTorrentEngineOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return new EngineSettingsBuilder
        {
            CacheDirectory = Path.GetFullPath(cacheDirectory),
            AllowedEncryption = GetAllowedEncryption(options.EncryptionPolicy),
            AllowLocalPeerDiscovery = false,
            HttpStreamingPrefix = "http://127.0.0.1:5555/",
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            FastResumeMode = options.FastResumePolicy switch
            {
                MonoTorrentFastResumePolicy.BestEffort => FastResumeMode.BestEffort,
                MonoTorrentFastResumePolicy.Accurate => FastResumeMode.Accurate,
                _ => throw new ArgumentOutOfRangeException(nameof(options))
            }
        }.ToSettings();
    }

    private static List<EncryptionType> GetAllowedEncryption(
        MonoTorrentEncryptionPolicy policy) => policy switch
        {
            MonoTorrentEncryptionPolicy.Preferred =>
            [
                EncryptionType.RC4Header,
                EncryptionType.RC4Full,
                EncryptionType.PlainText
            ],
            MonoTorrentEncryptionPolicy.Required =>
            [
                EncryptionType.RC4Header,
                EncryptionType.RC4Full
            ],
            MonoTorrentEncryptionPolicy.Disabled => [EncryptionType.PlainText],
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };

    public async ValueTask<TorrentSessionId> AddAsync(
        TorrentDescriptor descriptor,
        TorrentAddOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        TorrentPrivacyPolicy.Validate(Capabilities, descriptor, options);

        var torrentSettings = CreateTorrentSettingsBuilder(options).ToSettings();

        NativeTorrentManager manager;
        if (descriptor.MagnetUri is not null)
        {
            var magnetLink = MagnetLink.Parse(descriptor.MagnetUri);
            manager = options.PreferSequentialDownload
                ? await _engine.AddStreamingAsync(magnetLink, _downloadDirectory, torrentSettings)
                : await _engine.AddAsync(magnetLink, _downloadDirectory, torrentSettings);
        }
        else
        {
            var torrent = Torrent.Load(descriptor.TorrentMetadata.Span);
            if (options.IsPrivate)
            {
                ValidatePrivateMetadataTrackers(torrent, options);
            }

            manager = options.PreferSequentialDownload
                ? await _engine.AddStreamingAsync(torrent, _downloadDirectory, torrentSettings)
                : await _engine.AddAsync(torrent, _downloadDirectory, torrentSettings);
        }

        var sessionId = TorrentSessionId.Create();
        EventHandler<TorrentStateChangedEventArgs> handler = (_, _) => RaiseSessionChanged(sessionId, manager);
        manager.TorrentStateChanged += handler;

        await _gate.WaitAsync();
        try
        {
            _sessions.Add(sessionId, new NativeSession(manager, handler));
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await manager.StartAsync();
            cancellationToken.ThrowIfCancellationRequested();
            RaiseSessionChanged(sessionId, manager);
            return sessionId;
        }
        catch
        {
            await RemoveFailedSessionAsync(sessionId, manager, handler);
            throw;
        }
    }

    internal static TorrentSettingsBuilder CreateTorrentSettingsBuilder(
        TorrentAddOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TorrentSettingsBuilder
        {
            AllowDht = !options.IsPrivate,
            AllowPeerExchange = !options.IsPrivate,
            CreateContainingDirectory = true
        };
    }

    private static void ValidatePrivateMetadataTrackers(
        Torrent torrent,
        TorrentAddOptions options)
    {
        var approvedTrackers = options.Trackers
            .Select(static tracker => tracker.Endpoint.AbsoluteUri)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var metadataTrackers = torrent.AnnounceUrls
            .SelectMany(static tier => tier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!approvedTrackers.SetEquals(metadataTrackers))
        {
            throw new InvalidOperationException(
                "Private torrent metadata may use only the approved HTTPS trackers. " +
                "Provide an info hash or a sanitized private torrent file.");
        }
    }

    public async ValueTask<IReadOnlyList<TorrentFileEntry>> GetFilesAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(sessionId, cancellationToken);
        await WaitForMetadataAsync(manager, cancellationToken);

        return manager.Files
            .Select((file, index) => new TorrentFileEntry(
                index,
                NormalizeTorrentPath(file.Path),
                file.Length,
                MimeTypes.FromPath(file.Path)))
            .ToArray();
    }

    public async ValueTask<IMediaReadHandle> OpenReadAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(sessionId, cancellationToken);
        await WaitForMetadataAsync(manager, cancellationToken);

        if (fileIndex < 0 || fileIndex >= manager.Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }

        var streamProvider = manager.StreamProvider ??
            throw new InvalidOperationException("This torrent session was not created for streaming.");
        var stream = await streamProvider.CreateStreamAsync(
            manager.Files[fileIndex],
            prebuffer: false,
            cancellationToken);
        return new MonoTorrentMediaReadHandle(stream);
    }

    public async ValueTask<Uri?> GetPlaybackUriAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken);
        var manager = session.Manager;
        await WaitForMetadataAsync(manager, cancellationToken);
        if (fileIndex < 0 || fileIndex >= manager.Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }

        var streamProvider = manager.StreamProvider ??
            throw new InvalidOperationException("This torrent session was not created for streaming.");
        await session.PlaybackGate.WaitAsync(cancellationToken);
        try
        {
            if (session.IsClosing)
            {
                throw new InvalidOperationException(
                    $"Torrent session '{sessionId}' is closing.");
            }

            if (session.HttpStream is not null &&
                session.HttpStreamFileIndex == fileIndex)
            {
                return CreateLoopbackPlaybackUri(session.HttpStream);
            }

            session.DisposePlayback();
            session.HttpStream = await streamProvider.CreateHttpStreamAsync(
                manager.Files[fileIndex],
                prebuffer: false,
                cancellationToken);
            session.HttpStreamFileIndex = fileIndex;
            return CreateLoopbackPlaybackUri(session.HttpStream);
        }
        finally
        {
            session.PlaybackGate.Release();
        }
    }

    public async ValueTask SetFilePriorityAsync(
        TorrentSessionId sessionId,
        int fileIndex,
        TorrentFilePriority priority,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(sessionId, cancellationToken);
        await WaitForMetadataAsync(manager, cancellationToken);

        if (fileIndex < 0 || fileIndex >= manager.Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }

        var nativePriority = priority switch
        {
            TorrentFilePriority.DoNotDownload => Priority.DoNotDownload,
            TorrentFilePriority.Low => Priority.Low,
            TorrentFilePriority.Normal => Priority.Normal,
            TorrentFilePriority.High => Priority.High,
            _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null)
        };

        cancellationToken.ThrowIfCancellationRequested();
        await manager.SetFilePriorityAsync(manager.Files[fileIndex], nativePriority);
        cancellationToken.ThrowIfCancellationRequested();
        RaiseSessionChanged(sessionId, manager);
    }

    public async ValueTask PauseAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(sessionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await manager.PauseAsync();
    }

    public async ValueTask ResumeAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(sessionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await manager.StartAsync();
    }

    public async ValueTask RemoveAsync(
        TorrentSessionId sessionId,
        bool deleteData,
        CancellationToken cancellationToken = default)
    {
        var session = await TakeSessionAsync(sessionId, cancellationToken);
        session.Manager.TorrentStateChanged -= session.StateChangedHandler;
        await ClosePlaybackAsync(session);

        if (session.Manager.State != TorrentState.Stopped)
        {
            await session.Manager.StopAsync();
        }

        var mode = deleteData
            ? RemoveMode.CacheDataOnly | RemoveMode.DownloadedDataOnly
            : RemoveMode.CacheDataOnly;
        await _engine.RemoveAsync(session.Manager, mode);
    }

    public async ValueTask<TorrentSessionStorage?> GetSessionStorageAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(sessionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!manager.HasMetadata)
        {
            return null;
        }

        return new TorrentSessionStorage(
            manager.Files
                .Select(static file => new TorrentStoredFile(
                    Path.GetFullPath(file.DownloadCompleteFullPath),
                    Path.GetFullPath(file.DownloadIncompleteFullPath),
                    file.Length))
                .ToArray());
    }

    public ValueTask DeleteStoredDataAsync(
        TorrentSessionStorage storage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var managedPaths = storage.Files
            .SelectMany(static file =>
                new[] { file.CompletePath, file.IncompletePath })
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(ResolveManagedDownloadPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in managedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        var directories = managedPaths
            .Select(Path.GetDirectoryName)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static path => path.Length)
            .ToArray();
        foreach (var directory in directories)
        {
            DeleteEmptyManagedDirectories(directory, cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _progressCancellation.CancelAsync();
        try
        {
            await _progressTask;
        }
        catch (OperationCanceledException)
        {
        }

        await _engine.StopAllAsync();

        NativeSession[] sessions;
        await _gate.WaitAsync();
        try
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions)
        {
            session.Manager.TorrentStateChanged -= session.StateChangedHandler;
            await ClosePlaybackAsync(session);
        }

        _engine.Dispose();
        _progressCancellation.Dispose();
    }

    private async Task PumpProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ProgressUpdateInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                KeyValuePair<TorrentSessionId, NativeSession>[] sessions;
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    sessions = [.. _sessions];
                }
                finally
                {
                    _gate.Release();
                }

                foreach (var (sessionId, session) in sessions)
                {
                    if (!session.IsClosing)
                    {
                        try
                        {
                            RaiseSessionChanged(sessionId, session.Manager);
                        }
                        catch
                        {
                            // A transient native snapshot or UI observer
                            // failure must not stop future progress updates.
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<NativeTorrentManager> GetManagerAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken);
        return session.Manager;
    }

    private async ValueTask<NativeSession> GetSessionAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _sessions.TryGetValue(sessionId, out var session)
                ? session
                : throw new KeyNotFoundException($"Torrent session '{sessionId}' does not exist.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<NativeSession> TakeSessionAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.Remove(sessionId, out var session))
            {
                throw new KeyNotFoundException($"Torrent session '{sessionId}' does not exist.");
            }

            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveFailedSessionAsync(
        TorrentSessionId sessionId,
        NativeTorrentManager manager,
        EventHandler<TorrentStateChangedEventArgs> handler)
    {
        manager.TorrentStateChanged -= handler;
        NativeSession? failedSession = null;
        await _gate.WaitAsync();
        try
        {
            _sessions.Remove(sessionId, out failedSession);
        }
        finally
        {
            _gate.Release();
        }

        if (failedSession is not null)
        {
            await ClosePlaybackAsync(failedSession);
        }

        if (manager.State != TorrentState.Stopped)
        {
            await manager.StopAsync();
        }

        await _engine.RemoveAsync(manager, RemoveMode.CacheDataOnly);
    }

    private static async ValueTask ClosePlaybackAsync(NativeSession session)
    {
        await session.PlaybackGate.WaitAsync();
        try
        {
            session.IsClosing = true;
            session.DisposePlayback();
        }
        finally
        {
            session.PlaybackGate.Release();
        }
    }

    private static async Task WaitForMetadataAsync(
        NativeTorrentManager manager,
        CancellationToken cancellationToken)
    {
        if (manager.HasMetadata)
        {
            return;
        }

        await WaitForMetadataCoreAsync(manager).WaitAsync(cancellationToken);
    }

    private static async Task WaitForMetadataCoreAsync(NativeTorrentManager manager) =>
        await manager.WaitForMetadataAsync();

    private void RaiseSessionChanged(TorrentSessionId sessionId, NativeTorrentManager manager)
    {
        var state = manager.State.ToString() switch
        {
            "Metadata" => TorrentSessionState.Metadata,
            "Paused" or "HashingPaused" => TorrentSessionState.Paused,
            "Hashing" => TorrentSessionState.Checking,
            "Seeding" => TorrentSessionState.Seeding,
            "Stopped" when manager.Complete => TorrentSessionState.Complete,
            "Error" => TorrentSessionState.Failed,
            _ => TorrentSessionState.Downloading
        };

        var progress = CreateProgressSnapshot(manager);
        SessionChanged?.Invoke(this, new TorrentSessionChangedEventArgs(new TorrentSessionSnapshot(
            sessionId,
            state,
            manager.Monitor.DataBytesReceived,
            manager.Monitor.DataBytesSent,
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.OpenConnections,
            state is TorrentSessionState.Failed ? "monotorrent_error" : null,
            TotalBytes: progress.TotalBytes,
            WantedBytes: progress.WantedBytes,
            WantedDownloadedBytes: progress.WantedDownloadedBytes,
            Progress: progress.Progress,
            EstimatedTimeRemaining: EstimateRemaining(
                progress.WantedBytes,
                progress.WantedDownloadedBytes,
                manager.Monitor.DownloadRate),
            DisplayName: progress.DisplayName)));
    }

    private static ProgressSnapshot CreateProgressSnapshot(
        NativeTorrentManager manager)
    {
        if (!manager.HasMetadata)
        {
            return default;
        }

        var totalBytes = manager.Files.Sum(static file => file.Length);
        var wantedFiles = manager.Files
            .Where(static file => file.Priority is not Priority.DoNotDownload)
            .ToArray();
        var wantedBytes = wantedFiles.Sum(static file => file.Length);
        var wantedDownloadedBytes = wantedFiles.Sum(static file =>
            (long)Math.Round(
                file.Length *
                Math.Clamp(file.BitField.PercentComplete / 100d, 0, 1)));
        var progress = wantedBytes > 0
            ? Math.Clamp((double)wantedDownloadedBytes / wantedBytes, 0, 1)
            : (double?)null;
        return new ProgressSnapshot(
            totalBytes,
            wantedBytes,
            wantedDownloadedBytes,
            progress,
            manager.Torrent?.Name);
    }

    private static TimeSpan? EstimateRemaining(
        long? wantedBytes,
        long? wantedDownloadedBytes,
        long downloadRate)
    {
        if (wantedBytes is not > 0 ||
            wantedDownloadedBytes is null ||
            downloadRate <= 0)
        {
            return null;
        }

        var remainingBytes = Math.Max(
            0,
            wantedBytes.Value - wantedDownloadedBytes.Value);
        var seconds = Math.Min(
            TimeSpan.MaxValue.TotalSeconds,
            (double)remainingBytes / downloadRate);
        return TimeSpan.FromSeconds(seconds);
    }

    private readonly record struct ProgressSnapshot(
        long? TotalBytes,
        long? WantedBytes,
        long? WantedDownloadedBytes,
        double? Progress,
        string? DisplayName);

    private string ResolveManagedDownloadPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_downloadDirectory));
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The stored torrent file is outside the managed download directory.");
        }

        return fullPath;
    }

    private void DeleteEmptyManagedDirectories(
        string directory,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_downloadDirectory));
        var current = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory));
        while (!string.Equals(
                   current,
                   root,
                   StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, current);
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                relativePath.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The stored torrent directory is outside the managed download directory.");
            }

            if (Directory.Exists(current))
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            current = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(parent));
        }
    }

    private static string NormalizeTorrentPath(string path) => path.Replace('\\', '/');

    private static Uri CreateLoopbackPlaybackUri(IHttpStream stream)
    {
        var uri = new Uri(stream.FullUri, UriKind.Absolute);
        if (!uri.IsLoopback || !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "MonoTorrent playback must remain on the local HTTP loopback interface.");
        }

        return uri;
    }
}
