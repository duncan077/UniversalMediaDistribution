using UniversalMediaDistribution.Playback.LibVLC;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class PlayerOverlayLifecycleTests
{
    [Fact]
    public void OverlayRequiresBothAttachedViewAndActiveMedia()
    {
        var lifecycle = new PlayerOverlayLifecycle();

        lifecycle.Attach();
        Assert.False(lifecycle.ShouldShowOverlay);

        lifecycle.ActivateMedia();
        Assert.True(lifecycle.ShouldShowOverlay);

        lifecycle.DeactivateMedia();
        Assert.False(lifecycle.ShouldShowOverlay);
    }

    [Fact]
    public void DetachingViewClosesOverlayUntilItIsAttachedAgain()
    {
        var lifecycle = new PlayerOverlayLifecycle();
        lifecycle.ActivateMedia();
        lifecycle.Attach();

        lifecycle.Detach();
        Assert.False(lifecycle.ShouldShowOverlay);

        lifecycle.Attach();
        Assert.True(lifecycle.ShouldShowOverlay);
    }
}
