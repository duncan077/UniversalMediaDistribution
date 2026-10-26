using System.Collections.ObjectModel;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// Applies an ordered reference snapshot without clearing the collection.
/// Retained view models keep their visual state, image loads, and TV focus.
/// </summary>
internal static class ObservableCollectionSynchronizer
{
    public static void Synchronize<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (index < target.Count &&
                ReferenceEquals(target[index], item))
            {
                continue;
            }

            var existingIndex = IndexOfReference(
                target,
                item,
                index + 1);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else
            {
                target.Insert(index, item);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int IndexOfReference<T>(
        ObservableCollection<T> items,
        T sought,
        int startIndex)
        where T : class
    {
        for (var index = Math.Max(0, startIndex);
             index < items.Count;
             index++)
        {
            if (ReferenceEquals(items[index], sought))
            {
                return index;
            }
        }

        return -1;
    }
}
