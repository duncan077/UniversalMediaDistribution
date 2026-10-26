using LibVLCSharp.Shared;
using UniversalMediaDistribution.Playback.LibVLC;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class LibVlcPlaybackStateMonitorTests
{
    [Fact]
    public async Task WaitForStartAsync_PollsUntilPlaybackStarts()
    {
        var states = new Queue<VLCState>(
        [
            VLCState.NothingSpecial,
            VLCState.Opening,
            VLCState.Buffering,
            VLCState.Playing
        ]);

        var result = await LibVlcPlaybackStateMonitor.WaitForStartAsync(
            () => states.Dequeue(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(VLCState.Playing, result);
        Assert.Empty(states);
    }

    [Theory]
    [InlineData(VLCState.Paused)]
    [InlineData(VLCState.Ended)]
    [InlineData(VLCState.Error)]
    public async Task WaitForStartAsync_ReturnsTerminalStartState(
        VLCState state)
    {
        var result = await LibVlcPlaybackStateMonitor.WaitForStartAsync(
            () => state,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(state, result);
    }

    [Fact]
    public async Task WaitForStartAsync_ReportsLastStateOnTimeout()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => LibVlcPlaybackStateMonitor.WaitForStartAsync(
                () => VLCState.Buffering,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None));

        Assert.Contains(
            nameof(VLCState.Buffering),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForStartAsync_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => LibVlcPlaybackStateMonitor.WaitForStartAsync(
                () => VLCState.Opening,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }
}
