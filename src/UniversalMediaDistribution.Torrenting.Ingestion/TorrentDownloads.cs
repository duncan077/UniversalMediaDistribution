namespace UniversalMediaDistribution.Torrenting.Ingestion;

public enum TorrentDownloadState
{
    Adding = 1,
    FetchingMetadata = 2,
    CheckingFiles = 3,
    FindingPeers = 4,
    Downloading = 5,
    Complete = 6,
    Seeding = 7,
    Paused = 8,
    Failed = 9,
    Stopped = 10,
    Removing = 11
}

public enum TorrentSessionEndBehavior
{
    KeepDownloads = 1,
    DeleteDownloads = 2
}

public sealed record TorrentDownloadPreferences(
    TorrentSessionEndBehavior SessionEndBehavior =
        TorrentSessionEndBehavior.KeepDownloads)
{
    public TorrentDownloadPreferences Normalize() =>
        Enum.IsDefined(SessionEndBehavior)
            ? this
            : new TorrentDownloadPreferences();
}

public sealed record TorrentDownloadRecord(
    Guid Id,
    TorrentActivityId? ActivityId,
    TorrentSessionId? SessionId,
    string Title,
    string? Detail,
    TorrentDownloadState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt = null,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    long? WantedDownloadedBytes = null,
    long? WantedBytes = null,
    double? Progress = null,
    string? ErrorCode = null,
    string? StorageKey = null,
    TorrentSessionStorage? Storage = null)
{
    public bool IsActive =>
        SessionId is not null &&
        State is not (TorrentDownloadState.Complete or
            TorrentDownloadState.Failed or
            TorrentDownloadState.Stopped);

    public bool IsComplete =>
        State is TorrentDownloadState.Complete or
            TorrentDownloadState.Seeding ||
        Progress is >= 1;

    public long StoredBytes =>
        Math.Max(
            0,
            WantedDownloadedBytes ??
            (IsComplete ? WantedBytes ?? TotalBytes : null) ??
            DownloadedBytes);
}

public interface ITorrentDownloadStore
{
    ValueTask<IReadOnlyList<TorrentDownloadRecord>> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(
        TorrentDownloadRecord record,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken = default);
}

public interface ITorrentDownloadPreferencesStore
{
    ValueTask<TorrentDownloadPreferences> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        TorrentDownloadPreferences preferences,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryTorrentDownloadStore : ITorrentDownloadStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TorrentDownloadRecord> _records = [];

    public ValueTask<IReadOnlyList<TorrentDownloadRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<TorrentDownloadRecord>>(
                _records.Values
                    .OrderByDescending(static record => record.UpdatedAt)
                    .ToArray());
        }
    }

    public ValueTask UpsertAsync(
        TorrentDownloadRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _records[record.Id] = record;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var recordId in recordIds)
            {
                _records.Remove(recordId);
            }
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryTorrentDownloadPreferencesStore(
    TorrentDownloadPreferences? initialPreferences = null) :
    ITorrentDownloadPreferencesStore
{
    private readonly Lock _gate = new();
    private TorrentDownloadPreferences _preferences =
        (initialPreferences ?? new TorrentDownloadPreferences()).Normalize();

    public ValueTask<TorrentDownloadPreferences> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_preferences);
        }
    }

    public ValueTask SaveAsync(
        TorrentDownloadPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _preferences = preferences.Normalize();
        }

        return ValueTask.CompletedTask;
    }
}
