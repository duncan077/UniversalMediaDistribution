using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ReaderViewportRetentionTests
{
    [Fact]
    public void CompensatesForPagesGrowingAboveTheVisiblePage()
    {
        var offset = ReaderViewportRetention.CalculateCompensatedOffset(
            originalOffset: 920,
            currentOffset: 920,
            originalAnchorViewportTop: 80,
            currentAnchorViewportTop: 380,
            extentHeight: 5000,
            viewportHeight: 800);

        Assert.Equal(1220, offset);
    }

    [Fact]
    public void DoesNotDoubleCompensateFrameworkAnchoring()
    {
        var offset = ReaderViewportRetention.CalculateCompensatedOffset(
            originalOffset: 920,
            currentOffset: 1220,
            originalAnchorViewportTop: 80,
            currentAnchorViewportTop: 80,
            extentHeight: 5000,
            viewportHeight: 800);

        Assert.Equal(1220, offset);
    }

    [Fact]
    public void DoesNotReboundAfterFastScrollbarMovement()
    {
        var offset = ReaderViewportRetention.CalculateCompensatedOffset(
            originalOffset: 920,
            currentOffset: 3500,
            originalAnchorViewportTop: 80,
            currentAnchorViewportTop: -2200,
            extentHeight: 8000,
            viewportHeight: 800);

        Assert.Equal(3500, offset);
    }

    [Fact]
    public void CompensatesForReleasedPagesShrinkingAboveTheVisiblePage()
    {
        var offset = ReaderViewportRetention.CalculateCompensatedOffset(
            originalOffset: 1200,
            currentOffset: 1200,
            originalAnchorViewportTop: 100,
            currentAnchorViewportTop: -200,
            extentHeight: 5000,
            viewportHeight: 800);

        Assert.Equal(900, offset);
    }

    [Fact]
    public void ClampsCompensationToTheScrollableExtent()
    {
        var offset = ReaderViewportRetention.CalculateCompensatedOffset(
            originalOffset: 3900,
            currentOffset: 3900,
            originalAnchorViewportTop: 100,
            currentAnchorViewportTop: 500,
            extentHeight: 5000,
            viewportHeight: 800);

        Assert.Equal(4200, offset);
    }
}
