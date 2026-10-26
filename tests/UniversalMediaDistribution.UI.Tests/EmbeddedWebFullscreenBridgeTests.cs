using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class EmbeddedWebFullscreenBridgeTests
{
    [Fact]
    public void InitialNonFullscreenReportDoesNotExitApplicationFullscreen()
    {
        var bridge = new EmbeddedWebFullscreenBridge();

        var transition = bridge.Accept(
            "umd:embedded-fullscreen:exited");

        Assert.Equal(EmbeddedWebFullscreenTransition.None, transition);
        Assert.False(bridge.IsDocumentFullscreen);
    }

    [Fact]
    public void DocumentEnterAndExitAreReportedOnce()
    {
        var bridge = new EmbeddedWebFullscreenBridge();

        Assert.Equal(
            EmbeddedWebFullscreenTransition.Entered,
            bridge.Accept("umd:embedded-fullscreen:entered"));
        Assert.Equal(
            EmbeddedWebFullscreenTransition.None,
            bridge.Accept("umd:embedded-fullscreen:entered"));
        Assert.True(bridge.IsDocumentFullscreen);

        Assert.Equal(
            EmbeddedWebFullscreenTransition.Exited,
            bridge.Accept("umd:embedded-fullscreen:exited"));
        Assert.Equal(
            EmbeddedWebFullscreenTransition.None,
            bridge.Accept("umd:embedded-fullscreen:exited"));
        Assert.False(bridge.IsDocumentFullscreen);
    }

    [Fact]
    public void EscapeInsideWebViewRequestsApplicationFullscreenExit()
    {
        var bridge = new EmbeddedWebFullscreenBridge();

        var transition = bridge.Accept(
            "umd:embedded-fullscreen:exit-requested");

        Assert.Equal(
            EmbeddedWebFullscreenTransition.ExitRequested,
            transition);
    }

    [Fact]
    public void UnknownPageMessagesCannotChangeFullscreenState()
    {
        var bridge = new EmbeddedWebFullscreenBridge();

        var transition = bridge.Accept("fullscreen:true");

        Assert.Equal(EmbeddedWebFullscreenTransition.None, transition);
        Assert.False(bridge.IsDocumentFullscreen);
    }

    [Fact]
    public void ObserverListensForDocumentStateAndEscape()
    {
        Assert.Contains(
            "fullscreenchange",
            EmbeddedWebFullscreenBridge.ObserverScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Escape",
            EmbeddedWebFullscreenBridge.ObserverScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "invokeCSharpAction",
            EmbeddedWebFullscreenBridge.ObserverScript,
            StringComparison.Ordinal);
    }
}
