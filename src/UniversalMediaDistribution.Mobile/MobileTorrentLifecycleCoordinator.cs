using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Mobile;

public sealed class MobileTorrentLifecycleCoordinator : IAsyncDisposable
{
    private readonly ITorrentEngine _torrentEngine;
    private readonly MobileExecutionPolicy _policy;
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private readonly object _sessionGate = new();
    private readonly Dictionary<TorrentSessionId, TorrentSessionState> _sessionStates = [];
    private readonly HashSet<TorrentSessionId> _pausedByPolicy = [];
    private bool _isForeground = true;
    private MobileNetworkState _networkState = new(IsAvailable: true, IsMetered: false);
    private bool _isPowerSaving;
    private bool _disposed;

    public MobileTorrentLifecycleCoordinator(
        ITorrentEngine torrentEngine,
        MobileExecutionPolicy? policy = null)
    {
        _torrentEngine = torrentEngine ?? throw new ArgumentNullException(nameof(torrentEngine));
        _policy = policy ?? MobileExecutionPolicy.Default;
        _torrentEngine.SessionChanged += TorrentEngine_OnSessionChanged;
    }

    public async ValueTask SetForegroundAsync(
        bool isForeground,
        CancellationToken cancellationToken = default)
    {
        await _policyGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _isForeground = isForeground;
            await ApplyPolicyAsync(cancellationToken);
        }
        finally
        {
            _policyGate.Release();
        }
    }

    public async ValueTask SetNetworkStateAsync(
        MobileNetworkState state,
        CancellationToken cancellationToken = default)
    {
        await _policyGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _networkState = state;
            await ApplyPolicyAsync(cancellationToken);
        }
        finally
        {
            _policyGate.Release();
        }
    }

    public async ValueTask SetPowerSavingAsync(
        bool isPowerSaving,
        CancellationToken cancellationToken = default)
    {
        await _policyGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _isPowerSaving = isPowerSaving;
            await ApplyPolicyAsync(cancellationToken);
        }
        finally
        {
            _policyGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _policyGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _torrentEngine.SessionChanged -= TorrentEngine_OnSessionChanged;
        }
        finally
        {
            _policyGate.Release();
        }
    }

    private async ValueTask ApplyPolicyAsync(CancellationToken cancellationToken)
    {
        if (ShouldPause())
        {
            await PauseActiveSessionsAsync(cancellationToken);
        }
        else
        {
            await ResumePolicyPausedSessionsAsync(cancellationToken);
        }
    }

    private bool ShouldPause() =>
        (_policy.PauseWhenBackgrounded && !_isForeground) ||
        (_policy.PauseWhenOffline && !_networkState.IsAvailable) ||
        (_policy.PauseOnMeteredNetwork && _networkState.IsMetered) ||
        (_policy.PauseInPowerSavingMode && _isPowerSaving);

    private async ValueTask PauseActiveSessionsAsync(CancellationToken cancellationToken)
    {
        TorrentSessionId[] activeSessions;
        lock (_sessionGate)
        {
            activeSessions = _sessionStates
                .Where(static pair => pair.Value is
                    TorrentSessionState.Metadata or
                    TorrentSessionState.Downloading or
                    TorrentSessionState.Seeding)
                .Select(static pair => pair.Key)
                .Where(sessionId => !_pausedByPolicy.Contains(sessionId))
                .ToArray();
        }

        foreach (var sessionId in activeSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _torrentEngine.PauseAsync(sessionId, cancellationToken);
                _pausedByPolicy.Add(sessionId);
            }
            catch (KeyNotFoundException)
            {
                RemoveMissingSession(sessionId);
            }
            catch (ObjectDisposedException)
            {
                RemoveMissingSession(sessionId);
            }
        }
    }

    private async ValueTask ResumePolicyPausedSessionsAsync(CancellationToken cancellationToken)
    {
        var sessionsToResume = _pausedByPolicy.ToArray();
        foreach (var sessionId in sessionsToResume)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TorrentSessionState? state;
            lock (_sessionGate)
            {
                state = _sessionStates.TryGetValue(sessionId, out var currentState)
                    ? currentState
                    : null;
            }

            if (state is null or TorrentSessionState.Complete or TorrentSessionState.Failed)
            {
                _pausedByPolicy.Remove(sessionId);
                continue;
            }

            try
            {
                await _torrentEngine.ResumeAsync(sessionId, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                RemoveMissingSession(sessionId);
            }
            catch (ObjectDisposedException)
            {
                RemoveMissingSession(sessionId);
            }
            finally
            {
                _pausedByPolicy.Remove(sessionId);
            }
        }
    }

    private void TorrentEngine_OnSessionChanged(
        object? sender,
        TorrentSessionChangedEventArgs eventArgs)
    {
        var state = eventArgs.Snapshot.State;
        lock (_sessionGate)
        {
            _sessionStates[eventArgs.Snapshot.Id] = state;
        }

        if (state is
            TorrentSessionState.Metadata or
            TorrentSessionState.Downloading or
            TorrentSessionState.Seeding)
        {
            _ = ApplyPolicyAfterSessionChangeAsync();
        }
    }

    private async Task ApplyPolicyAfterSessionChangeAsync()
    {
        await _policyGate.WaitAsync();
        try
        {
            if (!_disposed && ShouldPause())
            {
                await PauseActiveSessionsAsync(CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is
            KeyNotFoundException or
            ObjectDisposedException or
            InvalidOperationException)
        {
            // A session can finish or be removed while a platform lifecycle
            // notification is being applied. The next state event reconciles it.
        }
        finally
        {
            _policyGate.Release();
        }
    }

    private void RemoveMissingSession(TorrentSessionId sessionId)
    {
        lock (_sessionGate)
        {
            _sessionStates.Remove(sessionId);
        }

        _pausedByPolicy.Remove(sessionId);
    }
}
