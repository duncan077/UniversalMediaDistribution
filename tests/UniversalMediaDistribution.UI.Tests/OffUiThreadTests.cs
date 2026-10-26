using UniversalMediaDistribution.UI.Threading;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The catalog reads behind a refresh are not just queries: the overview
/// sorts the whole library and builds a card per title. Run where the command
/// runs, that is the UI thread not drawing.
/// </summary>
public sealed class OffUiThreadTests
{
    [Fact]
    public async Task TheReadLeavesTheCallersContext()
    {
        var caller = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(caller);
        try
        {
            SynchronizationContext? observed = null;

            await OffUiThread.ReadAsync(() =>
            {
                observed = SynchronizationContext.Current;
                return ValueTask.FromResult(0);
            });

            Assert.NotSame(caller, observed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task EveryStepOfTheReadStaysOffThatContext()
    {
        // A read is several awaited queries. Only the first one starting off
        // the caller's thread would still hop back between the rest.
        var caller = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(caller);
        try
        {
            var observed = new List<SynchronizationContext?>();

            await OffUiThread.ReadAsync(async () =>
            {
                observed.Add(SynchronizationContext.Current);
                await Task.Yield();
                observed.Add(SynchronizationContext.Current);
                await Task.Delay(1);
                observed.Add(SynchronizationContext.Current);
                return 0;
            });

            Assert.Equal(3, observed.Count);
            Assert.DoesNotContain(caller, observed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task TheCultureTheSortDependsOnFollowsTheRead()
    {
        // The overview orders titles with a culture-sensitive comparer, so a
        // pool thread reading a different culture would order the library
        // differently from the thread that asked.
        var culture = new System.Globalization.CultureInfo("tr-TR");
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        try
        {
            var observed = await OffUiThread.ReadAsync(() =>
                ValueTask.FromResult(
                    System.Globalization.CultureInfo.CurrentCulture));

            Assert.Equal(culture, observed);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class RecordingSynchronizationContext :
        SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            ThreadPool.QueueUserWorkItem(
                _ =>
                {
                    SetSynchronizationContext(this);
                    callback(state);
                },
                null);
    }
}
