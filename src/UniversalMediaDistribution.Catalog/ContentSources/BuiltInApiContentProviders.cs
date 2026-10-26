namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>One lazy registry shared by the app and the headless server crawler.</summary>
public static class BuiltInApiContentProviders
{
    public static IReadOnlyList<ApiContentProviderRegistration> Create(
        HttpClient client, VideoHosterResolver? resolver = null) =>
    [
        new(BuiltInApiContentProviderDescriptors.UmdJson, () => new UmdJsonApiContentProvider(client)),
        new(BuiltInApiContentProviderDescriptors.LeerCapitulo, () => new LeerCapituloApiContentProvider(client), true),
        new(BuiltInApiContentProviderDescriptors.OlympusXyz, () => new OlympusXyzApiContentProvider(client), true),
        new(BuiltInApiContentProviderDescriptors.ManhwaWeb, () => new ManhwaWebApiContentProvider(client), true),
        new(BuiltInApiContentProviderDescriptors.ZonaTmo, () => new ZonaTmoApiContentProvider(client), true),
        new(BuiltInApiContentProviderDescriptors.CapibaraTraductor, () => new CapibaraTraductorApiContentProvider(client), true),
        new(BuiltInApiContentProviderDescriptors.UchuujinMangas, () => new UchuujinMangasApiContentProvider(client), true),
        new(BuiltInApiContentProviderDescriptors.AnimeAv1, () => new AnimeAv1ApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.FramexTv, () => new FramexTvApiContentProvider(client), true),
        new(BuiltInApiContentProviderDescriptors.AnimeTmo, () => new AnimeTmoApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.PelisPlus, () => new PelisPlusApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.Doramasflix, () => new DoramasflixApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.PelisPlusTo, () => new PelisPlusToApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.PelisflixHd, () => new PelisflixHdApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.PoseidonHd2, () => new PoseidonHd2ApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.SeriesFlix, () => new SeriesFlixApiContentProvider(client, resolver), true),
        new(BuiltInApiContentProviderDescriptors.JkAnime, () => new JkAnimeApiContentProvider(client, hosterResolver: resolver), true)
    ];
}
