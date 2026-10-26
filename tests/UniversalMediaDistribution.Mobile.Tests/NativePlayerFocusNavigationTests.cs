using UniversalMediaDistribution.Mobile;

namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class NativePlayerFocusNavigationTests
{
    [Fact]
    public void RemoteSkipsHiddenAndBusyActionsAndStaysInsidePanel()
    {
        var links = NativePlayerFocusNavigation.Build(
            [new(1, true, true), new(2, false, true), new(3, true, false), new(4, true, true)]);
        Assert.Equal(new NativePlayerFocusLink(1, 1, 4), links[0]);
        Assert.Equal(new NativePlayerFocusLink(4, 1, 4), links[1]);
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public void CloseRemainsReachableWhileEveryActionIsBusy()
    {
        var links = NativePlayerFocusNavigation.Build([new(1, true, true), new(2, true, false)]);
        Assert.Equal(new NativePlayerFocusLink(1, 1, 1), Assert.Single(links));
        Assert.Empty(NativePlayerFocusNavigation.Build([]));
    }
}
