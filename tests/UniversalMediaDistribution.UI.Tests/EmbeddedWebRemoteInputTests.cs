using Avalonia.Controls;
using Avalonia.Platform;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class EmbeddedWebRemoteInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OkDoesNotClickWhenNoWebPlayerIsOpen(bool repeatOrRelease)
    {
        var input = new RecordingInput();
        var player = new WebViewPlatformMediaPlayer(new UnavailablePlatformMediaPlayer(), remoteInput: input)
        {
            RemoteActivationAllowed = () => true
        };

        Assert.False(player.CanActivateRemoteCenter);
        Assert.False(player.TryActivateRemoteCenter(repeatOrRelease));
        Assert.Equal(0, input.Clicks);
    }

    [Fact]
    public void PlayersWithoutNativeRemoteSupportLeaveEnterAlone()
    {
        var player = new WebViewPlatformMediaPlayer(new UnavailablePlatformMediaPlayer())
        {
            RemoteActivationAllowed = () => true
        };

        Assert.False(player.CanActivateRemoteCenter);
        Assert.False(player.TryActivateRemoteCenter());
    }

    private sealed class RecordingInput : INativeWebViewRemoteInput
    {
        public int Clicks { get; private set; }
        public void Attach(NativeWebView webView, IPlatformHandle? handle, Func<bool, bool> activate) { }
        public void Detach() { }
        public ValueTask ClickCenterAsync(NativeWebView webView)
        {
            Clicks++;
            return ValueTask.CompletedTask;
        }
    }
}
