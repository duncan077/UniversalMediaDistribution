using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class TorrentDownloadRecordChangedEventArgs(
    TorrentDownloadRecord record,
    bool wasRemoved = false) : EventArgs
{
    public TorrentDownloadRecord Record { get; } = record;

    public bool WasRemoved { get; } = wasRemoved;
}

public sealed class TorrentDownloadLibraryErrorEventArgs(Exception exception) :
    EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed class TorrentDownloadLibrary : IAsyncDisposable
{
    private static readonly TimeSpan PersistenceDebounce =
        TimeSpan.FromMilliseconds(350);

    private readonly TorrentActivityCoordinator _activityCoordinator;
    private readonly ITorrentDownloadStore _store;
    private readonly ITorrentDownloadPreferencesStore _preferencesStore;
    private readonly ITorrentDownloadStorage? _downloadStorage;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TorrentDownloadRecord> _records = [];
    private readonly Dictionary<Guid, TorrentDownloadRecord> _pendingUpserts = [];
    private readonly HashSet<Guid> _storageRequests = [];
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly Channel<bool> _persistenceSignals =
        Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            });
    private readonly CancellationTokenSource _persistenceCancellation = new();
    private readonly Task _persistenceTask;
    private bool _initialized;
    private bool _shuttingDown;
    private bool _disposed;

    public TorrentDownloadLibrary(
        TorrentActivityCoordinator activityCoordinator,
        ITorrentDownloadStore store,
        ITorrentDownloadPreferencesStore preferencesStore,
        ITorrentDownloadStorage? downloadStorage = null)
    {
        _activityCoordinator = activityCoordinator ??
            throw new ArgumentNullException(nameof(activityCoordinator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _preferencesStore = preferencesStore ??
            throw new ArgumentNullException(nameof(preferencesStore));
        _downloadStorage = downloadStorage;
        _activityCoordinator.ActivityChanged +=
            ActivityCoordinator_OnActivityChanged;
        _persistenceTask = PersistLoopAsync(_persistenceCancellation.Token);
    }

    public event EventHandler<TorrentDownloadRecordChangedEventArgs>?
        RecordChanged;

    public event EventHandler<TorrentDownloadLibraryErrorEventArgs>?
        PersistenceFailed;

    public IReadOnlyList<TorrentDownloadRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.Values
                    .OrderByDescending(static record => record.IsActive)
                    .ThenByDescending(static record => record.UpdatedAt)
                    .ToArray();
            }
        }
    }

    public async ValueTask InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        var records = await _store.LoadAsync(cancellationToken);
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            foreach (var loaded in records)
            {
                var normalized = NormalizeLoadedRecord(loaded);
                _records[normalized.Id] = normalized;
                if (!Equals(normalized, loaded))
                {
                    QueuePersistenceLocked(normalized);
                }
            }

            _initialized = true;
        }

        foreach (var activity in _activityCoordinator.Activities)
        {
            ApplyActivity(activity, wasRemoved: false);
        }
    }

    public ValueTask<TorrentDownloadPreferences> LoadPreferencesAsync(
        CancellationToken cancellationToken = default) =>
        _preferencesStore.LoadAsync(cancellationToken);

    public ValueTask SavePreferencesAsync(
        TorrentDownloadPreferences preferences,
        CancellationToken cancellationToken = default) =>
        _preferencesStore.SaveAsync(preferences.Normalize(), cancellationToken);

    public ValueTask PauseAsync(
        Guid recordId,
        CancellationToken cancellationToken = default) =>
        InvokeActivityAsync(
            recordId,
            static (coordinator, activityId, token) =>
                coordinator.PauseAsync(activityId, token),
            cancellationToken);

    public ValueTask ResumeAsync(
        Guid recordId,
        CancellationToken cancellationToken = default) =>
        InvokeActivityAsync(
            recordId,
            static (coordinator, activityId, token) =>
                coordinator.ResumeAsync(activityId, token),
            cancellationToken);

    public async ValueTask DeleteAsync(
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var errors = new List<Exception>();
        foreach (var recordId in recordIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            TorrentDownloadRecord? record;
            lock (_gate)
            {
                _records.TryGetValue(recordId, out record);
            }

            if (record is null)
            {
                continue;
            }

            try
            {
                await DeleteRecordDataAsync(record, cancellationToken);
                await RemoveRecordAsync(record, cancellationToken);
            }
            catch (Exception exception)
            {
                errors.Add(new InvalidOperationException(
                    $"'{record.Title}' could not be deleted.",
                    exception));
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "One or more downloads could not be deleted.",
                errors);
        }
    }

    public async ValueTask ShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed || _shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _activityCoordinator.ActivityChanged -=
            ActivityCoordinator_OnActivityChanged;

        var preferences = await _preferencesStore.LoadAsync(cancellationToken);
        if (preferences.Normalize().SessionEndBehavior is
            TorrentSessionEndBehavior.DeleteDownloads)
        {
            Guid[] activeRecordIds;
            lock (_gate)
            {
                activeRecordIds = _records.Values
                    .Where(static record => record.SessionId is not null)
                    .Select(static record => record.Id)
                    .ToArray();
            }

            await DeleteAsync(activeRecordIds, cancellationToken);
        }
        else
        {
            TorrentDownloadRecord[] stoppedRecords;
            lock (_gate)
            {
                stoppedRecords = _records.Values
                    .Where(static record => record.SessionId is not null)
                    .Select(static record => NormalizeEndedSession(record))
                    .ToArray();
                foreach (var record in stoppedRecords)
                {
                    _records[record.Id] = record;
                    QueuePersistenceLocked(record);
                }
            }

            foreach (var record in stoppedRecords)
            {
                RaiseRecordChanged(record);
            }
        }

        await FlushPendingAsync(cancellationToken);
        _persistenceSignals.Writer.TryComplete();
        await _persistenceCancellation.CancelAsync();
        try
        {
            await _persistenceTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await ShutdownAsync();
        _disposed = true;
        _persistenceCancellation.Dispose();
        _storeGate.Dispose();
    }

    private void ActivityCoordinator_OnActivityChanged(
        object? sender,
        TorrentActivityChangedEventArgs eventArgs)
    {
        if (!_shuttingDown)
        {
            ApplyActivity(eventArgs.Snapshot, eventArgs.WasRemoved);
        }
    }

    private void ApplyActivity(
        TorrentActivitySnapshot activity,
        bool wasRemoved)
    {
        TorrentDownloadRecord? removedTransient = null;
        TorrentDownloadRecord? removedEmpty = null;
        TorrentDownloadRecord updated;
        var requestStorage = false;
        lock (_gate)
        {
            var byActivity = _records.Values.FirstOrDefault(record =>
                record.ActivityId == activity.Id);
            var bySession = activity.SessionId is { } sessionId
                ? _records.Values.FirstOrDefault(record =>
                    record.SessionId == sessionId)
                : null;
            var existing = bySession ?? byActivity;
            if (wasRemoved &&
                existing is not null &&
                activity.SessionId is null &&
                activity.DownloadedBytes == 0 &&
                activity.WantedDownloadedBytes is null or 0 &&
                existing.Storage is null)
            {
                _records.Remove(existing.Id);
                _pendingUpserts.Remove(existing.Id);
                _storageRequests.Remove(existing.Id);
                removedEmpty = existing;
                updated = existing;
            }
            else
            {
                if (bySession is not null &&
                    byActivity is not null &&
                    bySession.Id != byActivity.Id)
                {
                    _records.Remove(byActivity.Id);
                    _pendingUpserts.Remove(byActivity.Id);
                    removedTransient = byActivity;
                }

                updated = CreateRecord(activity, existing, wasRemoved);
                _records[updated.Id] = updated;
                QueuePersistenceLocked(updated);
                requestStorage =
                    !wasRemoved &&
                    updated.SessionId is not null &&
                    updated.TotalBytes is not null &&
                    updated.Storage is null &&
                    _downloadStorage is not null &&
                    _storageRequests.Add(updated.Id);
            }
        }

        if (removedEmpty is not null)
        {
            _ = DeletePersistedRecordOnlyAsync(removedEmpty);
            RaiseRecordChanged(removedEmpty, wasRemoved: true);
            return;
        }

        if (removedTransient is not null)
        {
            _ = DeletePersistedRecordOnlyAsync(removedTransient);
            RaiseRecordChanged(removedTransient, wasRemoved: true);
        }

        RaiseRecordChanged(updated);
        if (requestStorage)
        {
            _ = ResolveStorageAsync(updated.Id);
        }
    }

    private static TorrentDownloadRecord CreateRecord(
        TorrentActivitySnapshot activity,
        TorrentDownloadRecord? existing,
        bool wasRemoved)
    {
        var state = MapState(activity.Phase);
        var sessionId = activity.SessionId;
        var activityId = (TorrentActivityId?)activity.Id;
        if (wasRemoved)
        {
            activityId = null;
            if (state is TorrentDownloadState.Removing)
            {
                sessionId = null;
                state = TorrentDownloadState.Stopped;
            }
        }

        var completedAt =
            state is TorrentDownloadState.Complete or
                TorrentDownloadState.Seeding
                ? existing?.CompletedAt ?? activity.UpdatedAt
                : existing?.CompletedAt;
        return new TorrentDownloadRecord(
            existing?.Id ?? activity.Id.Value,
            activityId,
            sessionId,
            string.IsNullOrWhiteSpace(activity.Title)
                ? existing?.Title ?? "Torrent"
                : activity.Title.Trim(),
            NormalizeOptionalText(activity.Detail) ?? existing?.Detail,
            state,
            existing?.CreatedAt ?? activity.StartedAt,
            activity.UpdatedAt,
            completedAt,
            activity.DownloadedBytes,
            activity.TotalBytes,
            activity.WantedDownloadedBytes,
            activity.WantedBytes,
            NormalizeProgress(activity.Progress),
            NormalizeOptionalText(activity.ErrorCode),
            existing?.StorageKey,
            existing?.Storage);
    }

    private async Task ResolveStorageAsync(Guid recordId)
    {
        try
        {
            TorrentDownloadRecord? current;
            lock (_gate)
            {
                _records.TryGetValue(recordId, out current);
            }

            if (current?.SessionId is not { } sessionId ||
                _downloadStorage is null)
            {
                return;
            }

            var storage = await _downloadStorage.GetSessionStorageAsync(
                sessionId);
            if (storage is null || storage.Files.Count == 0)
            {
                return;
            }

            var storageKey = CreateStorageKey(storage);
            TorrentDownloadRecord? duplicate = null;
            TorrentDownloadRecord updated;
            lock (_gate)
            {
                if (!_records.TryGetValue(recordId, out current))
                {
                    return;
                }

                duplicate = _records.Values.FirstOrDefault(record =>
                    record.Id != recordId &&
                    string.Equals(
                        record.StorageKey,
                        storageKey,
                        StringComparison.Ordinal));
                if (duplicate is not null)
                {
                    _records.Remove(duplicate.Id);
                    _pendingUpserts.Remove(duplicate.Id);
                }

                updated = current with
                {
                    StorageKey = storageKey,
                    Storage = storage,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _records[updated.Id] = updated;
                QueuePersistenceLocked(updated);
            }

            if (duplicate is not null)
            {
                await DeletePersistedRecordOnlyAsync(duplicate);
                RaiseRecordChanged(duplicate, wasRemoved: true);
            }

            RaiseRecordChanged(updated);
        }
        catch (Exception exception)
        {
            RaisePersistenceFailed(exception);
        }
        finally
        {
            lock (_gate)
            {
                _storageRequests.Remove(recordId);
            }
        }
    }

    private async ValueTask DeleteRecordDataAsync(
        TorrentDownloadRecord record,
        CancellationToken cancellationToken)
    {
        if (record.ActivityId is { } activityId &&
            record.SessionId is not null)
        {
            await _activityCoordinator.CancelAsync(
                activityId,
                deleteData: true,
                cancellationToken);
            return;
        }

        if (record.Storage is { Files.Count: > 0 } storage &&
            _downloadStorage is not null)
        {
            await _downloadStorage.DeleteStoredDataAsync(
                storage,
                cancellationToken);
            return;
        }

        if (record.StoredBytes > 0)
        {
            throw new InvalidOperationException(
                "The app no longer has a safe path to the downloaded files.");
        }
    }

    private async ValueTask RemoveRecordAsync(
        TorrentDownloadRecord record,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _records.Remove(record.Id);
            _pendingUpserts.Remove(record.Id);
            _storageRequests.Remove(record.Id);
        }

        await _storeGate.WaitAsync(cancellationToken);
        try
        {
            await _store.DeleteAsync([record.Id], cancellationToken);
        }
        finally
        {
            _storeGate.Release();
        }

        RaiseRecordChanged(record, wasRemoved: true);
    }

    private async Task DeletePersistedRecordOnlyAsync(
        TorrentDownloadRecord record)
    {
        try
        {
            await _storeGate.WaitAsync();
            try
            {
                await _store.DeleteAsync([record.Id]);
            }
            finally
            {
                _storeGate.Release();
            }
        }
        catch (Exception exception)
        {
            RaisePersistenceFailed(exception);
        }
    }

    private async ValueTask InvokeActivityAsync(
        Guid recordId,
        Func<
            TorrentActivityCoordinator,
            TorrentActivityId,
            CancellationToken,
            ValueTask> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TorrentActivityId activityId;
        lock (_gate)
        {
            activityId = _records.TryGetValue(recordId, out var record) &&
                         record.ActivityId is { } activeId
                ? activeId
                : throw new InvalidOperationException(
                    "This download is not active in the current session.");
        }

        await action(
            _activityCoordinator,
            activityId,
            cancellationToken);
    }

    private void QueuePersistenceLocked(TorrentDownloadRecord record)
    {
        _pendingUpserts[record.Id] = record;
        _persistenceSignals.Writer.TryWrite(true);
    }

    private async Task PersistLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _persistenceSignals.Reader.WaitToReadAsync(
                       cancellationToken))
            {
                while (_persistenceSignals.Reader.TryRead(out _))
                {
                }

                await Task.Delay(PersistenceDebounce, cancellationToken);
                await FlushPendingAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask FlushPendingAsync(
        CancellationToken cancellationToken)
    {
        TorrentDownloadRecord[] pending;
        lock (_gate)
        {
            pending = _pendingUpserts.Values.ToArray();
            _pendingUpserts.Clear();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await _storeGate.WaitAsync(cancellationToken);
            try
            {
                foreach (var candidate in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TorrentDownloadRecord? current;
                    lock (_gate)
                    {
                        _records.TryGetValue(candidate.Id, out current);
                    }

                    if (current is not null)
                    {
                        await _store.UpsertAsync(current, cancellationToken);
                    }
                }
            }
            finally
            {
                _storeGate.Release();
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                foreach (var candidate in pending)
                {
                    if (_records.TryGetValue(candidate.Id, out var current))
                    {
                        _pendingUpserts[candidate.Id] = current;
                    }
                }
            }

            RaisePersistenceFailed(exception);
        }
    }

    private static TorrentDownloadRecord NormalizeLoadedRecord(
        TorrentDownloadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var title = string.IsNullOrWhiteSpace(record.Title)
            ? "Torrent"
            : record.Title.Trim();
        var state = record.State;
        if (record.SessionId is not null &&
            state is not (TorrentDownloadState.Complete or
                TorrentDownloadState.Seeding or
                TorrentDownloadState.Failed))
        {
            state = TorrentDownloadState.Stopped;
        }

        return record with
        {
            ActivityId = null,
            SessionId = null,
            Title = title,
            Detail = NormalizeOptionalText(record.Detail),
            State = state,
            DownloadedBytes = Math.Max(0, record.DownloadedBytes),
            TotalBytes = NormalizeBytes(record.TotalBytes),
            WantedDownloadedBytes =
                NormalizeBytes(record.WantedDownloadedBytes),
            WantedBytes = NormalizeBytes(record.WantedBytes),
            Progress = NormalizeProgress(record.Progress),
            ErrorCode = NormalizeOptionalText(record.ErrorCode),
            StorageKey = NormalizeOptionalText(record.StorageKey)
        };
    }

    private static TorrentDownloadRecord NormalizeEndedSession(
        TorrentDownloadRecord record) =>
        record with
        {
            ActivityId = null,
            SessionId = null,
            State = record.IsComplete
                ? TorrentDownloadState.Complete
                : record.State is TorrentDownloadState.Failed
                    ? TorrentDownloadState.Failed
                    : TorrentDownloadState.Stopped,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static TorrentDownloadState MapState(
        TorrentActivityPhase phase) => phase switch
        {
            TorrentActivityPhase.Adding => TorrentDownloadState.Adding,
            TorrentActivityPhase.FetchingMetadata =>
                TorrentDownloadState.FetchingMetadata,
            TorrentActivityPhase.CheckingFiles =>
                TorrentDownloadState.CheckingFiles,
            TorrentActivityPhase.FindingPeers =>
                TorrentDownloadState.FindingPeers,
            TorrentActivityPhase.Downloading =>
                TorrentDownloadState.Downloading,
            TorrentActivityPhase.Ready => TorrentDownloadState.Complete,
            TorrentActivityPhase.Seeding => TorrentDownloadState.Seeding,
            TorrentActivityPhase.Paused => TorrentDownloadState.Paused,
            TorrentActivityPhase.Failed => TorrentDownloadState.Failed,
            TorrentActivityPhase.Removing => TorrentDownloadState.Removing,
            _ => TorrentDownloadState.Stopped
        };

    private static string CreateStorageKey(TorrentSessionStorage storage)
    {
        var value = string.Join(
            '\n',
            storage.Files
                .Select(static file =>
                    $"{Path.GetFullPath(file.CompletePath)}\0" +
                    $"{Path.GetFullPath(file.IncompletePath)}\0" +
                    $"{file.Length}")
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static long? NormalizeBytes(long? value) =>
        value is >= 0 ? value : null;

    private static double? NormalizeProgress(double? value) =>
        value is >= 0 and <= 1 && double.IsFinite(value.Value)
            ? value
            : null;

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RaiseRecordChanged(
        TorrentDownloadRecord record,
        bool wasRemoved = false)
    {
        try
        {
            RecordChanged?.Invoke(
                this,
                new TorrentDownloadRecordChangedEventArgs(
                    record,
                    wasRemoved));
        }
        catch
        {
            // Download reporting must not interrupt the torrent engine.
        }
    }

    private void RaisePersistenceFailed(Exception exception)
    {
        try
        {
            PersistenceFailed?.Invoke(
                this,
                new TorrentDownloadLibraryErrorEventArgs(exception));
        }
        catch
        {
            // A status observer must not interrupt persistence.
        }
    }
}
