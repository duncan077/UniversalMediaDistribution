using System.Collections.ObjectModel;
using System.Collections.Specialized;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ObservableCollectionSynchronizerTests
{
    [Fact]
    public void SynchronizePreservesMatchingInstancesWithoutResettingCollection()
    {
        var first = new object();
        var second = new object();
        var removed = new object();
        var added = new object();
        var target = new ObservableCollection<object>(
            [first, second, removed]);
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, eventArgs) =>
            actions.Add(eventArgs.Action);

        ObservableCollectionSynchronizer.Synchronize(
            target,
            [second, first, added]);

        Assert.Equal(3, target.Count);
        Assert.Same(second, target[0]);
        Assert.Same(first, target[1]);
        Assert.Same(added, target[2]);
        Assert.DoesNotContain(
            NotifyCollectionChangedAction.Reset,
            actions);
    }

    [Fact]
    public void SynchronizeLeavesIdenticalCollectionUntouched()
    {
        var first = new object();
        var second = new object();
        var target = new ObservableCollection<object>([first, second]);
        var changeCount = 0;
        target.CollectionChanged += (_, _) => changeCount++;

        ObservableCollectionSynchronizer.Synchronize(
            target,
            [first, second]);

        Assert.Equal(0, changeCount);
    }
}
