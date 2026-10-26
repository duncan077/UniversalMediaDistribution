namespace UniversalMediaDistribution.UI.Threading;

/// <summary>
/// Runs a catalog or user-state read away from the thread that asked for it.
/// </summary>
/// <remarks>
/// The query is the smaller half of these reads: an overview sorts the whole
/// library by name under the current culture and builds a card for every
/// title, and the progress read groups every position ever recorded. Awaited
/// from a command all of that runs on the UI thread, and the window is not
/// drawing while it does.
/// <para>
/// Only the read moves. What the caller does with the result stays where it
/// was called, because that part fills the bound collections — so callers
/// read whatever the read needs from the view model before handing it over,
/// rather than inside the callback.
/// </para>
/// </remarks>
internal static class OffUiThread
{
    public static Task<T> ReadAsync<T>(Func<ValueTask<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        // Started on the pool, and with no synchronization context there,
        // every await inside the read resumes there too rather than hopping
        // back between steps.
        return Task.Run(() => read().AsTask());
    }
}
