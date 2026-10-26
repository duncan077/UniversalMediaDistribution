using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class BackgroundWebVideoPlaybackTests
{
    [Fact]
    public void ASessionRunsForThreeMinutesAfterPlaybackStarts()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(3),
            BackgroundWebVideoPlayback.PlaybackDuration);
        Assert.Equal(
            "umd:background-video:playing",
            BackgroundWebVideoPlayback.PlayingMessage);
    }

    [Fact]
    public void TheScriptClicksThePlayerAndWaitsForRealPlayback()
    {
        var script = BackgroundWebVideoPlayback.StartScript;

        Assert.Contains("candidate.click();", script);
        Assert.Contains("video.click();", script);
        Assert.Contains("video.play()", script);
        Assert.Contains(
            "video.addEventListener(\"playing\"",
            script);
        Assert.Contains(
            BackgroundWebVideoPlayback.PlayingMessage,
            script);
    }

    [Fact]
    public void TheScriptSupportsBothNativeMessageChannels()
    {
        var script = BackgroundWebVideoPlayback.StartScript;

        Assert.Contains("window.umdBackgroundVideo.postMessage", script);
        Assert.Contains("window.chrome.webview.postMessage", script);
        Assert.Contains("window.invokeCSharpAction", script);
    }

    [Theory]
    [InlineData("file:///tmp/video.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://name:secret@example.test/embed")]
    public void OnlyCredentialFreeRemotePagesAreAccepted(string address)
    {
        Assert.Throws<ArgumentException>(() =>
            BackgroundWebVideoPlayback.ValidateSource(new Uri(address)));
    }

    [Theory]
    [InlineData("https://player.example.test/embed/123")]
    [InlineData("http://127.0.0.1:8080/embed/123")]
    public void HttpAndHttpsPagesAreAccepted(string address) =>
        BackgroundWebVideoPlayback.ValidateSource(new Uri(address));
}
