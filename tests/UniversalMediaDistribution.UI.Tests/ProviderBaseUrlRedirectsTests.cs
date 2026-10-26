using System.Net;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// A site that changes domain announces it with a permanent redirect. These
/// pin when that is adopted as the provider's address and — the part that
/// matters more — when it is not.
/// </summary>
public sealed class ProviderBaseUrlRedirectsTests : IDisposable
{
    public void Dispose() => ProviderBaseUrlRedirects.Reset();

    [Fact]
    public void APermanentMoveBecomesTheProviderAddress()
    {
        Observe(
            "https://seriesflixhd.ink/",
            HttpStatusCode.MovedPermanently,
            "https://seriesflixhd.win/");

        Assert.Equal(
            "https://seriesflixhd.win/",
            ProviderBaseUrlRedirects.Apply("https://seriesflixhd.ink/"));
    }

    [Fact]
    public void ATemporaryRedirectIsNotAMove()
    {
        // A site load-shedding onto a mirror for an hour has not changed
        // address, and adopting it would strand the provider there.
        Observe(
            "https://example.test/",
            HttpStatusCode.Found,
            "https://mirror.example.test/");

        Assert.Equal(
            "https://example.test/",
            ProviderBaseUrlRedirects.Apply("https://example.test/"));
    }

    [Fact]
    public void ARedirectToAnotherPageIsNotAMove()
    {
        // The real shape this guards against: a dead route permanently
        // redirecting to the home page. Treating that as a move would rewrite
        // the whole provider onto it.
        Observe(
            "https://example.test/directorio/anime",
            HttpStatusCode.MovedPermanently,
            "https://example.test/");

        Assert.Equal(
            "https://example.test/",
            ProviderBaseUrlRedirects.Apply("https://example.test/"));
    }

    [Fact]
    public void ARedirectAwayFromHttpsIsNotFollowed()
    {
        Observe(
            "https://example.test/",
            HttpStatusCode.MovedPermanently,
            "http://example.test/");

        Assert.Equal(
            "https://example.test/",
            ProviderBaseUrlRedirects.Apply("https://example.test/"));
    }

    [Fact]
    public void SuccessiveMovesAreFollowedToTheEnd()
    {
        Observe("https://one.test/", HttpStatusCode.MovedPermanently, "https://two.test/");
        Observe("https://two.test/", HttpStatusCode.PermanentRedirect, "https://three.test/");

        Assert.Equal(
            "https://three.test/",
            ProviderBaseUrlRedirects.Apply("https://one.test/"));
    }

    [Fact]
    public void TheConfiguredPathSurvivesTheMove()
    {
        Observe("https://old.test/", HttpStatusCode.MovedPermanently, "https://new.test/");

        Assert.Equal(
            "https://new.test/api/v2/",
            ProviderBaseUrlRedirects.Apply("https://old.test/api/v2/"));
    }

    [Fact]
    public void AnAddressNothingIsKnownAboutIsLeftAlone() =>
        Assert.Equal(
            "https://untouched.test/",
            ProviderBaseUrlRedirects.Apply("https://untouched.test/"));

    private static void Observe(
        string requested,
        HttpStatusCode status,
        string location)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(requested));
        using var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location);
        ProviderBaseUrlRedirects.Observe(request, response);
    }
}
