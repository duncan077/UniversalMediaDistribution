using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class NativePlaybackStartGateTests
{
    [Fact]
    public async Task WaitsUntilExternalPlayerActuallyReportsPlaying()
    {
        var states = new Queue<PlatformMediaState>(
        [
            PendingState(),
            PendingState() with { IsPlaying = true }
        ]);
        var reads = 0;

        await NativePlaybackStartGate.WaitAsync(
            _ =>
            {
                reads++;
                return ValueTask.FromResult(states.Dequeue());
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task NativeErrorRejectsHandoffSoBrowserCanBeRestored()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await NativePlaybackStartGate.WaitAsync(
                _ => ValueTask.FromResult(
                    PendingState() with
                    {
                        ErrorMessage = "decoder failed"
                    }),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None));

        Assert.Equal("decoder failed", exception.Message);
    }

    [Fact]
    public async Task ClosedExternalPlayerRejectsHandoff()
    {
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await NativePlaybackStartGate.WaitAsync(
                _ => ValueTask.FromResult(
                    PendingState() with
                    {
                        IsClosed = true
                    }),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsConfirmationWithoutClaimingSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await NativePlaybackStartGate.WaitAsync(
                _ => ValueTask.FromResult(PendingState()),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                cancellation.Token));
    }

    private static PlatformMediaState PendingState() =>
        new(
            PositionSeconds: 0,
            DurationSeconds: null,
            IsPlaying: false,
            IsEnded: false,
            IsClosed: false);
}
