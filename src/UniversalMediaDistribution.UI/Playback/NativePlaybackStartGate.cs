namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Confirms that an external native player has actually started before its
/// browser fallback is discarded.
/// </summary>
internal static class NativePlaybackStartGate
{
    internal static async ValueTask WaitAsync(
        Func<CancellationToken, ValueTask<PlatformMediaState>> readStateAsync,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readStateAsync);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var deadline = Environment.TickCount64 +
            (long)timeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await readStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state.IsPlaying || state.IsEnded)
            {
                return;
            }

            if (state.IsClosed ||
                !string.IsNullOrWhiteSpace(state.ErrorMessage))
            {
                throw new InvalidOperationException(
                    state.ErrorMessage ??
                    "Native playback closed before it started.");
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    "Native playback did not start before the browser " +
                    "fallback timeout.");
            }

            await Task.Delay(
                    pollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
