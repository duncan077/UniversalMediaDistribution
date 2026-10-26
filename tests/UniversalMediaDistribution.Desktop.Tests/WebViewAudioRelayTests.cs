using UniversalMediaDistribution.Desktop.Web;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class WebViewAudioRelayTests
{
    [Fact]
    public void QuantizationNoiseDoesNotPreventSilentCaptureFallback()
    {
        Assert.False(AudioRelayHealth.ContainsSignal([0, 1, -1, 0, 2, -2]));
        Assert.True(AudioRelayHealth.ContainsSignal([0, 32, -32]));
        Assert.True(AudioRelayHealth.ContainsSignal([short.MinValue]));
    }

    [Fact]
    public void RelayDoesNotTakeOverUntilRealAudioReachesOutput()
    {
        var health = new AudioRelayHealth(0);
        health.Observe(100, signal: false, written: true);
        Assert.False(health.CanTakeOver);
        health.Observe(200, signal: true, written: true);
        Assert.True(health.CanTakeOver);
        Assert.False(health.MustRestoreBrowser(200));
    }

    [Fact]
    public void SilentPacketsAfterMuteRestoreBrowserWithoutReadingSourceMeter()
    {
        var health = new AudioRelayHealth(0);
        health.Observe(100, signal: true, written: true);
        for (var time = 200; time <= 1600; time += 100)
            health.Observe(time, signal: false, written: true);
        Assert.True(health.MustRestoreBrowser(1600));
    }

    [Fact]
    public void NoCapturePacketsOrStalledOutputRestoreBrowser()
    {
        var empty = new AudioRelayHealth(0);
        Assert.False(empty.CanTakeOver);
        Assert.True(empty.MustRestoreBrowser(1500));
        var stalled = new AudioRelayHealth(0);
        stalled.Observe(100, signal: true, written: true);
        stalled.Observe(1600, signal: true, written: false);
        Assert.True(stalled.MustRestoreBrowser(1600));
    }

    [Fact]
    public void FlowingAudioKeepsRelayHealthy()
    {
        var health = new AudioRelayHealth(0);
        for (var time = 100; time <= 10000; time += 100)
        {
            health.Observe(time, signal: true, written: true);
            Assert.False(health.MustRestoreBrowser(time));
        }
    }

    [Fact]
    public void BrowserTreeExcludesOtherAppsAndHost()
    {
        var host = (uint)Environment.ProcessId;
        var tree = BrowserAudioSession.SelectDescendants(1, new Dictionary<uint, uint>
        {
            [1] = host,
            [2] = 1,
            [3] = 2,
            [4] = host,
            [5] = 4,
            [host] = 0
        });
        Assert.Equal(new uint[] { 1, 2, 3 }, tree.Order());
    }

    [Fact]
    public void MissingBrowserOrHostTargetCannotCaptureWholeApplication()
    {
        var parents = new Dictionary<uint, uint> { [1] = 0, [2] = 1 };
        Assert.Empty(BrowserAudioSession.SelectDescendants(9, parents));
        Assert.Empty(BrowserAudioSession.SelectDescendants(0, parents));
        Assert.Empty(BrowserAudioSession.SelectDescendants((uint)Environment.ProcessId, parents));
    }

    [Fact]
    public void QueueWrapPreservesStereoFrameOrder()
    {
        var queue = new PcmRelayBuffer(12);
        queue.Write([1, 2, 3, 4, 5, 6, 7, 8]);
        queue.Read(new byte[4]);
        queue.Write([9, 10, 11, 12, 13, 14, 15, 16]);
        var output = new byte[12];
        queue.Read(output);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, output);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void SlowDeviceDropsOldFramesInsteadOfBuildingLatency()
    {
        var queue = new PcmRelayBuffer(8);
        queue.Write([1, 2, 3, 4]);
        queue.Write([5, 6, 7, 8, 9, 10, 11, 12]);
        var output = new byte[8];
        queue.Read(output);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }, output);
        queue.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        queue.Read(output);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }, output);
    }

    [Fact]
    public void PartialStereoFramesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmRelayBuffer(7));
        var queue = new PcmRelayBuffer(8);
        Assert.Throws<ArgumentException>(() => queue.Write([1, 2]));
        Assert.Throws<ArgumentException>(() => queue.Read(new byte[4]));
    }
}
