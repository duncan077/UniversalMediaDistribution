using UniversalMediaDistribution.Application.Metadata;

namespace UniversalMediaDistribution.Metadata;

public static class MetadataProviderFactory
{
    public static IReadOnlyList<ICatalogMetadataProvider> CreateDefault(
        HttpClient? myAnimeListHttpClient = null) =>
    [
        new TmdbMetadataProvider(),
        new MyAnimeListMetadataProvider(
            myAnimeListHttpClient ?? new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            })
    ];

    public static IReadOnlyList<ICatalogMetadataProvider>
        CreateBrowserCompatible() =>
    [
        new TmdbMetadataProvider()
    ];
}
