using UniversalMediaDistribution.Mobile;

namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class NativePlaybackTimelineTests
{
    [Theory]
    [InlineData(-1000, "0:00")]
    [InlineData(0, "0:00")]
    [InlineData(61000, "1:01")]
    [InlineData(3599000, "59:59")]
    [InlineData(3600000, "1:00:00")]
    [InlineData(3723000, "1:02:03")]
    [InlineData(int.MaxValue, "596:31:23")]
    public void FormatsElapsedAndDurationLabels(int milliseconds, string expected) =>
        Assert.Equal(expected, NativePlaybackTimeline.FormatTime(milliseconds));

    [Theory]
    [InlineData(-1, 120000, 0)]
    [InlineData(60000, 120000, 500)]
    [InlineData(120001, 120000, 1000)]
    [InlineData(int.MaxValue, int.MaxValue, 1000)]
    [InlineData(1000, 0, 0)]
    [InlineData(1000, -1, 0)]
    public void ProgressClampsToKnownDurationWithoutOverflow(int position, int duration, int expected) =>
        Assert.Equal(expected, NativePlaybackTimeline.ToProgress(position, duration));

    [Theory]
    [InlineData(500, 120000, 60000)]
    [InlineData(-1, 120000, 0)]
    [InlineData(1001, 120000, 120000)]
    [InlineData(1000, int.MaxValue, int.MaxValue)]
    [InlineData(500, 0, 0)]
    [InlineData(500, -1, 0)]
    public void SeekingClampsToPlayableRangeWithoutOverflow(int progress, int duration, int expected) =>
        Assert.Equal(expected, NativePlaybackTimeline.ToMilliseconds(progress, duration));
}
