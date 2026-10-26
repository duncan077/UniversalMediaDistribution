using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public readonly record struct TorrentActivityId(Guid Value)
{
    public static TorrentActivityId Create() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public enum TorrentActivityPhase
{
    Adding = 1,
    FetchingMetadata = 2,
    CheckingFiles = 3,
    FindingPeers = 4,
    Downloading = 5,
    Ready = 6,
    Seeding = 7,
    Paused = 8,
    Failed = 9,
    Removing = 10
}

public sealed record TorrentActivitySnapshot(
    TorrentActivityId Id,
    TorrentSessionId? SessionId,
    string Title,
    string? Detail,
    TorrentActivityPhase Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    long? WantedDownloadedBytes = null,
    long? WantedBytes = null,
    long DownloadBytesPerSecond = 0,
    int ConnectedPeers = 0,
    double? Progress = null,
    TimeSpan? EstimatedTimeRemaining = null,
    string? TorrentName = null,
    string? ErrorCode = null)
{
    public bool IsIndeterminate =>
        Progress is not (>= 0 and <= 1);

    public bool CanPause =>
        SessionId is not null &&
        Phase is (TorrentActivityPhase.FetchingMetadata or
            TorrentActivityPhase.CheckingFiles or
            TorrentActivityPhase.FindingPeers or
            TorrentActivityPhase.Downloading or
            TorrentActivityPhase.Seeding);

    public bool CanResume =>
        SessionId is not null &&
        Phase is TorrentActivityPhase.Paused;

    public bool CanCancel =>
        SessionId is not null &&
        Phase is not TorrentActivityPhase.Removing;
}

public sealed class TorrentActivityChangedEventArgs(
    TorrentActivitySnapshot snapshot,
    bool wasRemoved = false) : EventArgs
{
    public TorrentActivitySnapshot Snapshot { get; } = snapshot;

    public bool WasRemoved { get; } = wasRemoved;
}

public sealed class TorrentActivityCoordinator : IDisposable
{
    private readonly ITorrentEngine? _engine;
    private readonly Lock _gate = new();
    private readonly Dictionary<TorrentActivityId, TorrentActivitySnapshot>
        _activities = [];
    private readonly Dictionary<TorrentSessionId, TorrentActivityId>
        _activitiesBySession = [];
    private readonly Dictionary<TorrentSessionId, TorrentSessionSnapshot>
        _latestSessions = [];
    private bool _disposed;

    public TorrentActivityCoordinator(ITorrentEngine? engine)
    {
        _engine = engine;
        if (_engine is not null)
        {
            _engine.SessionChanged += Engine_OnSessionChanged;
        }
    }

    public event EventHandler<TorrentActivityChangedEventArgs>? ActivityChanged;

    public IReadOnlyList<TorrentActivitySnapshot> Activities
    {
        get
        {
            lock (_gate)
            {
                return _activities.Values
                    .OrderByDescending(static activity => activity.StartedAt)
                    .ToArray();
            }
        }
    }

    public TorrentActivityId Begin(string title, string? detail = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var now = DateTimeOffset.UtcNow;
        var snapshot = new TorrentActivitySnapshot(
            TorrentActivityId.Create(),
            SessionId: null,
            title.Trim(),
            NormalizeOptionalText(detail),
            TorrentActivityPhase.Adding,
            now,
            now);
        lock (_gate)
        {
            _activities.Add(snapshot.Id, snapshot);
        }

        RaiseActivityChanged(snapshot);
        return snapshot.Id;
    }

    public TorrentActivityId AttachSession(
        TorrentActivityId activityId,
        TorrentSessionId sessionId,
        string? title = null,
        string? detail = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A torrent session identifier is required.",
                nameof(sessionId));
        }

        TorrentActivitySnapshot? removed = null;
        TorrentActivitySnapshot updated;
        lock (_gate)
        {
            if (!_activities.TryGetValue(activityId, out var pending))
            {
                throw new KeyNotFoundException(
                    $"Torrent activity '{activityId}' does not exist.");
            }

            if (_activitiesBySession.TryGetValue(
                    sessionId,
                    out var existingActivityId) &&
                existingActivityId != activityId &&
                _activities.TryGetValue(existingActivityId, out var existing))
            {
                _activities.Remove(existingActivityId);
                removed = existing;
                updated = pending with
                {
                    SessionId = sessionId,
                    Title = NormalizeOptionalText(title) ?? pending.Title,
                    Detail = NormalizeOptionalText(detail) ?? pending.Detail,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _activitiesBySession[sessionId] = activityId;
            }
            else
            {
                updated = pending with
                {
                    SessionId = sessionId,
                    Title = NormalizeOptionalText(title) ?? pending.Title,
                    Detail = NormalizeOptionalText(detail) ?? pending.Detail,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _activitiesBySession[sessionId] = activityId;
            }

            if (_latestSessions.TryGetValue(sessionId, out var session))
            {
                updated = ApplySessionSnapshot(updated, session);
            }

            _activities[activityId] = updated;
        }

        if (removed is not null)
        {
            RaiseActivityChanged(removed, wasRemoved: true);
        }

        RaiseActivityChanged(updated);
        return activityId;
    }

    public void UpdateDetail(TorrentActivityId activityId, string? detail)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TorrentActivitySnapshot updated;
        lock (_gate)
        {
            if (!_activities.TryGetValue(activityId, out var current))
            {
                return;
            }

            updated = current with
            {
                Detail = NormalizeOptionalText(detail),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _activities[activityId] = updated;
        }

        RaiseActivityChanged(updated);
    }

    public void MarkReady(TorrentActivityId activityId, string? detail = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdatePhase(
            activityId,
            TorrentActivityPhase.Ready,
            detail,
            errorCode: null);
    }

    public void MarkFailed(
        TorrentActivityId activityId,
        string error,
        string? errorCode = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        UpdatePhase(
            activityId,
            TorrentActivityPhase.Failed,
            error,
            errorCode ?? "torrent_activity_failed");
    }

    public void Dismiss(TorrentActivityId activityId)
    {
        TorrentActivitySnapshot? removed;
        lock (_gate)
        {
            if (!_activities.Remove(activityId, out removed))
            {
                return;
            }

            if (removed.SessionId is { } sessionId &&
                _activitiesBySession.GetValueOrDefault(sessionId) == activityId)
            {
                _activitiesBySession.Remove(sessionId);
            }
        }

        RaiseActivityChanged(removed, wasRemoved: true);
    }

    public ValueTask PauseAsync(
        TorrentActivityId activityId,
        CancellationToken cancellationToken = default) =>
        InvokeForSessionAsync(
            activityId,
            static (engine, sessionId, token) =>
                engine.PauseAsync(sessionId, token),
            cancellationToken);

    public ValueTask ResumeAsync(
        TorrentActivityId activityId,
        CancellationToken cancellationToken = default) =>
        InvokeForSessionAsync(
            activityId,
            static (engine, sessionId, token) =>
                engine.ResumeAsync(sessionId, token),
            cancellationToken);

    public async ValueTask CancelAsync(
        TorrentActivityId activityId,
        bool deleteData,
        CancellationToken cancellationToken = default)
    {
        var sessionId = GetRequiredSession(activityId);
        UpdatePhase(
            activityId,
            TorrentActivityPhase.Removing,
            detail: null,
            errorCode: null);
        try
        {
            await RequireEngine().RemoveAsync(
                sessionId,
                deleteData,
                cancellationToken);
            Dismiss(activityId);
        }
        catch
        {
            UpdatePhase(
                activityId,
                TorrentActivityPhase.Failed,
                "The torrent could not be removed.",
                "torrent_remove_failed");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_engine is not null)
        {
            _engine.SessionChanged -= Engine_OnSessionChanged;
        }

        lock (_gate)
        {
            _activities.Clear();
            _activitiesBySession.Clear();
            _latestSessions.Clear();
        }
    }

    private async ValueTask InvokeForSessionAsync(
        TorrentActivityId activityId,
        Func<
            ITorrentEngine,
            TorrentSessionId,
            CancellationToken,
            ValueTask> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sessionId = GetRequiredSession(activityId);
        await action(RequireEngine(), sessionId, cancellationToken);
    }

    private TorrentSessionId GetRequiredSession(
        TorrentActivityId activityId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            return _activities.TryGetValue(activityId, out var activity) &&
                   activity.SessionId is { } sessionId
                ? sessionId
                : throw new InvalidOperationException(
                    "This torrent activity is not attached to a session.");
        }
    }

    private ITorrentEngine RequireEngine() =>
        _engine ?? throw new InvalidOperationException(
            "This platform has no torrent engine configured.");

    private void Engine_OnSessionChanged(
        object? sender,
        TorrentSessionChangedEventArgs eventArgs)
    {
        TorrentActivitySnapshot? updated = null;
        lock (_gate)
        {
            _latestSessions[eventArgs.Snapshot.Id] = eventArgs.Snapshot;
            if (_activitiesBySession.TryGetValue(
                    eventArgs.Snapshot.Id,
                    out var activityId) &&
                _activities.TryGetValue(activityId, out var current))
            {
                updated = ApplySessionSnapshot(current, eventArgs.Snapshot);
                _activities[activityId] = updated;
            }
        }

        if (updated is not null)
        {
            RaiseActivityChanged(updated);
        }
    }

    private void UpdatePhase(
        TorrentActivityId activityId,
        TorrentActivityPhase phase,
        string? detail,
        string? errorCode)
    {
        TorrentActivitySnapshot updated;
        lock (_gate)
        {
            if (!_activities.TryGetValue(activityId, out var current))
            {
                return;
            }

            updated = current with
            {
                Phase = phase,
                Detail = NormalizeOptionalText(detail) ?? current.Detail,
                ErrorCode = errorCode,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _activities[activityId] = updated;
        }

        RaiseActivityChanged(updated);
    }

    private static TorrentActivitySnapshot ApplySessionSnapshot(
        TorrentActivitySnapshot activity,
        TorrentSessionSnapshot session)
    {
        var phase = session.State switch
        {
            TorrentSessionState.Metadata =>
                TorrentActivityPhase.FetchingMetadata,
            TorrentSessionState.Checking =>
                TorrentActivityPhase.CheckingFiles,
            TorrentSessionState.Paused =>
                TorrentActivityPhase.Paused,
            TorrentSessionState.Failed =>
                TorrentActivityPhase.Failed,
            TorrentSessionState.Seeding =>
                TorrentActivityPhase.Seeding,
            TorrentSessionState.Complete =>
                TorrentActivityPhase.Ready,
            TorrentSessionState.Downloading
                when session.Progress is >= 1 =>
                    TorrentActivityPhase.Ready,
            TorrentSessionState.Downloading
                when session.ConnectedPeers == 0 &&
                     session.DownloadBytesPerSecond == 0 =>
                    TorrentActivityPhase.FindingPeers,
            _ => TorrentActivityPhase.Downloading
        };

        return activity with
        {
            SessionId = session.Id,
            Phase = phase,
            UpdatedAt = DateTimeOffset.UtcNow,
            DownloadedBytes = session.DownloadedBytes,
            TotalBytes = session.TotalBytes,
            WantedDownloadedBytes = session.WantedDownloadedBytes,
            WantedBytes = session.WantedBytes,
            DownloadBytesPerSecond = session.DownloadBytesPerSecond,
            ConnectedPeers = session.ConnectedPeers,
            Progress = NormalizeProgress(session.Progress),
            EstimatedTimeRemaining = session.EstimatedTimeRemaining,
            TorrentName = NormalizeOptionalText(session.DisplayName),
            ErrorCode = session.ErrorCode
        };
    }

    private static double? NormalizeProgress(double? value) =>
        value is >= 0 and <= 1 && double.IsFinite(value.Value)
            ? value
            : null;

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RaiseActivityChanged(
        TorrentActivitySnapshot snapshot,
        bool wasRemoved = false)
    {
        try
        {
            ActivityChanged?.Invoke(
                this,
                new TorrentActivityChangedEventArgs(snapshot, wasRemoved));
        }
        catch
        {
            // Progress reporting must never interrupt the torrent engine.
        }
    }
}
