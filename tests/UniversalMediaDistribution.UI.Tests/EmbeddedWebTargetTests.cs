using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// A wrapper page whose only content is an iframe of the real player puts that
/// player out of the filter's reach: a cross-origin iframe runs in its own
/// renderer, and the request-interception hook only sees the frame it is
/// attached to. Opening the inner address instead is what makes the player's
/// own requests filterable at all.
/// </summary>
public sealed class EmbeddedWebTargetTests
{
    private static Uri Unwrap(string url) =>
        EmbeddedWebTarget.Unwrap(new Uri(url), static _ => true);

    [Fact]
    public void AnIframeWrapperResolvesToThePlayerItWraps()
    {
        Assert.Equal(
            new Uri("https://voe.sx/e/dxaecmu83h94"),
            Unwrap(
                "https://nupload.top/iframe/?url=" +
                "https%3A%2F%2Fvoe.sx%2Fe%2Fdxaecmu83h94"));
    }

    [Theory]
    [InlineData("u")]
    [InlineData("link")]
    [InlineData("src")]
    [InlineData("embed")]
    [InlineData("file")]
    public void TheOtherNamesHostsUseAreRecognized(string parameter)
    {
        Assert.Equal(
            new Uri("https://player.example/e/1"),
            Unwrap(
                $"https://wrapper.example/go?{parameter}=" +
                "https%3A%2F%2Fplayer.example%2Fe%2F1"));
    }

    [Fact]
    public void APlainPlayerAddressIsLeftAlone()
    {
        var direct = "https://player.example/e/dxaecmu83h94";

        Assert.Equal(new Uri(direct), Unwrap(direct));
    }

    [Fact]
    public void AParameterThatMerelyContainsAUrlIsNotFollowed()
    {
        // Following a tracking or return parameter would navigate the viewer
        // somewhere they never asked to go.
        var url =
            "https://player.example/e/1?ref=https%3A%2F%2Ftracker.example%2Fr";

        Assert.Equal(new Uri(url), Unwrap(url));
    }

    [Fact]
    public void AWrapperPointingAtItsOwnHostIsNotAWrapper()
    {
        var url =
            "https://player.example/iframe/?url=https%3A%2F%2Fplayer.example%2Fe%2F1";

        Assert.Equal(new Uri(url), Unwrap(url));
    }

    [Fact]
    public void ANonHttpTargetIsRefused()
    {
        var url = "https://wrapper.example/go?url=file%3A%2F%2F%2Fc%3A%2Fsecret";

        Assert.Equal(new Uri(url), Unwrap(url));
    }

    [Fact]
    public void AnUnacceptableTargetIsRefused()
    {
        // The player's own safety check rejects loopback and private
        // addresses; unwrapping must not become a way around it.
        var url = "https://wrapper.example/go?url=http%3A%2F%2F127.0.0.1%2Fx";

        Assert.Equal(
            new Uri(url),
            EmbeddedWebTarget.Unwrap(
                new Uri(url),
                static candidate => !candidate.IsLoopback));
    }

    [Fact]
    public void NestedWrappersResolveToTheInnermostPlayer()
    {
        Assert.Equal(
            new Uri("https://player.example/e/1"),
            Unwrap(
                "https://outer.example/i/?url=" +
                Uri.EscapeDataString(
                    "https://inner.example/i/?url=" +
                    Uri.EscapeDataString("https://player.example/e/1"))));
    }
}
