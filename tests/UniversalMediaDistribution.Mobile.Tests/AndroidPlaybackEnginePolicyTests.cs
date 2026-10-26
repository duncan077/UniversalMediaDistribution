using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class AndroidPlaybackEnginePolicyTests
{
    private static PlatformMediaSource Stream(Uri? origin = null, bool encrypted = false) => new(
        new Uri("https://stream.example/live.m3u8"), null,
        new Uri("https://provider.example/watch"), "Provider player/1.0",
        encrypted ? [new PlatformMediaDecryptionKey(new string('a', 32), new string('b', 32))] : null,
        origin);

    [Theory]
    [InlineData(true, true, AndroidPlaybackEngine.LibVlc)]
    [InlineData(true, false, AndroidPlaybackEngine.LibVlc)]
    [InlineData(false, true, AndroidPlaybackEngine.LibVlc)]
    [InlineData(false, false, AndroidPlaybackEngine.LibVlc)]
    public void VlcIsDefaultForTvAndPhoneLiveAndOnDemand(bool tv, bool live, AndroidPlaybackEngine expected) =>
        Assert.Equal(expected, AndroidPlaybackEnginePolicy.Initial(tv, live, Stream()));

    [Fact]
    public void RequiredOriginIsNeverSilentlyDropped()
    {
        var source = Stream(new Uri("https://provider.example"));
        Assert.False(AndroidPlaybackEnginePolicy.CanUseLibVlc(source));
        Assert.Equal(AndroidPlaybackEngine.System, AndroidPlaybackEnginePolicy.Initial(true, true, source));
        Assert.Null(AndroidPlaybackEnginePolicy.Fallback(true, source, AndroidPlaybackEngine.System,
            new HashSet<AndroidPlaybackEngine> { AndroidPlaybackEngine.System }));
        Assert.Throws<NotSupportedException>(() => AndroidPlaybackEnginePolicy.LibVlcOptions(source, true));
    }

    [Fact]
    public void StableAndroidVlcDoesNotPretendToSupportClearKey()
    {
        var source = Stream(encrypted: true);
        Assert.False(AndroidPlaybackEnginePolicy.CanUseLibVlc(source));
        Assert.Throws<NotSupportedException>(() => AndroidPlaybackEnginePolicy.LibVlcOptions(source, true));
        Assert.Null(AndroidPlaybackEnginePolicy.Fallback(true, source, AndroidPlaybackEngine.LibVlc,
            new HashSet<AndroidPlaybackEngine>()));
    }

    [Theory]
    [InlineData(AndroidPlaybackEngine.LibVlc, AndroidPlaybackEngine.System)]
    [InlineData(AndroidPlaybackEngine.System, AndroidPlaybackEngine.LibVlc)]
    public void FailureTriesOtherEngineOnlyOnce(AndroidPlaybackEngine first, AndroidPlaybackEngine second)
    {
        var attempted = new HashSet<AndroidPlaybackEngine> { first };
        Assert.Equal(second, AndroidPlaybackEnginePolicy.Fallback(true, Stream(), first, attempted));
        attempted.Add(second);
        Assert.Null(AndroidPlaybackEnginePolicy.Fallback(true, Stream(), second, attempted));
    }

    [Fact]
    public void PhonePlaybackCanRecoverFromVlcFailure() =>
        Assert.Equal(AndroidPlaybackEngine.System,
            AndroidPlaybackEnginePolicy.Fallback(false, Stream(), AndroidPlaybackEngine.LibVlc,
                new HashSet<AndroidPlaybackEngine> { AndroidPlaybackEngine.LibVlc }));

    [Fact]
    public void VlcReceivesTheProviderHeadersAndLiveBufferOptions()
    {
        var options = AndroidPlaybackEnginePolicy.LibVlcOptions(Stream(), true);
        Assert.Contains(":http-user-agent=Provider player/1.0", options);
        Assert.Contains(":http-referrer=https://provider.example/watch", options);
        Assert.Contains(":network-caching=1500", options);
        Assert.Contains(":http-reconnect", options);
    }
}
