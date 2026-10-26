using Avalonia.Threading;

namespace UniversalMediaDistribution.UI.Threading;

internal sealed class CoalescedDispatcherAction
{
    private int _pending;

    internal void Post(Action action)
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // Clear before running so a change arriving during the refresh
            // can schedule one follow-up without growing an event backlog.
            Volatile.Write(ref _pending, 0);
            action();
        }, DispatcherPriority.Background);
    }
}
