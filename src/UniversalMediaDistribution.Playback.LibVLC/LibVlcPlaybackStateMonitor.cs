using System.Diagnostics;
using LibVLCSharp.Shared;

namespace UniversalMediaDistribution.Playback.LibVLC;

internal static class LibVlcPlaybackStateMonitor
{
    internal static async Task<VLCState> WaitForStartAsync(
        Func<VLCState> readState,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readState);
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                pollInterval,
                "The LibVLC state poll interval must be positive.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The LibVLC playback start timeout must be positive.");
        }

        var timer = Stopwatch.StartNew();
        var lastState = VLCState.NothingSpecial;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastState = readState();
            if (lastState is
                VLCState.Playing or
                VLCState.Paused or
                VLCState.Ended or
                VLCState.Error)
            {
                return lastState;
            }

            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"LibVLC remained in state '{lastState}' for " +
                    $"{timeout.TotalSeconds:0.###} seconds.");
            }

            await Task.Delay(
                remaining < pollInterval ? remaining : pollInterval,
                cancellationToken);
        }
    }
}
