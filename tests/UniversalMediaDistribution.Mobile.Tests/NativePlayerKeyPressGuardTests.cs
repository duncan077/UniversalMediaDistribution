using UniversalMediaDistribution.Mobile;

namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class NativePlayerKeyPressGuardTests
{
    [Fact]
    public void RevealingControlsConsumesRepeatsAndReleaseButAllowsNextPress()
    {
        var guard = new NativePlayerKeyPressGuard();
        guard.SuppressUntilRelease(23);
        Assert.True(guard.Consume(23, released: false));
        Assert.True(guard.Consume(23, released: false));
        Assert.True(guard.Consume(23, released: true));
        Assert.False(guard.Consume(23, released: false));
        Assert.False(guard.Consume(23, released: true));
    }

    [Fact]
    public void OtherRemoteKeysDoNotConsumeOrReleaseTheOpeningPress()
    {
        var guard = new NativePlayerKeyPressGuard();
        guard.SuppressUntilRelease(23);
        Assert.False(guard.Consume(4, released: false));
        Assert.False(guard.Consume(4, released: true));
        Assert.True(guard.Consume(23, released: true));
    }

    [Fact]
    public void PausingActivityClearsAnUnfinishedPress()
    {
        var guard = new NativePlayerKeyPressGuard();
        guard.SuppressUntilRelease(23);
        guard.Reset();
        Assert.False(guard.Consume(23, released: false));
    }
}
