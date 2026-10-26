using System.Globalization;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The embedded page reports its own player's state so a watch party can
/// include someone watching on a hoster's player. What matters here is that a
/// reading survives the round trip intact, that a page which stops reporting
/// stops being trusted, and that the commands sent back are valid JavaScript
/// whatever the machine's number formatting says.
/// </summary>
public sealed class EmbeddedWebPlaybackBridgeTests
{
    [Fact]
    public void AReportBecomesAReading()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bridge = new EmbeddedWebPlaybackBridge(clock);

        Assert.True(bridge.Accept(
            "umd:embedded-playback:123.500|2400.000|0|0|0"));

        var reading = bridge.TryRead();
        Assert.NotNull(reading);
        Assert.Equal(123.5, reading.Value.PositionSeconds, precision: 3);
        Assert.Equal(2400, reading.Value.DurationSeconds!.Value, precision: 3);
        Assert.True(reading.Value.IsPlaying);
        Assert.False(reading.Value.IsEnded);
        Assert.False(reading.Value.IsBuffering);
        Assert.True(bridge.CanControl);
    }

    [Fact]
    public void PausedEndedAndBufferingAreCarriedSeparately()
    {
        var bridge = new EmbeddedWebPlaybackBridge(
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        bridge.Accept("umd:embedded-playback:10.000|-1.000|1|1|1");

        var reading = bridge.TryRead();
        Assert.NotNull(reading);
        Assert.False(reading.Value.IsPlaying);
        Assert.True(reading.Value.IsEnded);
        Assert.True(reading.Value.IsBuffering);

        // A live stream has no meaningful duration, and the page says so
        // with a negative value rather than omitting the field.
        Assert.Null(reading.Value.DurationSeconds);
    }

    /// <summary>
    /// A video inside a cross-origin frame never reports. Treating the last
    /// reading as current forever would tell a watch party it can drive a
    /// page it cannot even see.
    /// </summary>
    [Fact]
    public void APageThatStopsReportingStopsBeingTrusted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bridge = new EmbeddedWebPlaybackBridge(clock);
        bridge.Accept("umd:embedded-playback:5.000|60.000|0|0|0");

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.NotNull(bridge.TryRead());

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(bridge.TryRead());
        Assert.False(bridge.CanControl);
    }

    [Fact]
    public void AnotherBridgesMessageIsLeftAlone()
    {
        var bridge = new EmbeddedWebPlaybackBridge(
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.False(bridge.Accept("umd:embedded-fullscreen:entered"));
        Assert.False(bridge.Accept(null));
        Assert.False(bridge.Accept(string.Empty));
        Assert.Null(bridge.TryRead());
    }

    /// <summary>
    /// A malformed report is claimed — it is ours — but must not replace a
    /// good reading or invent one.
    /// </summary>
    [Fact]
    public void AMalformedReportIsClaimedWithoutBeingBelieved()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bridge = new EmbeddedWebPlaybackBridge(clock);
        bridge.Accept("umd:embedded-playback:7.000|60.000|0|0|0");

        Assert.True(bridge.Accept("umd:embedded-playback:broken"));
        Assert.True(bridge.Accept("umd:embedded-playback:x|y|0|0|0"));

        var reading = bridge.TryRead();
        Assert.NotNull(reading);
        Assert.Equal(7, reading.Value.PositionSeconds, precision: 3);
    }

    [Fact]
    public void ClosingForgetsThePage()
    {
        var bridge = new EmbeddedWebPlaybackBridge(
            new FakeTimeProvider(DateTimeOffset.UtcNow));
        bridge.Accept("umd:embedded-playback:5.000|60.000|0|0|0");

        bridge.Reset();

        Assert.Null(bridge.TryRead());
        Assert.False(bridge.CanControl);
    }

    /// <summary>
    /// The seek command is JavaScript source. A culture that writes decimals
    /// with a comma would emit <c>currentTime = 12,5</c>, which is a comma
    /// operator and silently seeks to 5.
    /// </summary>
    [Fact]
    public void ASeekIsWrittenWithAnInvariantDecimalPoint()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture =
                CultureInfo.GetCultureInfo("es-AR");

            var script = EmbeddedWebPlaybackBridge.CreateSeekScript(12.5);

            Assert.Contains("video.currentTime = 12.5;", script);
            Assert.DoesNotContain("12,5", script);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void ASeekNeverAsksForANegativePosition()
    {
        var script = EmbeddedWebPlaybackBridge.CreateSeekScript(-30);

        Assert.Contains("video.currentTime = 0;", script);
    }

    [Fact]
    public void TheTransportCommandsDriveThePagesOwnPlayer()
    {
        var play = EmbeddedWebPlaybackBridge.CreateSetPlayingScript(true);
        var pause = EmbeddedWebPlaybackBridge.CreateSetPlayingScript(false);

        Assert.Contains("video.play()", play);
        Assert.Contains("video.pause();", pause);

        // A rejected play promise is routine — browsers refuse one that no
        // gesture asked for — and must not surface as a page error.
        Assert.Contains("catch", play);
    }

    /// <summary>
    /// The observer runs inside a page the application does not control, so
    /// the things that keep it from breaking that page are worth pinning.
    /// </summary>
    [Fact]
    public void TheObserverInstallsItselfOnceAndKeepsLooking()
    {
        var script = EmbeddedWebPlaybackBridge.ObserverScript;

        Assert.Contains("__umdEmbeddedPlaybackBridge", script);
        Assert.Contains("invokeCSharpAction", script);
        Assert.Contains("setInterval", script);
        Assert.Contains("readyState < 3", script);
        Assert.Contains(
            EmbeddedWebPlaybackBridge.MessagePrefix,
            script);
    }

    /// <summary>
    /// Opening a page for a paused party must not play it and correct itself
    /// a tick later: that is a second of video and sound nobody asked for.
    /// </summary>
    [Fact]
    public void TheStartScriptCanHoldAPageAtItsFirstFrame()
    {
        var playing =
            WebViewPlatformMediaPlayer.BuildPlaybackStartScript(
                startPaused: false);
        var held =
            WebViewPlatformMediaPlayer.BuildPlaybackStartScript(
                startPaused: true);

        Assert.Contains("const startPaused = false;", playing);
        Assert.Contains("const startPaused = true;", held);

        // Both variants still click a play overlay when the page has no
        // video element at all: without one there is nothing for a party to
        // read or drive, so the player has to be materialized either way.
        Assert.Contains("candidate.click();", playing);
        Assert.Contains("candidate.click();", held);
        Assert.Contains("video.pause();", held);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
