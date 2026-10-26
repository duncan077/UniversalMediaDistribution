namespace UniversalMediaDistribution.UI.Views;

internal static class ReaderViewportRetention
{
    public static double CalculateCompensatedOffset(
        double originalOffset,
        double currentOffset,
        double originalAnchorViewportTop,
        double currentAnchorViewportTop,
        double extentHeight,
        double viewportHeight)
    {
        if (!double.IsFinite(originalOffset) ||
            !double.IsFinite(currentOffset) ||
            !double.IsFinite(originalAnchorViewportTop) ||
            !double.IsFinite(currentAnchorViewportTop) ||
            !double.IsFinite(extentHeight) ||
            !double.IsFinite(viewportHeight))
        {
            return 0;
        }

        // A changed offset means the user moved the scrollbar/wheel, or
        // Avalonia already performed its own anchor correction. In both cases
        // the current position wins; restoring the old snapshot would make a
        // fast scroll rebound.
        if (Math.Abs(currentOffset - originalOffset) > 0.5)
        {
            return currentOffset;
        }

        var maximumOffset = Math.Max(0, extentHeight - viewportHeight);
        var layoutDelta =
            currentAnchorViewportTop - originalAnchorViewportTop;
        return Math.Clamp(
            currentOffset + layoutDelta,
            0,
            maximumOffset);
    }
}
