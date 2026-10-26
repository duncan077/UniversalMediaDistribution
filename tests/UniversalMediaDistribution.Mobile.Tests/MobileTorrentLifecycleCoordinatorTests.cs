using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class MobileTorrentLifecycleCoordinatorTests
{
    [Fact]
    public async Task BackgroundPausesActiveSessionsAndForegroundResumesThem()
    {
        var engine = new LifecycleTorrentEngine();
        await using var coordinator = new MobileTorrentLifecycleCoordinator(engine);
        var sessionId = engine.Publish(TorrentSessionState.Downloading);

        await coordinator.SetForegroundAsync(false);
        await coordinator.SetForegroundAsync(true);

        Assert.Equal([sessionId], engine.PausedSessions);
        Assert.Equal([sessionId], engine.ResumedSessions);
    }

    [Fact]
    public async Task OfflineStatePausesUntilConnectivityReturns()
    {
        var engine = new LifecycleTorrentEngine();
        await using var coordinator = new MobileTorrentLifecycleCoordinator(engine);
        var sessionId = engine.Publish(TorrentSessionState.Seeding);

        await coordinator.SetNetworkStateAsync(new MobileNetworkState(false, false));
        await coordinator.SetNetworkStateAsync(new MobileNetworkState(true, false));

        Assert.Equal([sessionId], engine.PausedSessions);
        Assert.Equal([sessionId], engine.ResumedSessions);
    }

    [Fact]
    public async Task DefaultPolicyAllowsMeteredConnectivity()
    {
        var engine = new LifecycleTorrentEngine();
        await using var coordinator = new MobileTorrentLifecycleCoordinator(engine);
        engine.Publish(TorrentSessionState.Downloading);

        await coordinator.SetNetworkStateAsync(new MobileNetworkState(true, true));

        Assert.Empty(engine.PausedSessions);
    }

    [Fact]
    public async Task OptInMeteredPolicyPausesAndResumes()
    {
        var engine = new LifecycleTorrentEngine();
        await using var coordinator = new MobileTorrentLifecycleCoordinator(
            engine,
            new MobileExecutionPolicy { PauseOnMeteredNetwork = true });
        var sessionId = engine.Publish(TorrentSessionState.Metadata);

        await coordinator.SetNetworkStateAsync(new MobileNetworkState(true, true));
        await coordinator.SetNetworkStateAsync(new MobileNetworkState(true, false));

        Assert.Equal([sessionId], engine.PausedSessions);
        Assert.Equal([sessionId], engine.ResumedSessions);
    }

    [Fact]
    public async Task PowerSavingPausesButDoesNotResumeManuallyPausedSession()
    {
        var engine = new LifecycleTorrentEngine();
        await using var coordinator = new MobileTorrentLifecycleCoordinator(engine);
        engine.Publish(TorrentSessionState.Paused);

        await coordinator.SetPowerSavingAsync(true);
        await coordinator.SetPowerSavingAsync(false);

        Assert.Empty(engine.PausedSessions);
        Assert.Empty(engine.ResumedSessions);
    }

    [Fact]
    public async Task SessionStartedWhileBackgrounded_IsPausedImmediately()
    {
        var engine = new LifecycleTorrentEngine();
        await using var coordinator = new MobileTorrentLifecycleCoordinator(engine);
        await coordinator.SetForegroundAsync(false);

        var sessionId = engine.Publish(TorrentSessionState.Downloading);
        var pausedSession = await engine.NextPolicyPause.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal(sessionId, pausedSession);
        Assert.Equal([sessionId], engine.PausedSessions);
    }

    [Theory]
    [InlineData(TorrentSessionState.Complete)]
    [InlineData(TorrentSessionState.Failed)]
    public async Task TerminalSessionsAreIgnored(TorrentSessionState state)
    {
        var engine = new LifecycleTorrentEngine();
        await using var coordinator = new MobileTorrentLifecycleCoordinator(engine);
        engine.Publish(state);

        await coordinator.SetForegroundAsync(false);

        Assert.Empty(engine.PausedSessions);
    }

    private sealed class LifecycleTorrentEngine : ITorrentEngine
    {
        private readonly HashSet<TorrentSessionId> _sessions = [];

        public TorrentEngineCapabilities Capabilities =>
            TorrentEngineCapabilities.MonoTorrentBaseline;

        public List<TorrentSessionId> PausedSessions { get; } = [];

        public List<TorrentSessionId> ResumedSessions { get; } = [];

        public TaskCompletionSource<TorrentSessionId> NextPolicyPause { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<TorrentSessionChangedEventArgs>? SessionChanged;

        public TorrentSessionId Publish(TorrentSessionState state)
        {
            var sessionId = TorrentSessionId.Create();
            _sessions.Add(sessionId);
            Publish(sessionId, state);
            return sessionId;
        }

        public ValueTask<TorrentSessionId> AddAsync(
            TorrentDescriptor descriptor,
            TorrentAddOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<TorrentFileEntry>> GetFilesAsync(
            TorrentSessionId sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IMediaReadHandle> OpenReadAsync(
            TorrentSessionId sessionId,
            int fileIndex,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Uri?> GetPlaybackUriAsync(
            TorrentSessionId sessionId,
            int fileIndex,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetFilePriorityAsync(
            TorrentSessionId sessionId,
            int fileIndex,
            TorrentFilePriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask PauseAsync(
            TorrentSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            AssertSession(sessionId);
            PausedSessions.Add(sessionId);
            NextPolicyPause.TrySetResult(sessionId);
            Publish(sessionId, TorrentSessionState.Paused);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(
            TorrentSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            AssertSession(sessionId);
            ResumedSessions.Add(sessionId);
            Publish(sessionId, TorrentSessionState.Downloading);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(
            TorrentSessionId sessionId,
            bool deleteData,
            CancellationToken cancellationToken = default)
        {
            _sessions.Remove(sessionId);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Publish(TorrentSessionId sessionId, TorrentSessionState state) =>
            SessionChanged?.Invoke(
                this,
                new TorrentSessionChangedEventArgs(new TorrentSessionSnapshot(
                    sessionId,
                    state,
                    0,
                    0,
                    0,
                    0,
                    0)));

        private void AssertSession(TorrentSessionId sessionId)
        {
            if (!_sessions.Contains(sessionId))
            {
                throw new KeyNotFoundException();
            }
        }
    }
}
