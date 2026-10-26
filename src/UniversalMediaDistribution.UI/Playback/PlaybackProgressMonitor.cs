using UniversalMediaDistribution.Application.UserState;

namespace UniversalMediaDistribution.UI.Playback;

internal static class PlaybackProgressMonitor
{
    public static async Task RunAsync(
        Func<CancellationToken, ValueTask<PlatformMediaState>> readState,
        Func<PlatformMediaState, CancellationToken, Task> onSample,
        Func<MediaProgressState, CancellationToken, ValueTask> saveProgress,
        Func<bool> isSeen,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        var lastSavedPosition = -1d;
        var lastWasEnded = false;
        var completedInSession = false;
        using var timer = new PeriodicTimer(pollInterval ?? TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var state = await readState(cancellationToken);
            await onSample(state, cancellationToken);
            if (Math.Abs(state.PositionSeconds - lastSavedPosition) >= 1 ||
                state.IsEnded != lastWasEnded || state.IsClosed)
            {
                var reachedEnd = state.IsEnded || state.DurationSeconds is > 0 &&
                    state.PositionSeconds >= state.DurationSeconds.Value - 2;
                completedInSession |= reachedEnd;
                await saveProgress(new MediaProgressState(
                    reachedEnd ? 0 : Math.Max(0, state.PositionSeconds),
                    MediaPositionKind.Seconds,
                    completedInSession || isSeen(),
                    DateTimeOffset.UtcNow), cancellationToken);
                lastSavedPosition = state.PositionSeconds;
                lastWasEnded = state.IsEnded;
            }

            // Ended players remain open for Replay. Keep sampling them without
            // repeatedly saving completion or scheduling redundant syncs.
            if (state.IsClosed) break;
        }
    }
}
