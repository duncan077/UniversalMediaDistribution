using System.Diagnostics;

namespace UniversalMediaDistribution.Desktop;

internal static class DesktopShutdownCoordinator
{
    public static async Task<bool> DisposeAsync(
        Task? applicationShutdownTask,
        IAsyncDisposable mediaPlayer,
        IAsyncDisposable torrentEngine,
        TimeSpan componentTimeout)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        ArgumentNullException.ThrowIfNull(torrentEngine);
        if (componentTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(componentTimeout));
        }

        if (applicationShutdownTask is { IsCompleted: false })
        {
            Trace.TraceError(
                "Application UI shutdown is still running after the window closed. " +
                "Resource disposal was skipped to avoid overlapping cleanup.");
            return false;
        }

        var succeeded = ObserveApplicationShutdown(applicationShutdownTask);
        var mediaPlayerResult = await TryDisposeAsync(
            "media player",
            mediaPlayer,
            componentTimeout);
        // A media player that timed out is reported below, but it must not
        // cost the torrent engine its own shutdown: that is what flushes
        // fastresume state and closes the listeners.
        var torrentEngineResult = await TryDisposeAsync(
            "torrent engine",
            torrentEngine,
            componentTimeout);
        return succeeded &&
            mediaPlayerResult is DisposalResult.Succeeded &&
            torrentEngineResult is DisposalResult.Succeeded;
    }

    private static bool ObserveApplicationShutdown(Task? shutdownTask)
    {
        if (shutdownTask is null)
        {
            return true;
        }

        try
        {
            shutdownTask.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"Application UI shutdown failed: {exception.Message}");
            return false;
        }
    }

    private static async Task<DisposalResult> TryDisposeAsync(
        string componentName,
        IAsyncDisposable component,
        TimeSpan timeout)
    {
        try
        {
            await component.DisposeAsync()
                .AsTask()
                .WaitAsync(timeout)
                .ConfigureAwait(false);
            return DisposalResult.Succeeded;
        }
        catch (TimeoutException)
        {
            Trace.TraceError(
                $"{componentName} disposal exceeded {timeout.TotalSeconds:0} seconds.");
            return DisposalResult.TimedOut;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"{componentName} disposal failed: {exception.Message}");
            return DisposalResult.Failed;
        }
    }

    private enum DisposalResult
    {
        Succeeded,
        Failed,
        TimedOut
    }
}
