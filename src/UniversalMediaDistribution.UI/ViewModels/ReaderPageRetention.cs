namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// Decides which pages of an open chapter keep their decoded image. Reading a
/// long chapter used to leave every page ever shown decoded in memory, which is
/// what makes a three-hundred page manhwa stutter and, on a phone or a
/// television box, run out of memory outright.
/// </summary>
internal static class ReaderPageRetention
{
    internal static bool ShouldRetain(
        int index,
        int centerIndex,
        int pageCount,
        int perSide)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perSide);

        // A chapter that already fits inside the window is left alone: dropping
        // pages there would only cause reloads with nothing gained.
        if (pageCount <= (perSide * 2) + 1)
        {
            return true;
        }

        return index >= centerIndex - perSide &&
               index <= centerIndex + perSide;
    }
}
