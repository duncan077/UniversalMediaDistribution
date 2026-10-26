using UniversalMediaDistribution.Desktop;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class DesktopShutdownCoordinatorTests
{
    [Fact]
    public async Task DisposeAsync_DisposesResourcesInOrder()
    {
        var disposalOrder = new List<string>();
        var mediaPlayer = new StubAsyncDisposable(() =>
        {
            disposalOrder.Add("media");
            return ValueTask.CompletedTask;
        });
        var torrentEngine = new StubAsyncDisposable(() =>
        {
            disposalOrder.Add("torrent");
            return ValueTask.CompletedTask;
        });

        var result = await DesktopShutdownCoordinator.DisposeAsync(
            Task.CompletedTask,
            mediaPlayer,
            torrentEngine,
            TimeSpan.FromSeconds(1));

        Assert.True(result);
        Assert.Equal(["media", "torrent"], disposalOrder);
    }

    [Fact]
    public async Task DisposeAsync_SkipsResourcesWhileUiShutdownIsRunning()
    {
        var shutdown = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaPlayer = new StubAsyncDisposable();
        var torrentEngine = new StubAsyncDisposable();

        var result = await DesktopShutdownCoordinator.DisposeAsync(
            shutdown.Task,
            mediaPlayer,
            torrentEngine,
            TimeSpan.FromSeconds(1));

        Assert.False(result);
        Assert.Equal(0, mediaPlayer.DisposeCount);
        Assert.Equal(0, torrentEngine.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_AttemptsTorrentEngineWhenMediaPlayerTimesOut()
    {
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaPlayer = new StubAsyncDisposable(
            () => new ValueTask(neverCompletes.Task));
        var torrentEngine = new StubAsyncDisposable();

        var result = await DesktopShutdownCoordinator.DisposeAsync(
            Task.CompletedTask,
            mediaPlayer,
            torrentEngine,
            TimeSpan.FromMilliseconds(50));

        // A stuck media player is still reported as a failed shutdown, but it
        // must not cost the torrent engine the flush that closes it cleanly.
        Assert.False(result);
        Assert.Equal(1, mediaPlayer.DisposeCount);
        Assert.Equal(1, torrentEngine.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_AttemptsTorrentEngineAfterMediaPlayerFailure()
    {
        var mediaPlayer = new StubAsyncDisposable(
            () => new ValueTask(Task.FromException(
                new InvalidOperationException("media failure"))));
        var torrentEngine = new StubAsyncDisposable();

        var result = await DesktopShutdownCoordinator.DisposeAsync(
            Task.CompletedTask,
            mediaPlayer,
            torrentEngine,
            TimeSpan.FromSeconds(1));

        Assert.False(result);
        Assert.Equal(1, mediaPlayer.DisposeCount);
        Assert.Equal(1, torrentEngine.DisposeCount);
    }

    private sealed class StubAsyncDisposable(
        Func<ValueTask>? dispose = null) : IAsyncDisposable
    {
        private readonly Func<ValueTask> _dispose =
            dispose ?? (() => ValueTask.CompletedTask);

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return _dispose();
        }
    }
}
