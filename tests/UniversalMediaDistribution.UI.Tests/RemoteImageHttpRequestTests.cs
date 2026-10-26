using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class RemoteImageHttpRequestTests
{
    [Fact]
    public void CreateCarriesProviderReferrerAndImageHeaders()
    {
        var imageUri = new Uri(
            "https://image-cdn.example/title/cover.webp");
        var providerUri = new Uri(
            "https://configured-provider.example/");

        using var request = RemoteImageHttpRequest.Create(
            imageUri,
            providerUri);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(imageUri, request.RequestUri);
        Assert.Equal(providerUri, request.Headers.Referrer);
        Assert.Contains(
            request.Headers.UserAgent,
            static value => value.Product?.Name is "Chrome");
        Assert.Contains(
            request.Headers.Accept,
            static value => value.MediaType is "image/webp");
        Assert.Contains(
            request.Headers.Accept,
            static value => value.MediaType is "image/png");
        Assert.Contains(
            request.Headers.Accept,
            static value => value.MediaType is "image/jpeg");
    }

    [Fact]
    public void ArtworkPolicyResolvesOnlyItsOwnOrigin()
    {
        var providerUri = new Uri(
            "https://configured-provider.example/");
        IReadOnlyList<ApiContentArtworkPolicy> policies =
        [
            new(
                new Uri("https://image-cdn.example/some/path"),
                providerUri)
        ];

        Assert.Equal(
            providerUri,
            ContentSourceCatalogService.ResolveArtworkReferrer(
                "https://image-cdn.example/title/cover.webp",
                policies));
        Assert.Null(
            ContentSourceCatalogService.ResolveArtworkReferrer(
                "https://other.example/title/cover.webp",
                policies));
    }
}
