namespace UniversalMediaDistribution.Torrenting;

public readonly record struct TorrentSessionId(Guid Value)
{
    public static TorrentSessionId Create() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public enum TorrentSessionState
{
    Metadata = 1,
    Downloading = 2,
    Seeding = 3,
    Paused = 4,
    Complete = 5,
    Failed = 6,
    Checking = 7
}

public sealed record TorrentSessionSnapshot(
    TorrentSessionId Id,
    TorrentSessionState State,
    long DownloadedBytes,
    long UploadedBytes,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond,
    int ConnectedPeers,
    string? ErrorCode = null,
    TorrentStorageSnapshot? Storage = null,
    long? TotalBytes = null,
    long? WantedBytes = null,
    long? WantedDownloadedBytes = null,
    double? Progress = null,
    TimeSpan? EstimatedTimeRemaining = null,
    string? DisplayName = null)
{
    public bool HasDeterminateProgress =>
        Progress is >= 0 and <= 1;
}

public sealed record TorrentStorageSnapshot(
    bool PersistentStorageSupported,
    bool PersistenceGranted,
    bool OriginPrivateFileSystemSupported,
    long? UsageBytes,
    long? QuotaBytes);

public sealed class TorrentSessionChangedEventArgs(TorrentSessionSnapshot snapshot) : EventArgs
{
    public TorrentSessionSnapshot Snapshot { get; } = snapshot;
}
