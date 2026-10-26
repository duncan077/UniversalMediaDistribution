using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class PlaybackProgressMonitorTests
{
    [Fact]
    public async Task ReplayContinuesSamplesAndSavesWithoutRepeatingCompletion()
    {
        PlatformMediaState[] states =
        [
            new(25, 100, true, false, false),
            new(100, 100, false, true, false),
            new(100, 100, false, true, false),
            new(0, 100, true, false, false),
            new(12, 100, true, false, false),
            new(12, 100, false, false, true)
        ];
        var samples = new List<PlatformMediaState>();
        var saves = new List<MediaProgressState>();
        var index = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await PlaybackProgressMonitor.RunAsync(
            _ => ValueTask.FromResult(states[index++]),
            (state, _) => { samples.Add(state); return Task.CompletedTask; },
            (progress, _) => { saves.Add(progress); return ValueTask.CompletedTask; },
            () => false, timeout.Token, TimeSpan.FromMilliseconds(1));
        Assert.Equal(states, samples);
        Assert.Equal(new double[] { 25, 0, 0, 12, 12 }, saves.Select(x => x.Position));
        Assert.False(saves[0].IsCompleted);
        Assert.All(saves.Skip(1), saved => Assert.True(saved.IsCompleted));
    }

    [Fact]
    public async Task CancellationStopsAnEndedPlayerWithoutAnotherSave()
    {
        using var cancellation = new CancellationTokenSource();
        var samples = 0;
        var saves = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlaybackProgressMonitor.RunAsync(
            _ => ValueTask.FromResult(new PlatformMediaState(50, 50, false, true, false)),
            (_, _) => { if (++samples == 3) cancellation.Cancel(); return Task.CompletedTask; },
            (_, _) => { saves++; return ValueTask.CompletedTask; },
            () => false, cancellation.Token, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(3, samples);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task WatchingAnAlreadySeenTitleKeepsItsSeenFlag()
    {
        MediaProgressState? saved = null;
        await PlaybackProgressMonitor.RunAsync(
            _ => ValueTask.FromResult(new PlatformMediaState(10, 100, false, false, true)),
            (_, _) => Task.CompletedTask,
            (progress, _) => { saved = progress; return ValueTask.CompletedTask; },
            () => true, CancellationToken.None, TimeSpan.FromMilliseconds(1));
        Assert.NotNull(saved);
        Assert.True(saved.IsCompleted);
        Assert.Equal(10, saved.Position);
    }
}
